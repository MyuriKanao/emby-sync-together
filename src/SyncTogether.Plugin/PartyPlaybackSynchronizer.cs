using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;

namespace SyncTogether.Plugin;

/// <summary>
/// Adds the playback relay that Emby's Party API does not provide by itself.
/// </summary>
public sealed class PartyPlaybackSynchronizer : IServerEntryPoint
{
    private const long SoftDriftTicks = TimeSpan.TicksPerMillisecond * 800;
    private const long HardDriftTicks = TimeSpan.TicksPerSecond * 2;
    private const int SoftDriftConfirmations = 2;
    private static readonly TimeSpan DriftCheckInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SuppressionLifetime = TimeSpan.FromSeconds(10);

    private readonly ISessionManager _sessionManager;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<string, PartyRelayState> _partyStates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _suppressions =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public static PartyPlaybackSynchronizer? Current { get; private set; }

    public PartyPlaybackSynchronizer(ISessionManager sessionManager, ILogger logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
        Current = this;
    }

    public async Task<int> ForceResynchronizeAsync(SessionInfo source)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PartyPlaybackSynchronizer));
        }

        if (!TryGetPartyId(source, out var partyId))
        {
            throw new InvalidOperationException("The selected session is not in a watch party.");
        }

        if (source.FullNowPlayingItem == null)
        {
            throw new InvalidOperationException(
                "The selected device is not currently playing anything.");
        }

        var state = _partyStates.GetOrAdd(partyId, _ => new PartyRelayState());
        await state.Gate.WaitAsync(_stopping.Token).ConfigureAwait(false);
        try
        {
            if (!string.Equals(source.PartyId, partyId, StringComparison.OrdinalIgnoreCase) ||
                source.FullNowPlayingItem == null)
            {
                throw new InvalidOperationException(
                    "The selected playback session changed before calibration completed.");
            }

            state.LeaderSessionId = source.Id;
            state.LastDriftCheck = DateTimeOffset.UtcNow;
            var positionTicks = source.PlayState.PositionTicks;
            var targets = GetPartySessions(partyId)
                .Where(target => !SameSession(source, target) && IsControllable(target))
                .ToArray();

            foreach (var target in targets)
            {
                state.DriftBreaches.TryRemove(target.Id, out _);
                if (target.FullNowPlayingItem?.InternalId == source.FullNowPlayingItem.InternalId)
                {
                    if (positionTicks.HasValue)
                    {
                        await SendPlaystateAsync(
                                source, target, PlaystateCommand.Seek, positionTicks,
                                RelayEvent.StateChange)
                            .ConfigureAwait(false);
                    }

                    await AlignPauseStateAsync(source, target, source.PlayState.IsPaused)
                        .ConfigureAwait(false);
                }
                else
                {
                    await BringTargetToPlaybackAsync(
                            source,
                            target,
                            source.FullNowPlayingItem,
                            positionTicks,
                            source.PlayState.MediaSourceId,
                            source.PlayState.IsPaused)
                        .ConfigureAwait(false);
                }
            }

            _logger.Info(
                "SyncTogether manually calibrated {0} target(s) from {1}",
                targets.Length, source.DeviceName);
            return targets.Length;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public void Run()
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _sessionManager.AddedToParty += OnAddedToParty;
        _sessionManager.RemovedFromParty += OnRemovedFromParty;
        _logger.Info("SyncTogether playback relay started", Array.Empty<object>());
    }

    private void OnPlaybackStart(object sender, PlaybackProgressEventArgs e)
    {
        if (!TryGetPartyId(e.Session, out var partyId) ||
            IsSuppressed(e.Session.Id, RelayEvent.Start) ||
            e.Item == null)
        {
            return;
        }

        var item = e.Item;
        var positionTicks = e.PlaybackPositionTicks ?? e.Session.PlayState.PositionTicks;
        var mediaSourceId = e.MediaSourceId ?? e.Session.PlayState.MediaSourceId;
        var paused = e.IsPaused || e.Session.PlayState.IsPaused;

        QueuePartyWork(partyId, state => RelayPlaybackAsync(
            partyId,
            state,
            e.Session,
            item,
            positionTicks,
            mediaSourceId,
            paused));
    }

    private void OnPlaybackProgress(object sender, PlaybackProgressEventArgs e)
    {
        if (!TryGetPartyId(e.Session, out var partyId))
        {
            return;
        }

        switch (e.EventName)
        {
            case ProgressEvent.Pause:
                if (!IsSuppressed(e.Session.Id, RelayEvent.Pause))
                {
                    QueuePartyWork(partyId, state => RelayPlaystateAsync(
                        partyId, state, e.Session, PlaystateCommand.Pause,
                        e.PlaybackPositionTicks));
                }
                break;

            case ProgressEvent.Unpause:
                if (!IsSuppressed(e.Session.Id, RelayEvent.Unpause))
                {
                    QueuePartyWork(partyId, state => RelayPlaystateAsync(
                        partyId, state, e.Session, PlaystateCommand.Unpause,
                        e.PlaybackPositionTicks));
                }
                break;

            case ProgressEvent.StateChange:
                if (!IsSuppressed(e.Session.Id, RelayEvent.StateChange) &&
                    e.PlaybackPositionTicks.HasValue)
                {
                    QueuePartyWork(partyId, state => RelayPlaystateAsync(
                        partyId, state, e.Session, PlaystateCommand.Seek,
                        e.PlaybackPositionTicks));
                }
                break;

            case ProgressEvent.TimeUpdate:
                QueuePartyWork(partyId, state => CorrectDriftAsync(
                    partyId, state, e.Session,
                    e.PlaybackPositionTicks ?? e.Session.PlayState.PositionTicks));
                break;
        }
    }

    private void OnPlaybackStopped(object sender, PlaybackStopEventArgs e)
    {
        if (e.IsAutomated || e.PlayedToCompletion ||
            !TryGetPartyId(e.Session, out var partyId) ||
            IsSuppressed(e.Session.Id, RelayEvent.Stop))
        {
            return;
        }

        QueuePartyWork(partyId, state => RelayStopAsync(partyId, state, e.Session));
    }

    private void OnAddedToParty(object sender, PartyEventArgs e)
    {
        var partyId = e.PartyInfo?.Id;
        var joinedSessionId = e.SessionInfo?.Id;
        if (string.IsNullOrWhiteSpace(partyId) || string.IsNullOrWhiteSpace(joinedSessionId))
        {
            return;
        }

        var currentPartyId = partyId!;
        var currentSessionId = joinedSessionId!;
        QueuePartyWork(currentPartyId, state => SynchronizeJoinedSessionAsync(
            currentPartyId, state, currentSessionId));
    }

    private void OnRemovedFromParty(object sender, PartyEventArgs e)
    {
        var partyId = e.PartyInfo?.Id;
        var removedSessionId = e.SessionInfo?.Id;
        if (string.IsNullOrWhiteSpace(partyId))
        {
            return;
        }

        var currentPartyId = partyId!;
        QueuePartyWork(currentPartyId, state =>
        {
            if (string.Equals(state.LeaderSessionId, removedSessionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                state.LeaderSessionId = string.Empty;
            }

            if (!GetPartySessions(currentPartyId).Any())
            {
                _partyStates.TryRemove(currentPartyId, out _);
            }

            return Task.CompletedTask;
        });
    }

    private async Task RelayPlaybackAsync(
        string partyId,
        PartyRelayState state,
        SessionInfo source,
        BaseItem item,
        long? positionTicks,
        string mediaSourceId,
        bool paused)
    {
        state.LeaderSessionId = source.Id;
        var targets = GetPartySessions(partyId)
            .Where(target => !SameSession(source, target))
            .ToArray();

        foreach (var target in targets)
        {
            await BringTargetToPlaybackAsync(
                    source, target, item, positionTicks, mediaSourceId, paused)
                .ConfigureAwait(false);
        }
    }

    private async Task SynchronizeJoinedSessionAsync(
        string partyId,
        PartyRelayState state,
        string joinedSessionId)
    {
        var sessions = GetPartySessions(partyId).ToArray();
        var target = sessions.FirstOrDefault(session =>
            string.Equals(session.Id, joinedSessionId, StringComparison.OrdinalIgnoreCase));
        if (!IsControllable(target))
        {
            return;
        }

        var source = sessions.FirstOrDefault(session =>
                         string.Equals(session.Id, state.LeaderSessionId,
                             StringComparison.OrdinalIgnoreCase) &&
                         session.FullNowPlayingItem != null) ??
                     sessions.FirstOrDefault(session =>
                         !SameSession(session, target) && session.FullNowPlayingItem != null);

        if (source?.FullNowPlayingItem == null)
        {
            return;
        }

        state.LeaderSessionId = source.Id;
        await BringTargetToPlaybackAsync(
                source,
                target!,
                source.FullNowPlayingItem,
                source.PlayState.PositionTicks,
                source.PlayState.MediaSourceId,
                source.PlayState.IsPaused)
            .ConfigureAwait(false);
    }

    private async Task BringTargetToPlaybackAsync(
        SessionInfo source,
        SessionInfo target,
        BaseItem item,
        long? positionTicks,
        string mediaSourceId,
        bool paused)
    {
        if (!IsControllable(target))
        {
            return;
        }

        if (target.FullNowPlayingItem?.InternalId == item.InternalId)
        {
            await CorrectTargetStateAsync(source, target, positionTicks, paused)
                .ConfigureAwait(false);
            return;
        }

        Suppress(target.Id, RelayEvent.Stop);
        Suppress(target.Id, RelayEvent.Start);
        Suppress(target.Id, RelayEvent.StateChange);
        Suppress(target.Id, RelayEvent.Unpause);

        var request = new PlayRequest
        {
            ItemIds = new[] { item.InternalId },
            StartPositionTicks = positionTicks,
            PlayCommand = PlayCommand.PlayNow,
            AudioStreamIndex = source.PlayState.AudioStreamIndex,
            SubtitleStreamIndex = source.PlayState.SubtitleStreamIndex,
            MediaSourceId = mediaSourceId
        };

        try
        {
            await _sessionManager.SendPlayCommand(
                    source.Id, target.Id, request, _stopping.Token)
                .ConfigureAwait(false);
            _logger.Info(
                "SyncTogether relayed Play from {0} to {1} at {2} ticks",
                source.DeviceName, target.DeviceName, positionTicks.GetValueOrDefault());

            if (paused)
            {
                await Task.Delay(600, _stopping.Token).ConfigureAwait(false);
                await SendPlaystateAsync(
                        source, target, PlaystateCommand.Pause, null, RelayEvent.Pause)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.ErrorException(
                "SyncTogether failed to relay Play to {0}", ex, target.DeviceName);
        }
    }

    private async Task RelayPlaystateAsync(
        string partyId,
        PartyRelayState state,
        SessionInfo source,
        PlaystateCommand command,
        long? positionTicks)
    {
        state.LeaderSessionId = source.Id;
        var targets = GetPartySessions(partyId)
            .Where(target => !SameSession(source, target))
            .ToArray();

        foreach (var target in targets)
        {
            if (!IsControllable(target))
            {
                continue;
            }

            if (source.FullNowPlayingItem != null &&
                target.FullNowPlayingItem?.InternalId != source.FullNowPlayingItem.InternalId)
            {
                await BringTargetToPlaybackAsync(
                        source,
                        target,
                        source.FullNowPlayingItem,
                        positionTicks ?? source.PlayState.PositionTicks,
                        source.PlayState.MediaSourceId,
                        command == PlaystateCommand.Pause || source.PlayState.IsPaused)
                    .ConfigureAwait(false);
                continue;
            }

            // Pause and resume are the in-player manual calibration gestures.
            // Seek peers first so the user never has to leave the player and
            // open the room page just to correct drift.
            if ((command == PlaystateCommand.Pause ||
                 command == PlaystateCommand.Unpause) &&
                positionTicks.HasValue)
            {
                await SendPlaystateAsync(
                        source, target, PlaystateCommand.Seek, positionTicks,
                        RelayEvent.StateChange)
                    .ConfigureAwait(false);
            }

            if (command == PlaystateCommand.Seek ||
                (command == PlaystateCommand.Pause && !target.PlayState.IsPaused) ||
                (command == PlaystateCommand.Unpause && target.PlayState.IsPaused))
            {
                var relayEvent = command == PlaystateCommand.Pause
                    ? RelayEvent.Pause
                    : command == PlaystateCommand.Unpause
                        ? RelayEvent.Unpause
                        : RelayEvent.StateChange;

                await SendPlaystateAsync(source, target, command, positionTicks, relayEvent)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task RelayStopAsync(
        string partyId,
        PartyRelayState state,
        SessionInfo source)
    {
        state.LeaderSessionId = source.Id;
        foreach (var target in GetPartySessions(partyId).Where(target => !SameSession(source, target)))
        {
            if (!IsControllable(target) || target.FullNowPlayingItem == null)
            {
                continue;
            }

            await SendPlaystateAsync(
                    source, target, PlaystateCommand.Stop, null, RelayEvent.Stop)
                .ConfigureAwait(false);
        }
    }

    private async Task CorrectDriftAsync(
        string partyId,
        PartyRelayState state,
        SessionInfo source,
        long? sourcePositionTicks)
    {
        if (string.IsNullOrWhiteSpace(state.LeaderSessionId))
        {
            state.LeaderSessionId = source.Id;
        }
        else if (!string.Equals(state.LeaderSessionId, source.Id,
                     StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - state.LastDriftCheck < DriftCheckInterval)
        {
            return;
        }

        state.LastDriftCheck = now;
        if (source.FullNowPlayingItem == null)
        {
            return;
        }

        foreach (var target in GetPartySessions(partyId).Where(target => !SameSession(source, target)))
        {
            if (!IsControllable(target))
            {
                continue;
            }

            if (target.FullNowPlayingItem?.InternalId != source.FullNowPlayingItem.InternalId)
            {
                state.DriftBreaches.TryRemove(target.Id, out _);
                await BringTargetToPlaybackAsync(
                        source,
                        target,
                        source.FullNowPlayingItem,
                        sourcePositionTicks,
                        source.PlayState.MediaSourceId,
                        source.PlayState.IsPaused)
                    .ConfigureAwait(false);
                continue;
            }

            await CorrectTargetDriftAsync(
                    state, source, target, sourcePositionTicks, source.PlayState.IsPaused)
                .ConfigureAwait(false);
        }
    }

    private async Task CorrectTargetDriftAsync(
        PartyRelayState state,
        SessionInfo source,
        SessionInfo target,
        long? sourcePositionTicks,
        bool sourcePaused)
    {
        if (sourcePositionTicks.HasValue)
        {
            var driftTicks = Math.Abs(
                target.PlayState.PositionTicks.GetValueOrDefault() - sourcePositionTicks.Value);
            var shouldSeek = false;

            if (driftTicks > HardDriftTicks)
            {
                shouldSeek = true;
                state.DriftBreaches.TryRemove(target.Id, out _);
            }
            else if (driftTicks > SoftDriftTicks)
            {
                var confirmations = state.DriftBreaches.AddOrUpdate(
                    target.Id, 1, (_, current) => current + 1);
                if (confirmations >= SoftDriftConfirmations)
                {
                    shouldSeek = true;
                    state.DriftBreaches.TryRemove(target.Id, out _);
                }
            }
            else
            {
                state.DriftBreaches.TryRemove(target.Id, out _);
            }

            if (shouldSeek)
            {
                await SendPlaystateAsync(
                        source, target, PlaystateCommand.Seek, sourcePositionTicks,
                        RelayEvent.StateChange)
                    .ConfigureAwait(false);
            }
        }

        await AlignPauseStateAsync(source, target, sourcePaused).ConfigureAwait(false);
    }

    private async Task CorrectTargetStateAsync(
        SessionInfo source,
        SessionInfo target,
        long? sourcePositionTicks,
        bool sourcePaused)
    {
        if (sourcePositionTicks.HasValue &&
            Math.Abs(target.PlayState.PositionTicks.GetValueOrDefault() - sourcePositionTicks.Value) >
            HardDriftTicks)
        {
            await SendPlaystateAsync(
                    source, target, PlaystateCommand.Seek, sourcePositionTicks,
                    RelayEvent.StateChange)
                .ConfigureAwait(false);
        }

        await AlignPauseStateAsync(source, target, sourcePaused).ConfigureAwait(false);
    }

    private async Task AlignPauseStateAsync(
        SessionInfo source,
        SessionInfo target,
        bool sourcePaused)
    {
        if (sourcePaused != target.PlayState.IsPaused)
        {
            var command = sourcePaused ? PlaystateCommand.Pause : PlaystateCommand.Unpause;
            var relayEvent = sourcePaused ? RelayEvent.Pause : RelayEvent.Unpause;
            await SendPlaystateAsync(source, target, command, null, relayEvent)
                .ConfigureAwait(false);
        }
    }

    private async Task SendPlaystateAsync(
        SessionInfo source,
        SessionInfo target,
        PlaystateCommand command,
        long? positionTicks,
        string relayEvent)
    {
        Suppress(target.Id, relayEvent);
        try
        {
            await _sessionManager.SendPlaystateCommand(
                    source.Id,
                    target.Id,
                    new PlaystateRequest
                    {
                        Command = command,
                        SeekPositionTicks = positionTicks
                    },
                    _stopping.Token)
                .ConfigureAwait(false);
            _logger.Debug(
                "SyncTogether relayed {0} from {1} to {2}",
                command, source.DeviceName, target.DeviceName);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.ErrorException(
                "SyncTogether failed to relay {0} to {1}",
                ex, command, target.DeviceName);
        }
    }

    private void QueuePartyWork(
        string partyId,
        Func<PartyRelayState, Task> work)
    {
        var state = _partyStates.GetOrAdd(partyId, _ => new PartyRelayState());
        _ = Task.Run(async () =>
        {
            var entered = false;
            try
            {
                await state.Gate.WaitAsync(_stopping.Token).ConfigureAwait(false);
                entered = true;
                await work(state).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.ErrorException(
                    "SyncTogether party relay failed for {0}", ex, partyId);
            }
            finally
            {
                if (entered)
                {
                    state.Gate.Release();
                }
            }
        });
    }

    private IEnumerable<SessionInfo> GetPartySessions(string partyId)
    {
        return _sessionManager.Sessions.Where(session =>
            string.Equals(session.PartyId, partyId, StringComparison.OrdinalIgnoreCase) &&
            session.IsActive);
    }

    private static bool TryGetPartyId(SessionInfo session, out string partyId)
    {
        partyId = session?.PartyId ?? string.Empty;
        return !string.IsNullOrWhiteSpace(partyId);
    }

    private static bool SameSession(SessionInfo left, SessionInfo right)
    {
        return string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsControllable(SessionInfo? session)
    {
        return session != null && session.IsActive && session.SupportsRemoteControl;
    }

    private void Suppress(string sessionId, string relayEvent)
    {
        _suppressions[SuppressionKey(sessionId, relayEvent)] =
            DateTime.UtcNow.Add(SuppressionLifetime).Ticks;
    }

    private bool IsSuppressed(string sessionId, string relayEvent)
    {
        var key = SuppressionKey(sessionId, relayEvent);
        if (!_suppressions.TryGetValue(key, out var expiryTicks))
        {
            return false;
        }

        _suppressions.TryRemove(key, out _);
        return expiryTicks >= DateTime.UtcNow.Ticks;
    }

    private static string SuppressionKey(string sessionId, string relayEvent)
    {
        return sessionId + ":" + relayEvent;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _sessionManager.AddedToParty -= OnAddedToParty;
        _sessionManager.RemovedFromParty -= OnRemovedFromParty;
        _stopping.Cancel();
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
        _logger.Info("SyncTogether playback relay stopped", Array.Empty<object>());
    }

    private sealed class PartyRelayState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public string LeaderSessionId { get; set; } = string.Empty;

        public DateTimeOffset LastDriftCheck { get; set; } = DateTimeOffset.MinValue;

        public ConcurrentDictionary<string, int> DriftBreaches { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private static class RelayEvent
    {
        public const string Start = "start";
        public const string Stop = "stop";
        public const string Pause = "pause";
        public const string Unpause = "unpause";
        public const string StateChange = "state";
    }
}
