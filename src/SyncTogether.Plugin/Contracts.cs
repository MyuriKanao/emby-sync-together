using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace SyncTogether.Plugin;

[Route("/SyncTogether/Status", "GET", Summary = "Gets watch-party status and eligible sessions")]
[Authenticated]
public sealed class GetSyncTogetherStatus
{
}

[Route("/SyncTogether/Parties", "POST", Summary = "Creates a native Emby party")]
[Authenticated]
public sealed class CreateSyncTogetherParty
{
    public string SessionId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

[Route("/SyncTogether/Parties/{PartyId}/Join", "POST", Summary = "Joins a native Emby party")]
[Authenticated]
public sealed class JoinSyncTogetherParty
{
    public string PartyId { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;
}

[Route("/SyncTogether/Leave", "POST", Summary = "Removes a session from its party")]
[Authenticated]
public sealed class LeaveSyncTogetherParty
{
    public string SessionId { get; set; } = string.Empty;
}

[Route("/SyncTogether/Parties/{PartyId}/Resync", "POST", Summary = "Immediately aligns a party to the selected session")]
[Authenticated]
public sealed class ResyncSyncTogetherParty
{
    public string PartyId { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;
}

public sealed class SyncTogetherStatusDto
{
    public string Engine { get; set; } = "emby-party-server-relay";

    public bool PlaybackSyncEnabled { get; set; } = true;

    public bool AutomaticDriftCorrection { get; set; } = true;

    public int SoftDriftThresholdMs { get; set; } = 800;

    public int HardDriftThresholdMs { get; set; } = 2000;

    public string PluginVersion { get; set; } = string.Empty;

    public IReadOnlyList<SyncSessionDto> Sessions { get; set; } = Array.Empty<SyncSessionDto>();

    public IReadOnlyList<SyncPartyDto> Parties { get; set; } = Array.Empty<SyncPartyDto>();
}

public sealed class SyncPartyResultDto
{
    public SyncPartyDto Party { get; set; } = new();
}

public sealed class SyncResyncResultDto
{
    public bool Success { get; set; }

    public int TargetCount { get; set; }

    public string LeaderSessionId { get; set; } = string.Empty;

    public long PositionTicks { get; set; }
}

public sealed class SyncPartyDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public IReadOnlyList<SyncSessionDto> Sessions { get; set; } = Array.Empty<SyncSessionDto>();
}

public sealed class SyncSessionDto
{
    public string Id { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public string UserName { get; set; } = string.Empty;

    public string Client { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public bool SupportsRemoteControl { get; set; }

    public string PartyId { get; set; } = string.Empty;

    public DateTimeOffset LastActivityDate { get; set; }

    public string NowPlayingItemId { get; set; } = string.Empty;

    public string NowPlayingName { get; set; } = string.Empty;

    public long PositionTicks { get; set; }

    public bool IsPaused { get; set; }
}
