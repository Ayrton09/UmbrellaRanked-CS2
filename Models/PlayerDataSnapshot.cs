namespace UmbrellaRanked.Models;

public sealed record PlayerDataSnapshot(
    string SteamId,
    string Name,
    int Kills,
    int Deaths,
    int Assists,
    int Points,
    int PlaytimeSeconds,
    int LastSeenUnixTime,
    int LastResetUnixTime,
    IReadOnlyList<WeaponStatEntry> WeaponStats,
    long Version);
