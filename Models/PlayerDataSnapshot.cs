namespace UmbrellaRanked.Models;

public sealed record PlayerDataSnapshot(
    string SteamId,
    string Name,
    int Kills,
    int Deaths,
    int Assists,
    int Points,
    int Headshots,
    int Mvps,
    int RoundsWon,
    int RoundsLost,
    int RoundsCt,
    int RoundsT,
    int MatchesWon,
    int MatchesLost,
    int MatchesTied,
    int PlaytimeSeconds,
    int LastSeenUnixTime,
    int LastResetUnixTime,
    IReadOnlyList<WeaponStatEntry> WeaponStats,
    long Version);
