using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Logging;

namespace UmbrellaRanked.Data;

internal static class SchemaInitializer
{
    public static async Task InitializeAsync(
        DbConnection connection,
        SqlDialect dialect,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            dialect.CreatePlayerStatsTableSql,
            cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            dialect.CreateWeaponStatsTableSql,
            cancellationToken: cancellationToken));

        await EnsureColumnAsync(connection, dialect, "ur_cs2_player_stats", "last_seen", "INT NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, dialect, "ur_cs2_player_stats", "last_reset", "INT NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, dialect, "ur_cs2_player_stats", "assists", "INT NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(connection, dialect, "ur_cs2_player_stats", "points", "INT NOT NULL DEFAULT 0", cancellationToken);

        await EnsureIndexAsync(connection, dialect, logger, "ur_cs2_player_stats", "idx_ur_cs2_player_stats_last_seen", "CREATE INDEX idx_ur_cs2_player_stats_last_seen ON ur_cs2_player_stats (last_seen)", cancellationToken);
        await EnsureIndexAsync(connection, dialect, logger, "ur_cs2_player_stats", "idx_ur_cs2_player_stats_points_top", "CREATE INDEX idx_ur_cs2_player_stats_points_top ON ur_cs2_player_stats (points, kills, assists, playtime)", cancellationToken);
        await EnsureIndexAsync(connection, dialect, logger, "ur_cs2_player_stats", "idx_ur_cs2_player_stats_kills_top", "CREATE INDEX idx_ur_cs2_player_stats_kills_top ON ur_cs2_player_stats (kills, assists, points, playtime)", cancellationToken);
        await EnsureIndexAsync(connection, dialect, logger, "ur_cs2_player_stats", "idx_ur_cs2_player_stats_playtime_top", "CREATE INDEX idx_ur_cs2_player_stats_playtime_top ON ur_cs2_player_stats (playtime, name)", cancellationToken);
        await EnsureIndexAsync(connection, dialect, logger, "ur_cs2_weapon_stats", "idx_ur_cs2_weapon_stats_weapon_kills_top", "CREATE INDEX idx_ur_cs2_weapon_stats_weapon_kills_top ON ur_cs2_weapon_stats (weapon, kills, steamid)", cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        DbConnection connection,
        SqlDialect dialect,
        string tableName,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        var columnExists = dialect.IsSqlite
            ? await SqliteColumnExistsAsync(connection, tableName, columnName, cancellationToken)
            : await MySqlColumnExistsAsync(connection, tableName, columnName, cancellationToken);

        if (columnExists)
        {
            return;
        }

        var sql = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition}";
        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    private static async Task EnsureIndexAsync(
        DbConnection connection,
        SqlDialect dialect,
        ILogger logger,
        string tableName,
        string indexName,
        string createIndexSql,
        CancellationToken cancellationToken)
    {
        var indexExists = dialect.IsSqlite
            ? await SqliteIndexExistsAsync(connection, tableName, indexName, cancellationToken)
            : await MySqlIndexExistsAsync(connection, tableName, indexName, cancellationToken);

        if (indexExists)
        {
            return;
        }

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(createIndexSql, cancellationToken: cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Could not create required Umbrella Ranked index {IndexName} on {TableName}.",
                indexName,
                tableName);
            throw;
        }
    }

    private static async Task<bool> MySqlColumnExistsAsync(
        DbConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*)
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME = @TableName
              AND COLUMN_NAME = @ColumnName;
            """;

        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            sql,
            new { TableName = tableName, ColumnName = columnName },
            cancellationToken: cancellationToken));

        return count > 0;
    }

    private static async Task<bool> SqliteColumnExistsAsync(
        DbConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        var sql = $"PRAGMA table_info({tableName})";
        var rows = await connection.QueryAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
        return rows.Any(row => string.Equals((string?)row.name, columnName, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> MySqlIndexExistsAsync(
        DbConnection connection,
        string tableName,
        string indexName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*)
            FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME = @TableName
              AND INDEX_NAME = @IndexName;
            """;

        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            sql,
            new { TableName = tableName, IndexName = indexName },
            cancellationToken: cancellationToken));

        return count > 0;
    }

    private static async Task<bool> SqliteIndexExistsAsync(
        DbConnection connection,
        string tableName,
        string indexName,
        CancellationToken cancellationToken)
    {
        var sql = $"PRAGMA index_list({tableName})";
        var rows = await connection.QueryAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
        return rows.Any(row => string.Equals((string?)row.name, indexName, StringComparison.OrdinalIgnoreCase));
    }
}
