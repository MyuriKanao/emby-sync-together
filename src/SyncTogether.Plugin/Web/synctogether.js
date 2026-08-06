define([
    'baseView',
    'loading',
    'emby-input',
    'emby-button',
    'emby-scroller'
], function (BaseView, loading) {
    'use strict';

    function api(path, method, body) {
        var options = {
            type: method || 'GET',
            url: ApiClient.getUrl(path),
            dataType: 'json'
        };

        if (body !== undefined && body !== null) {
            options.data = JSON.stringify(body);
            options.contentType = 'application/json';
        }

        return ApiClient.ajax(options);
    }

    function errorMessage(error) {
        if (!error) return '请求失败，请稍后重试。';
        if (typeof error === 'string') return error;
        if (error.ResponseStatus && error.ResponseStatus.Message) return error.ResponseStatus.Message;
        if (error.responseJSON && error.responseJSON.ResponseStatus) return error.responseJSON.ResponseStatus.Message;
        return error.message || error.statusText || '请求失败，请稍后重试。';
    }

    function View(view, params) {
        BaseView.apply(this, arguments);

        this.status = null;
        this.selectedSessionId = '';
        this.refreshTimer = null;
        this.busy = false;

        view.querySelector('.st-create').addEventListener('click', this.createParty.bind(this));
        view.querySelector('.st-join').addEventListener('click', this.joinParty.bind(this));
        view.querySelector('.st-refresh').addEventListener('click', this.refresh.bind(this, true));
        view.querySelector('.st-party-id').addEventListener('keydown', function (event) {
            if (event.key === 'Enter') {
                event.preventDefault();
                this.joinParty();
            }
        }.bind(this));
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function () {
        BaseView.prototype.onResume.apply(this, arguments);
        this.readInviteFromUrl();
        this.refresh(true);
        this.stopPolling();
        this.refreshTimer = setInterval(this.refresh.bind(this, false), 10000);
    };

    View.prototype.onPause = function () {
        this.stopPolling();
        if (BaseView.prototype.onPause) BaseView.prototype.onPause.apply(this, arguments);
    };

    View.prototype.stopPolling = function () {
        if (this.refreshTimer) {
            clearInterval(this.refreshTimer);
            this.refreshTimer = null;
        }
    };

    View.prototype.readInviteFromUrl = function () {
        var match = String(location.href).match(/[?&]syncParty=([^&#]+)/i);
        if (match) this.view.querySelector('.st-party-id').value = decodeURIComponent(match[1]);
    };

    View.prototype.setBusy = function (busy) {
        this.busy = busy;
        var controls = this.view.querySelectorAll('button, input');
        for (var i = 0; i < controls.length; i++) controls[i].disabled = busy;
        this.updateActionState();
    };

    View.prototype.showNotice = function (message, kind) {
        var notice = this.view.querySelector('.st-notice');
        notice.textContent = message || '';
        if (message) notice.setAttribute('data-kind', kind || 'error');
        else notice.removeAttribute('data-kind');
    };

    View.prototype.refresh = function (showLoader, force) {
        var instance = this;
        if (instance.busy && !showLoader && !force) return Promise.resolve();
        if (showLoader) loading.show();

        return api('SyncTogether/Status').then(function (status) {
            instance.status = status || { Sessions: [], Parties: [] };
            var sessions = instance.status.Sessions || [];
            var selectedStillValid = sessions.some(function (session) {
                return session.Id === instance.selectedSessionId && session.SupportsRemoteControl;
            });
            if (!selectedStillValid) {
                var preferred = sessions.find(function (session) { return session.SupportsRemoteControl && session.IsActive; }) ||
                    sessions.find(function (session) { return session.SupportsRemoteControl; });
                instance.selectedSessionId = preferred ? preferred.Id : '';
            }
            instance.showNotice('', '');
            instance.render();
        }).catch(function (error) {
            instance.showNotice(errorMessage(error), 'error');
        }).finally(function () {
            if (showLoader) loading.hide();
        });
    };

    View.prototype.render = function () {
        this.renderDevices();
        this.renderParty();
        this.updateActionState();
    };

    View.prototype.updateActionState = function () {
        var hasSelection = Boolean(this.selectedSessionId);
        this.view.querySelector('.st-create').disabled = this.busy || !hasSelection;
        this.view.querySelector('.st-join').disabled = this.busy || !hasSelection;
        this.view.querySelector('.st-refresh').disabled = this.busy;
    };

    View.prototype.renderDevices = function () {
        var instance = this;
        var list = this.view.querySelector('.st-device-list');
        var sessions = (this.status && this.status.Sessions) || [];
        list.replaceChildren();

        if (!sessions.length) {
            var empty = document.createElement('div');
            empty.className = 'st-empty secondaryText';
            empty.textContent = '暂无活跃的 Emby 播放设备。请先打开一个 Emby 客户端，然后点击刷新。';
            list.appendChild(empty);
            return;
        }

        sessions.forEach(function (session) {
            var button = document.createElement('button');
            button.type = 'button';
            button.className = 'st-device';
            button.setAttribute('data-selected', String(session.Id === instance.selectedSessionId));
            button.disabled = !session.SupportsRemoteControl;

            var icon = document.createElement('span');
            icon.className = 'st-device-icon md-icon';
            var client = String(session.Client || '').toLowerCase();
            icon.textContent = client.indexOf('tv') >= 0 ? 'tv' : client.indexOf('android') >= 0 || client.indexOf('ios') >= 0 ? 'smartphone' : 'computer';

            var copy = document.createElement('span');
            copy.className = 'st-device-copy';
            var name = document.createElement('span');
            name.className = 'st-device-name';
            name.textContent = session.DeviceName || session.Client || 'Emby 设备';
            var meta = document.createElement('span');
            meta.className = 'st-device-meta secondaryText';
            meta.textContent = session.SupportsRemoteControl
                ? (session.Client || 'Emby') + (session.NowPlayingName ? ' · 正在播放 ' + session.NowPlayingName : ' · 在线')
                : (session.Client || 'Emby') + ' · 不支持远程控制';
            copy.appendChild(name);
            copy.appendChild(meta);

            var dot = document.createElement('span');
            dot.className = 'st-online';
            dot.setAttribute('data-online', String(Boolean(session.IsActive && session.SupportsRemoteControl)));

            button.appendChild(icon);
            button.appendChild(copy);
            button.appendChild(dot);
            button.addEventListener('click', function () {
                instance.selectedSessionId = session.Id;
                instance.render();
            });
            list.appendChild(button);
        });
    };

    View.prototype.renderParty = function () {
        var instance = this;
        var region = this.view.querySelector('.st-current');
        var sessions = (this.status && this.status.Sessions) || [];
        var parties = (this.status && this.status.Parties) || [];
        var session = sessions.find(function (item) { return item.Id === instance.selectedSessionId; });
        var party = session && session.PartyId
            ? parties.find(function (item) { return item.Id === session.PartyId; })
            : null;

        region.replaceChildren();
        this.view.querySelector('.st-create-card').style.display = party ? 'none' : '';
        this.view.querySelector('.st-join-card').style.display = party ? 'none' : '';
        if (!party) return;

        var card = document.createElement('section');
        card.className = 'st-card st-card--room';
        var title = document.createElement('h2');
        title.className = 'st-card-title';
        title.textContent = party.Name || '一起看房间';
        var description = document.createElement('p');
        description.className = 'st-card-copy secondaryText';
        description.textContent = String((party.Sessions || []).length || 1) + ' 台设备已加入 · 播放中暂停、继续或拖动进度会立即校准';
        var code = document.createElement('div');
        code.className = 'st-code';
        code.textContent = party.Id;
        code.setAttribute('aria-label', '房间口令');

        var actions = document.createElement('div');
        actions.className = 'st-actions';
        var resyncButton = document.createElement('button');
        resyncButton.type = 'button';
        resyncButton.setAttribute('is', 'emby-button');
        resyncButton.className = 'button-flat emby-button';
        resyncButton.innerHTML = '<span>备用校准</span>';
        resyncButton.title = '备用入口：以当前选中的设备为准，同步其他房间成员';
        resyncButton.addEventListener('click', function () { instance.resyncParty(party.Id); });
        var copyButton = document.createElement('button');
        copyButton.type = 'button';
        copyButton.setAttribute('is', 'emby-button');
        copyButton.className = 'raised st-primary emby-button';
        copyButton.innerHTML = '<span>复制房间口令</span>';
        copyButton.addEventListener('click', function () { instance.copyCode(party.Id, copyButton); });
        var leaveButton = document.createElement('button');
        leaveButton.type = 'button';
        leaveButton.setAttribute('is', 'emby-button');
        leaveButton.className = 'raised st-danger emby-button';
        leaveButton.innerHTML = '<span>离开房间</span>';
        leaveButton.addEventListener('click', instance.leaveParty.bind(instance));
        actions.appendChild(resyncButton);
        actions.appendChild(copyButton);
        actions.appendChild(leaveButton);

        card.appendChild(title);
        card.appendChild(description);
        card.appendChild(code);
        card.appendChild(actions);
        region.appendChild(card);
    };

    View.prototype.runAction = function (action, successMessage) {
        var instance = this;
        instance.showNotice('', '');
        instance.setBusy(true);
        loading.show();
        return action().then(function () {
            return instance.refresh(false, true);
        }).then(function () {
            instance.showNotice(successMessage, 'success');
        }).catch(function (error) {
            instance.showNotice(errorMessage(error), 'error');
        }).finally(function () {
            instance.setBusy(false);
            loading.hide();
        });
    };

    View.prototype.createParty = function () {
        var name = this.view.querySelector('.st-name').value.trim() || '一起看';
        var sessionId = this.selectedSessionId;
        return this.runAction(function () {
            return api('SyncTogether/Parties', 'POST', { SessionId: sessionId, Name: name });
        }, '房间已创建，可以复制口令邀请好友。');
    };

    View.prototype.joinParty = function () {
        var partyId = this.view.querySelector('.st-party-id').value.trim();
        if (!partyId) {
            this.showNotice('请输入房间口令。', 'error');
            return Promise.resolve();
        }
        var sessionId = this.selectedSessionId;
        return this.runAction(function () {
            return api('SyncTogether/Parties/' + encodeURIComponent(partyId) + '/Join', 'POST', { SessionId: sessionId });
        }, '已加入同步房间。');
    };

    View.prototype.leaveParty = function () {
        var sessionId = this.selectedSessionId;
        return this.runAction(function () {
            return api('SyncTogether/Leave', 'POST', { SessionId: sessionId });
        }, '已离开同步房间。');
    };

    View.prototype.resyncParty = function (partyId) {
        var sessionId = this.selectedSessionId;
        return this.runAction(function () {
            return api('SyncTogether/Parties/' + encodeURIComponent(partyId) + '/Resync', 'POST', {
                SessionId: sessionId
            });
        }, '校准完成：其他设备已同步到当前设备的播放状态。');
    };

    View.prototype.copyCode = function (partyId, button) {
        var instance = this;
        function copied() {
            var span = button.querySelector('span');
            var oldText = span.textContent;
            span.textContent = '已复制';
            instance.showNotice('房间口令已复制。', 'success');
            setTimeout(function () { span.textContent = oldText; }, 1400);
        }

        if (navigator.clipboard && navigator.clipboard.writeText && window.isSecureContext) {
            navigator.clipboard.writeText(partyId).then(copied).catch(function () { instance.legacyCopy(partyId, copied); });
        } else {
            instance.legacyCopy(partyId, copied);
        }
    };

    View.prototype.legacyCopy = function (text, onSuccess) {
        var field = document.createElement('textarea');
        field.value = text;
        field.setAttribute('readonly', '');
        field.style.cssText = 'position:fixed;left:-9999px;top:0';
        document.body.appendChild(field);
        field.select();
        var copied = document.execCommand('copy');
        field.remove();
        if (copied) onSuccess();
        else this.showNotice('复制失败，请手动选择房间口令。', 'error');
    };

    return View;
});
