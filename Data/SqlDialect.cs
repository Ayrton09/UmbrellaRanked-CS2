namespace UmbrellaRanked.Data;

internal sealed record SqlDialect(
    bool IsSqlite,
    string CreatePlayerStatsTableSql,
    string CreateWeaponStatsTableSql,
    string PlayerUpsertSql,
    string WeaponUpsertSql,
    string ResetPlayerUpsertSql);
