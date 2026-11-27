using UmbrellaRanked.Models;
using UmbrellaRanked.Config;

namespace UmbrellaRanked.Data;

public interface IRankRepository : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<PlayerRankStats?> LoadPlayerStatsAsync(string steamId, CancellationToken cancellationToken);

    Task<IReadOnlyList<WeaponStatEntry>> LoadWeaponStatsAsync(string steamId, CancellationToken cancellationToken);

    Task SaveSnapshotAsync(PlayerDataSnapshot snapshot, CancellationToken cancellationToken);

    Task ResetRankAsync(ResetRankSnapshot snapshot, CancellationToken cancellationToken);

    Task<int?> GetRankPositionAsync(string steamId, int minimumKills, RankingMode rankingMode, int hideInactiveDays, CancellationToken cancellationToken);

    Task<IReadOnlyList<TopEntry>> GetTopPlayersAsync(int minimumKills, int limit, RankingMode rankingMode, int hideInactiveDays, CancellationToken cancellationToken);

    Task<IReadOnlyList<PlaytimeTopEntry>> GetTopPlaytimeAsync(int limit, int hideInactiveDays, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetTrackedWeaponsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<WeaponTopEntry>> GetTopWeaponAsync(string weapon, int limit, int hideInactiveDays, CancellationToken cancellationToken);

    Task<IReadOnlyList<WeaponTopEntry>> GetTopWeaponCategoryAsync(IReadOnlyCollection<string> weapons, int limit, int hideInactiveDays, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetTopSteamIdsAsync(int minimumKills, int limit, RankingMode rankingMode, int hideInactiveDays, string alwaysVisibleSteamId, CancellationToken cancellationToken);

    Task<IReadOnlyList<TeamkillTopEntry>> GetTopTeamkillsAsync(int minimumKills, int limit, int hideInactiveDays, CancellationToken cancellationToken);

    Task<int> PruneInactivePlayersAsync(int pruneDays, IReadOnlyCollection<string> excludedSteamIds, CancellationToken cancellationToken);
}
