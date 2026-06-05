namespace UmbrellaRanked.Models;

public sealed record PlayerIdentity(
    ulong SteamId64,
    string SteamId,
    string Name,
    int Slot,
    int? UserId);
