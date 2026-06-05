using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Logging;
using UmbrellaRanked.Config;
using UmbrellaRanked.Models;

namespace UmbrellaRanked.Data;

internal abstract class DapperRankRepositoryBase : IRankRepository
{
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
                COALESCE(kills, 0) AS Kills
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
            await transaction.RollbackAsync(cancellationToken);
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
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<int?> GetRankPositionAsync(string steamId, int minimumKills, RankingMode rankingMode, CancellationToken cancellationToken)
    {
        var comparisonSql = GetRankComparisonSql(rankingMode);
        var sql = $"""
            SELECT CASE
                WHEN me.kills >= @MinimumKills THEN 1 + (
                    SELECT COUNT(*)
                    FROM ur_cs2_player_stats other
                    WHERE other.kills >= @MinimumKills
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
            new { SteamId = steamId, MinimumKills = minimumKills },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<TopEntry>> GetTopPlayersAsync(int minimumKills, int limit, RankingMode rankingMode, CancellationToken cancellationToken)
    {
        var orderSql = GetRankOrderSql(rankingMode);
        var sql = $"""
            SELECT
                COALESCE(NULLIF(name, ''), 'Unknown') AS Name,
                COALESCE(kills, 0) AS Kills,
                COALESCE(deaths, 0) AS Deaths,
                COALESCE(assists, 0) AS Assists,
                COALESCE(points, 0) AS Points,
                ((COALESCE(kills, 0) + COALESCE(assists, 0)) * 1.0 / CASE WHEN COALESCE(deaths, 0) = 0 THEN 1 ELSE deaths END) AS Kda,
                COALESCE(playtime, 0) AS PlaytimeSeconds
            FROM ur_cs2_player_stats
            WHERE kills >= @MinimumKills
            ORDER BY {orderSql}
            LIMIT @Limit;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<TopEntry>(new CommandDefinition(
            sql,
            new { MinimumKills = minimumKills, Limit = limit },
            cancellationToken: cancellationToken));

        return rows.ToList();
    }

    public async Task<IReadOnlyList<PlaytimeTopEntry>> GetTopPlaytimeAsync(int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                COALESCE(NULLIF(name, ''), 'Unknown') AS Name,
                COALESCE(playtime, 0) AS PlaytimeSeconds
            FROM ur_cs2_player_stats
            ORDER BY playtime DESC, name ASC
            LIMIT @Limit;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<PlaytimeTopEntry>(new CommandDefinition(
            sql,
            new { Limit = limit },
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

    public async Task<IReadOnlyList<WeaponTopEntry>> GetTopWeaponAsync(string weapon, int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                COALESCE(NULLIF(p.name, ''), 'Unknown') AS Name,
                COALESCE(w.kills, 0) AS Kills
            FROM ur_cs2_weapon_stats w
            INNER JOIN ur_cs2_player_stats p ON w.steamid = p.steamid
            WHERE w.weapon = @Weapon
            ORDER BY w.kills DESC, p.name ASC
            LIMIT @Limit;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<WeaponTopEntry>(new CommandDefinition(
            sql,
            new { Weapon = weapon, Limit = limit },
            cancellationToken: cancellationToken));

        return rows.ToList();
    }

    public async Task<IReadOnlyList<WeaponTopEntry>> GetTopWeaponCategoryAsync(IReadOnlyCollection<string> weapons, int limit, CancellationToken cancellationToken)
    {
        if (weapons.Count == 0)
        {
            return Array.Empty<WeaponTopEntry>();
        }

        const string sql = """
            SELECT
                COALESCE(NULLIF(p.name, ''), 'Unknown') AS Name,
                SUM(COALESCE(w.kills, 0)) AS Kills
            FROM ur_cs2_weapon_stats w
            INNER JOIN ur_cs2_player_stats p ON w.steamid = p.steamid
            WHERE w.weapon IN @Weapons
            GROUP BY w.steamid, p.name
            HAVING SUM(COALESCE(w.kills, 0)) > 0
            ORDER BY SUM(COALESCE(w.kills, 0)) DESC, p.name ASC
            LIMIT @Limit;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<WeaponTopEntry>(new CommandDefinition(
            sql,
            new { Weapons = weapons.ToArray(), Limit = limit },
            cancellationToken: cancellationToken));

        return rows.ToList();
    }

    public async Task<IReadOnlyList<string>> GetTopSteamIdsAsync(int minimumKills, int limit, RankingMode rankingMode, CancellationToken cancellationToken)
    {
        var orderSql = GetRankOrderSql(rankingMode);
        var sql = $"""
            SELECT steamid
            FROM ur_cs2_player_stats
            WHERE kills >= @MinimumKills
            ORDER BY {orderSql}
            LIMIT @Limit;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            sql,
            new { MinimumKills = minimumKills, Limit = limit },
            cancellationToken: cancellationToken));

        return rows.ToList();
    }

    public async Task<int> PruneInactivePlayersAsync(int pruneDays, IReadOnlyCollection<string> excludedSteamIds, CancellationToken cancellationToken)
    {
        if (pruneDays <= 0)
        {
            return 0;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-pruneDays).ToUnixTimeSeconds();
        var filterSql = "last_seen > 0 AND last_seen < @Cutoff";

        object parameters;
        if (excludedSteamIds.Count > 0)
        {
            filterSql += " AND steamid NOT IN @ExcludedSteamIds";
            parameters = new { Cutoff = (int)cutoff, ExcludedSteamIds = excludedSteamIds.ToArray() };
        }
        else
        {
            parameters = new { Cutoff = (int)cutoff };
        }

        var deleteWeaponSql = $"""
            DELETE FROM ur_cs2_weapon_stats
            WHERE steamid IN (
                SELECT steamid
                FROM ur_cs2_player_stats
                WHERE {filterSql}
            );
            """;

        var deletePlayerSql = $"""
            DELETE FROM ur_cs2_player_stats
            WHERE {filterSql};
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                deleteWeaponSql,
                parameters,
                transaction,
                cancellationToken: cancellationToken));

            var deletedPlayers = await connection.ExecuteAsync(new CommandDefinition(
                deletePlayerSql,
                parameters,
                transaction,
                cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);
            return deletedPlayers;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
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
            _logger.LogError(exception, "Failed to open {Backend} rank repository connection.", Dialect.IsSqlite ? "SQLite" : "MySQL");
            await connection.DisposeAsync();
            throw;
        }
    }

    private static string GetRankOrderSql(RankingMode rankingMode)
    {
        return rankingMode == RankingMode.Points
            ? """
              points DESC,
              ((COALESCE(kills, 0) + COALESCE(assists, 0)) * 1.0 / CASE WHEN COALESCE(deaths, 0) = 0 THEN 1 ELSE deaths END) DESC,
              kills DESC,
              assists DESC,
              playtime DESC,
              name ASC
              """
            : """
              ((COALESCE(kills, 0) + COALESCE(assists, 0)) * 1.0 / CASE WHEN COALESCE(deaths, 0) = 0 THEN 1 ELSE deaths END) DESC,
              kills DESC,
              assists DESC,
              points DESC,
              playtime DESC,
              name ASC
              """;
    }

    private static string GetRankComparisonSql(RankingMode rankingMode)
    {
        var otherKda = "((COALESCE(other.kills, 0) + COALESCE(other.assists, 0)) * 1.0 / CASE WHEN COALESCE(other.deaths, 0) = 0 THEN 1 ELSE other.deaths END)";
        var meKda = "((COALESCE(me.kills, 0) + COALESCE(me.assists, 0)) * 1.0 / CASE WHEN COALESCE(me.deaths, 0) = 0 THEN 1 ELSE me.deaths END)";
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
