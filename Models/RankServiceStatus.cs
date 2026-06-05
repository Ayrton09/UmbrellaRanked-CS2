namespace UmbrellaRanked.Models;

public sealed record RankServiceStatus(
    int CachedLeaderboardCount,
    DateTimeOffset? LastCacheRefreshUtc);
