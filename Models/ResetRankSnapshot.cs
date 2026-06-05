namespace UmbrellaRanked.Models;

public sealed record ResetRankSnapshot(
    string SteamId,
    string Name,
    int PlaytimeSeconds,
    int ResetUnixTime);
