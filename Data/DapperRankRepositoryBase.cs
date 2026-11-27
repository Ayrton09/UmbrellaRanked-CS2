using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Logging;
using UmbrellaRanked.Config;
using UmbrellaRanked.Models;

namespace UmbrellaRanked.Data;

internal abstract class DapperRankRepositoryBase : IRankRepository
{
    // Not const so the test harness can shrink it; production always uses 5000.
    internal static int PruneBatchSize { get; set; } = 5000;

    // Test seam, null in production: runs between a batch SELECT and its DELETE.
    internal static Func<IReadOnlyList<string>, Task>? PruneAfterSelectHook { get; set; }

    private readonly ILogger _logger;

    protected DapperRankRepositoryBase(ILogger logger)
    {
        _logger = logger;
    }

    protected abstract SqlDialect Dialect { get; }

    protected abstract DbConnection CreateConnection();

    protected virtual Task OnConnectionOpenedAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await SchemaInitializer.InitializeAsync(connection, Dialect, _logger, cancellationToken);
    }

    public async Task<PlayerRankStats?> LoadPlayerStatsAsync(string steamId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                steamid AS SteamId,
                COALESCE(NULLIF(name, ''), 'Unknown') AS Name,
                COALESCE(kills, 0) AS Kills,
                COALESCE(deaths, 0) AS Deaths,
                COALESCE(assists, 0) AS Assists,
                COALESCE(points, 0) AS Points,
                COALESCE(headshots, 0) AS Headshots,
                COALESCE(mvps, 0) AS Mvps,
                COALESCE(rounds_won, 0) AS RoundsWon,
                COALESCE(rounds_lost, 0) AS RoundsLost,
                COALESCE(rounds_ct, 0) AS RoundsCt,
                COALESCE(rounds_t, 0) AS RoundsT,
                COALESCE(matches_won, 0) AS MatchesWon,
                COALESCE(matches_lost, 0) AS MatchesLost,
                COALESCE(matches_tied, 0) AS MatchesTied,
                COALESCE(teamkills, 0) AS Teamkills,
                COALESCE(playtime, 0) AS PlaytimeSeconds,
                COALESCE(last_seen, 0) AS LastSeenUnixTime,
                COALESCE(last_reset, 0) AS LastResetUnixTime
            FROM ur_cs2_player_stats
            WHERE steamid = @SteamId
            LIMIT 1;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<PlayerRankStats>(new CommandDefinition(
            sql,
            new { SteamId = steamId },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<WeaponStatEntry>> LoadWeaponStatsAsync(string steamId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                steamid AS SteamId,
                weapon AS Weapon,
                COALESCE(kills, 0) AS Kills,
                COALESCE(headshots, 0) AS Headshots
            FROM ur_cs2_weapon_stats
            WHERE steamid = @SteamId
            ORDER BY weapon ASC;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<WeaponStatEntry>(new CommandDefinition(
            sql,
            new { SteamId = steamId },
            cancellationToken: cancellationToken));

        return rows.ToList();
    }

    public async Task SaveSnapshotAsync(PlayerDataSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                Dialect.PlayerUpsertSql,
                snapshot,
                transaction,
                cancellationToken: cancellationToken));

            if (snapshot.WeaponStats.Count > 0)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    Dialect.WeaponUpsertSql,
                    snapshot.WeaponStats,
                    transaction,
                    cancellationToken: cancellationToken));
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await SchemaInitializer.RollbackQuietlyAsync(transaction, _logger);
            throw;
        }
    }

    public async Task ResetRankAsync(ResetRankSnapshot snapshot, CancellationToken cancellationToken)
    {
        const string deleteWeaponsSql = "DELETE FROM ur_cs2_weapon_stats WHERE steamid = @SteamId;";

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                deleteWeaponsSql,
                new { snapshot.SteamId },
                transaction,
                cancellationToken: cancellationToken));

            await connection.ExecuteAsync(new CommandDefinition(
                Dialect.ResetPlayerUpsertSql,
                snapshot,
                transaction,
                cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await SchemaInitializer.RollbackQuietlyAsync(transaction, _logger);
            throw;
        }
    }

    public async Task<int?> GetRankPositionAsync(string steamId, int minimumKills, RankingMode rankingMode, int hideInactiveDays, CancellationToken cancellationToken)
    {
        var comparisonSql = GetRankComparisonSql(rankingMode);
        var activeFilter = hideInactiveDays > 0 ? $"AND {GetActivePredicate("other.last_seen")}" : string.Empty;
        var sql = $"""
            SELECT CASE
                WHEN me.kills >= @MinimumKills THEN 1 + (
                    SELECT COUNT(*)
                    FROM ur_cs2_player_stats other
                    WHERE other.kills >= @MinimumKills
                      {activeFilter}
                      AND ({comparisonSql})
                )
                ELSE 0
            END AS rank_pos
            FROM ur_cs2_player_stats me
            WHERE me.steamid = @SteamId
            LIMIT 1;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            sql,
            new { SteamId = steamId, MinimumKills = minimumKills, ActiveCutoff = GetInactiveCutoff(hideInactiveDays) },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<TopEntry>> GetTopPlayersAsync(int minimumKills, int limit, RankingMode rankingMode, int hideInactiveDays, CancellationToken cancellationToken)
    {
        var orderSql = GetRankOrderSql(rankingMode);
        var activeFilter = hideInactiveDays > 0 ? $"AND {GetActivePredicate("last_seen")}" : string.Empty;
        var sql = $"""
            SELECT
                COALESCE(NULLIF(name, ''), 'Unknown') AS Name,
                COALESCE(kills, 0) AS Kills,
                COALESCE(deaths, 0) AS Deaths,
                COALESCE(assists, 0) AS Assists,
                COALESCE(points, 0) AS Points,
                ((COALESCE(kills, 0) + COALESCE(assists, 0)) * 1.0e0 / CASE WHEN COALESCE(deaths, 0) = 0 THEN 1 ELSE deaths END) AS Kda,
                COALESCE(playtime, 0) AS PlaytimeSeconds
            FROM ur_cs2_player_stats
            WHERE kills >= @MinimumKills
              {activeFilter}
            ORDER BY {orderSql}
            LIMIT @Limit;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<TopEntry>(new CommandDefinition(
            sql,
            new { MinimumKills = minimumKills, Limit = limit, ActiveCutoff = GetInactiveCutoff(hideInactiveDays) },
            cancellationToken: cancellationToken));

        return rows.ToList();
    }

    public async Task<IReadOnlyList<PlaytimeTopEntry>> GetTopPlaytimeAsync(int limit, int hideInactiveDays, CancellationToken cancellationToken)
    {
        var activeFilter = hideInactiveDays > 0 ? $"WHERE {GetActivePredicate("last_seen")}" : string.Empty;
        var sql = $"""
            SELECT
                COALESCE(NULLIF(name, ''), 'Unknown') AS Name,
                COALESCE(playtime, 0) AS PlaytimeSeconds
            FROM ur_cs2_player_stats
            {activeFilter}
            ORDER BY playtime DESC, name ASC
            LIMIT @Limit;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<PlaytimeTopEntry>(new CommandDefinition(
            sql,
            new { Limit = limit, ActiveCutoff = GetInactiveCutoff(hideInactiveDays) },
            cancellationToken: cancellationToken));

        return rows.ToList();
    }

    public async Task<IReadOnlyList<string>> GetTrackedWeaponsAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT DISTINCT weapon
            FROM ur_cs2_weapon_stats
            ORDER BY weapon ASC;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<string>(new CommandDefinition(sql, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<WeaponTopEntry>> GetTopWeaponAsync(string weapon, int limit, int hideInactiveDays, CancellationToken cancellationToken)
    {
        var activeFilter = hideInactiveDays > 0 ? $"AND {GetActivePredicate("p.last_seen")}" : string.Empty;
        var sql = $"""
            SELECT
                COALESCE(NULLIF(p.name, ''), 'Unknown') AS Name,
                COALESCE(w.kills, 0) AS Kills
            FROM ur_cs2_weapon_stats w
            INNER JOIN ur_cs2_player_stats p ON w.steamid = p.steamid
            WHERE w.weapon = @Weapon
              {activeFilter}
            ORDER BY w.kills DESC, p.name ASC
            LIMIT @Limit;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<WeaponTopEntry>(new CommandDefinition(
            sql,
            new { Weapon = weapon, Limit = limit, ActiveCutoff = GetInactiveCutoff(hideInactiveDays) },
            cancellationToken: cancellationToken));

        return rows.ToList();
    }

    public async Task<IReadOnlyList<WeaponTopEntry>> GetTopWeaponCategoryAsync(IReadOnlyCollection<string> weapons, int limit, int hideInactiveDays, CancellationToken cancellationToken)
    {
        if (weapons.Count == 0)
        {
            return Array.Empty<WeaponTopEntry>();
        }

        var activeFilter = hideInactiveDays > 0 ? $"AND {GetActivePredicate("p.last_seen")}" : string.Empty;
        var sql = $"""
            SELECT
                COALESCE(NULLIF(p.name, ''), 'Unknown') AS Name,
                SUM(COALESCE(w.kills, 0)) AS Kills
            FROM ur_cs2_weapon_stats w
            INNER JOIN ur_cs2_player_stats p ON w.steamid = p.steamid
            WHERE w.weapon IN @Weapons
              {activeFilter}
            GROUP BY w.steamid, p.name
            HAVING SUM(COALESCE(w.kills, 0)) > 0
            ORDER BY SUM(COALESCE(w.kills, 0)) DESC, p.name ASC
            LIMIT @Limit;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<WeaponTopEntry>(new CommandDefinition(
            sql,
            new { Weapons = weapons.ToArray(), Limit = limit, ActiveCutoff = GetInactiveCutoff(hideInactiveDays) },
            cancellationToken: cancellationToken));

        return rows.ToList();
    }

    public async Task<IReadOnlyList<string>> GetTopSteamIdsAsync(int minimumKills, int limit, RankingMode rankingMode, int hideInactiveDays, string alwaysVisibleSteamId, CancellationToken cancellationToken)
    {
        var orderSql = GetRankOrderSql(rankingMode);
        var activeFilter = hideInactiveDays > 0 ? "AND (last_seen <= 0 OR last_seen >= @ActiveCutoff OR steamid = @SteamId)" : string.Empty;
        var sql = $"""
            SELECT steamid
            FROM ur_cs2_player_stats
            WHERE kills >= @MinimumKills
              {activeFilter}
            ORDER BY {orderSql}
            LIMIT @Limit;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            sql,
            new { MinimumKills = minimumKills, Limit = limit, SteamId = alwaysVisibleSteamId, ActiveCutoff = GetInactiveCutoff(hideInactiveDays) },
            cancellationToken: cancellationToken));

        return rows.ToList();
    }

    public async Task<IReadOnlyList<TeamkillTopEntry>> GetTopTeamkillsAsync(int minimumKills, int limit, int hideInactiveDays, CancellationToken cancellationToken)
    {
        var activeFilter = hideInactiveDays > 0 ? $"AND {GetActivePredicate("last_seen")}" : string.Empty;
        var sql = $"""
            SELECT
                COALESCE(NULLIF(name, ''), 'Unknown') AS Name,
                COALESCE(kills, 0) AS Kills,
                COALESCE(teamkills, 0) AS Teamkills
            FROM ur_cs2_player_stats
            WHERE kills >= @MinimumKills
              AND teamkills > 0
              {activeFilter}
            ORDER BY (COALESCE(teamkills, 0) * 1.0e0 / CASE WHEN COALESCE(kills, 0) = 0 THEN 1 ELSE kills END) DESC,
                     teamkills DESC,
                     kills DESC,
                     name ASC
            LIMIT @Limit;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<TeamkillTopEntry>(new CommandDefinition(
            sql,
            new { MinimumKills = minimumKills, Limit = limit, ActiveCutoff = GetInactiveCutoff(hideInactiveDays) },
            cancellationToken: cancellationToken));

        return rows.ToList();
    }

    public async Task<int> PruneInactivePlayersAsync(int pruneDays, IReadOnlyCollection<string> excludedSteamIds, CancellationToken cancellationToken)
    {
        if (pruneDays <= 0)
        {
            return 0;
        }

        var cutoff = GetInactiveCutoff(pruneDays);
        if (cutoff <= 0)
        {
            // The cutoff is clamped to the epoch for absurdly large day counts: nothing is old enough.
            return 0;
        }

        var filterSql = "last_seen > 0 AND last_seen < @Cutoff";

        object parameters;
        if (excludedSteamIds.Count > 0)
        {
            filterSql += " AND steamid NOT IN @ExcludedSteamIds";
            parameters = new { Cutoff = cutoff, ExcludedSteamIds = excludedSteamIds.ToArray() };
        }
        else
        {
            parameters = new { Cutoff = cutoff };
        }

        var batchSize = PruneBatchSize;
        var selectBatchSql = $"SELECT steamid FROM ur_cs2_player_stats WHERE {filterSql} LIMIT {batchSize};";

        // The player DELETE repeats the filter: a player who came back between the SELECT
        // and the DELETE is no longer stale and must keep their row. Weapon rows only go
        // for the batch players whose player row is really gone.
        var deletePlayersSql = $"DELETE FROM ur_cs2_player_stats WHERE steamid IN @Batch AND {filterSql};";
        const string deleteWeaponsSql = """
            DELETE FROM ur_cs2_weapon_stats
            WHERE steamid IN @Batch
              AND steamid NOT IN (SELECT steamid FROM ur_cs2_player_stats WHERE steamid IN @Batch);
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);

        // Batches, one short transaction each: a huge backlog in a single statement
        // outlives the 30 s command timeout and would never complete.
        var totalDeleted = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = (await connection.QueryAsync<string>(new CommandDefinition(
                selectBatchSql,
                parameters,
                cancellationToken: cancellationToken))).ToArray();

            if (batch.Length == 0)
            {
                break;
            }

            if (PruneAfterSelectHook is { } hook)
            {
                await hook(batch);
            }

            var deleteParameters = new DynamicParameters(parameters);
            deleteParameters.Add("Batch", batch);

            int deletedPlayers;
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                try
                {
                    deletedPlayers = await connection.ExecuteAsync(new CommandDefinition(
                        deletePlayersSql,
                        deleteParameters,
                        transaction,
                        cancellationToken: cancellationToken));

                    await connection.ExecuteAsync(new CommandDefinition(
                        deleteWeaponsSql,
                        new { Batch = batch },
                        transaction,
                        cancellationToken: cancellationToken));

                    await transaction.CommitAsync(cancellationToken);
                }
                catch
                {
                    await SchemaInitializer.RollbackQuietlyAsync(transaction, _logger);
                    throw;
                }
            }

            totalDeleted += deletedPlayers;

            // A short batch is the last one; a batch that deleted nothing would loop forever.
            if (batch.Length < batchSize || deletedPlayers == 0)
            {
                break;
            }
        }

        return totalDeleted;
    }

    public async ValueTask DisposeAsync()
    {
        // Both backends pool connections. Without this the pooled handles outlive
        // the plugin: SQLite keeps the database file open and MySQL sockets pile up
        // on every reload. Clearing the pools closes them and releases the SQLite file;
        // the assembly load context itself may stay alive anyway, because
        // Microsoft.Data.Sqlite registers static handlers.
        try
        {
            await ClearConnectionPoolsAsync();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to clear the {Backend} connection pool during shutdown.",
                Dialect.IsSqlite ? "SQLite" : "MySQL");
        }

        GC.SuppressFinalize(this);
    }

    protected virtual ValueTask ClearConnectionPoolsAsync()
    {
        return ValueTask.CompletedTask;
    }

    // Same cutoff the prune uses, so a hidden player is exactly one that would be pruned.
    // Computed in 64 bits and clamped to the int range of the last_seen column: a huge
    // day count would otherwise wrap into the future (deleting everyone) or throw.
    // 0 means nothing is old enough, so prune deletes nothing and Hide hides nothing.
    private static int GetInactiveCutoff(int days)
    {
        if (days <= 0)
        {
            return 0;
        }

        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - days * 86400L;
        return (int)Math.Clamp(cutoff, 0L, int.MaxValue);
    }

    private static string GetActivePredicate(string column)
    {
        return $"({column} <= 0 OR {column} >= @ActiveCutoff)";
    }

    private async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = CreateConnection();

        try
        {
            await connection.OpenAsync(cancellationToken);
            await OnConnectionOpenedAsync(connection, cancellationToken);
            return connection;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await connection.DisposeAsync();
            throw;
        }
        catch (Exception exception)
        {
            // Debug only: every caller already logs the failure, and RankService logs it once.
            _logger.LogDebug(exception, "Failed to open {Backend} rank repository connection.", Dialect.IsSqlite ? "SQLite" : "MySQL");
            await connection.DisposeAsync();
            throw;
        }
    }

    private static string GetRankOrderSql(RankingMode rankingMode)
    {
        return rankingMode == RankingMode.Points
            ? """
              points DESC,
              ((COALESCE(kills, 0) + COALESCE(assists, 0)) * 1.0e0 / CASE WHEN COALESCE(deaths, 0) = 0 THEN 1 ELSE deaths END) DESC,
              kills DESC,
              assists DESC,
              playtime DESC,
              name ASC
              """
            : """
              ((COALESCE(kills, 0) + COALESCE(assists, 0)) * 1.0e0 / CASE WHEN COALESCE(deaths, 0) = 0 THEN 1 ELSE deaths END) DESC,
              kills DESC,
              assists DESC,
              points DESC,
              playtime DESC,
              name ASC
              """;
    }

    private static string GetRankComparisonSql(RankingMode rankingMode)
    {
        var otherKda = "((COALESCE(other.kills, 0) + COALESCE(other.assists, 0)) * 1.0e0 / CASE WHEN COALESCE(other.deaths, 0) = 0 THEN 1 ELSE other.deaths END)";
        var meKda = "((COALESCE(me.kills, 0) + COALESCE(me.assists, 0)) * 1.0e0 / CASE WHEN COALESCE(me.deaths, 0) = 0 THEN 1 ELSE me.deaths END)";
        var kdaThenStatsTieBreakers = $"""
            {otherKda} > {meKda}
            OR (
                {otherKda} = {meKda}
                AND (
                    COALESCE(other.kills, 0) > COALESCE(me.kills, 0)
                    OR (
                        COALESCE(other.kills, 0) = COALESCE(me.kills, 0)
                        AND (
                            COALESCE(other.assists, 0) > COALESCE(me.assists, 0)
                            OR (
                                COALESCE(other.assists, 0) = COALESCE(me.assists, 0)
                                AND (
                                    COALESCE(other.playtime, 0) > COALESCE(me.playtime, 0)
                                    OR (
                                        COALESCE(other.playtime, 0) = COALESCE(me.playtime, 0)
                                        AND COALESCE(NULLIF(other.name, ''), 'Unknown') < COALESCE(NULLIF(me.name, ''), 'Unknown')
                                    )
                                )
                            )
                        )
                    )
                )
            )
            """;

        return rankingMode == RankingMode.Points
            ? $"""
              COALESCE(other.points, 0) > COALESCE(me.points, 0)
              OR (
                  COALESCE(other.points, 0) = COALESCE(me.points, 0)
                  AND ({kdaThenStatsTieBreakers})
              )
              """
            : $"""
              {otherKda} > {meKda}
              OR (
                  {otherKda} = {meKda}
                  AND (
                      COALESCE(other.kills, 0) > COALESCE(me.kills, 0)
                      OR (
                          COALESCE(other.kills, 0) = COALESCE(me.kills, 0)
                          AND (
                              COALESCE(other.assists, 0) > COALESCE(me.assists, 0)
                              OR (
                                  COALESCE(other.assists, 0) = COALESCE(me.assists, 0)
                                  AND (
                                      COALESCE(other.points, 0) > COALESCE(me.points, 0)
                                      OR (
                                          COALESCE(other.points, 0) = COALESCE(me.points, 0)
                                          AND (
                                              COALESCE(other.playtime, 0) > COALESCE(me.playtime, 0)
                                              OR (
                                                  COALESCE(other.playtime, 0) = COALESCE(me.playtime, 0)
                                                  AND COALESCE(NULLIF(other.name, ''), 'Unknown') < COALESCE(NULLIF(me.name, ''), 'Unknown')
                                              )
                                          )
                                      )
                                  )
                              )
                          )
                      )
                  )
              )
              """;
    }
}
