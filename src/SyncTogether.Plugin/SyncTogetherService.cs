using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Services;

namespace SyncTogether.Plugin;

public sealed class SyncTogetherService : IService, IRequiresRequest
{
    private readonly ISessionManager _sessionManager;
    private readonly IAuthorizationContext _authorizationContext;

    public SyncTogetherService(
        ISessionManager sessionManager,
        IAuthorizationContext authorizationContext)
    {
        _sessionManager = sessionManager;
        _authorizationContext = authorizationContext;
    }

    public IRequest Request { get; set; } = null!;

    public object Get(GetSyncTogetherStatus request)
    {
        var auth = GetAuth();
        var sessions = _sessionManager.Sessions
            .Where(session => session.UserInternalId == auth.UserId)
            .OrderByDescending(session => session.LastActivityDate)
            .Select(MapSession)
            .ToArray();

        // Native party discovery is global to the server. Only return rooms that
        // already contain one of the caller's own sessions so Status cannot be
        // used to enumerate other users' private rooms or devices.
        var parties = _sessionManager.GetParties().Items
            .Where(party => party.Sessions.Any(session => session.UserInternalId == auth.UserId))
            .Select(MapParty)
            .ToArray();

        return new SyncTogetherStatusDto
        {
            PluginVersion = Plugin.Instance?.Version.ToString() ?? "0.4.1",
            Sessions = sessions,
            Parties = parties
        };
    }

    public object Post(CreateSyncTogetherParty request)
    {
        var auth = GetAuth();
        var session = GetOwnedSession(auth, request.SessionId);
        EnsureControllable(session);

        var name = NormalizeName(request.Name, auth.User?.Name ?? "Watch Party");
        var result = _sessionManager.CreateParty(session, name);

        return new SyncPartyResultDto { Party = MapParty(RequireParty(result)) };
    }

    public object Post(JoinSyncTogetherParty request)
    {
        var auth = GetAuth();
        var session = GetOwnedSession(auth, request.SessionId);
        EnsureControllable(session);

        if (string.IsNullOrWhiteSpace(request.PartyId))
        {
            throw new ArgumentException("PartyId is required.", nameof(request));
        }

        var partyId = request.PartyId.Trim();

        // A session belongs to one party at a time. A device that already
        // created its own room would otherwise stay there, leaving two
        // single-member rooms that can never relay to each other.
        if (!string.IsNullOrWhiteSpace(session.PartyId) &&
            !string.Equals(session.PartyId, partyId, StringComparison.OrdinalIgnoreCase))
        {
            _sessionManager.LeaveParty(session);
        }

        var result = _sessionManager.JoinParty(session, partyId);
        return new SyncPartyResultDto { Party = MapParty(RequireParty(result)) };
    }

    public object Post(LeaveSyncTogetherParty request)
    {
        var auth = GetAuth();
        var session = GetOwnedSession(auth, request.SessionId);
        _sessionManager.LeaveParty(session);

        return new { Success = true };
    }

    public async Task<SyncResyncResultDto> Post(ResyncSyncTogetherParty request)
    {
        var auth = GetAuth();
        var session = GetOwnedSession(auth, request.SessionId);
        EnsureControllable(session);

        var partyId = request.PartyId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(partyId) ||
            !string.Equals(session.PartyId, partyId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The selected session is not a member of this watch party.");
        }

        var synchronizer = PartyPlaybackSynchronizer.Current ??
            throw new InvalidOperationException("The playback synchronizer is not running.");
        var result = await synchronizer.ForceResynchronizeAsync(session)
            .ConfigureAwait(false);

        return new SyncResyncResultDto
        {
            Success = true,
            TargetCount = result.TargetCount,
            LeaderSessionId = result.LeaderSessionId,
            PositionTicks = result.PositionTicks
        };
    }

    private AuthorizationInfo GetAuth()
    {
        var auth = _authorizationContext.GetAuthorizationInfo(Request);
        if (auth.User == null || auth.UserId <= 0)
        {
            throw new UnauthorizedAccessException("A signed-in Emby user is required.");
        }

        return auth;
    }

    private SessionInfo GetOwnedSession(AuthorizationInfo auth, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("SessionId is required.", nameof(sessionId));
        }

        var session = _sessionManager.Sessions.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, sessionId.Trim(), StringComparison.OrdinalIgnoreCase));

        if (session == null)
        {
            throw new ArgumentException("The selected Emby session is no longer active.");
        }

        if (session.UserInternalId != auth.UserId)
        {
            throw new UnauthorizedAccessException(
                "A user may only manage their own playback sessions.");
        }

        return session;
    }

    private static void EnsureControllable(SessionInfo session)
    {
        if (!session.SupportsRemoteControl)
        {
            throw new InvalidOperationException(
                "The selected Emby client does not expose remote playback control.");
        }
    }

    private static string NormalizeName(string? value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value!.Trim();
        return normalized.Length <= 64 ? normalized : normalized.Substring(0, 64);
    }

    private static PartyInfo RequireParty(PartyInfoResult result)
    {
        return result.PartyInfo ?? throw new InvalidOperationException(
            "Emby's native party engine returned an empty result.");
    }

    private static SyncPartyDto MapParty(PartyInfo party)
    {
        return new SyncPartyDto
        {
            Id = party.Id ?? string.Empty,
            Name = party.Name ?? "Watch Party",
            Sessions = party.Sessions
                .Select(MapSession)
                .ToArray()
        };
    }

    private static SyncSessionDto MapSession(SessionInfo session)
    {
        return new SyncSessionDto
        {
            Id = session.Id ?? string.Empty,
            UserId = session.UserId ?? string.Empty,
            UserName = session.UserName ?? string.Empty,
            Client = session.Client ?? string.Empty,
            DeviceName = session.DeviceName ?? string.Empty,
            IsActive = session.IsActive,
            SupportsRemoteControl = session.SupportsRemoteControl,
            PartyId = session.PartyId ?? string.Empty,
            LastActivityDate = session.LastActivityDate,
            NowPlayingItemId = session.NowPlayingItem?.Id ?? string.Empty,
            NowPlayingName = session.NowPlayingItem?.Name ?? string.Empty,
            PositionTicks = session.PlayState?.PositionTicks ?? 0,
            IsPaused = session.PlayState?.IsPaused ?? false
        };
    }
}
