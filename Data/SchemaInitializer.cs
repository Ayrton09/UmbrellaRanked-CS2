using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace UmbrellaRanked.Data;

internal static class SchemaInitializer
{
    private static readonly string[] AddedIn110PlayerColumns =
    [
        "headshots",
        "mvps",
        "rounds_won",
        "rounds_lost",
        "rounds_ct",
        "rounds_t",
        "matches_won",
        "matches_lost",
        "matches_tied"
    ];

    public static async Task InitializeAsync(
        DbConnection connection,
        SqlDialect dialect,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Several servers can share one MySQL database and start together: the
        // check-then-alter steps below would race (errors 1060, 1061, 1213 deadlocks).
        var lockTaken = !dialect.IsSqlite && await TryTakeSchemaLockAsync(connection, logger, cancellationToken);

        try
        {
            await InitializeSchemaAsync(connection, dialect, logger, cancellationToken);
        }
        finally
        {
            if (lockTaken)
            {
                await ReleaseSchemaLockAsync(connection, logger);
            }
        }
    }

    internal static async Task RollbackQuietlyAsync(DbTransaction transaction, ILogger logger)
    {
        try
        {
            // Never the caller's token: on shutdown it is already cancelled.
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception rollbackException)
        {
            // Never let the rollback replace the exception that actually caused it.
            logger.LogDebug(rollbackException, "Rollback failed after an earlier error.");
        }
    }

    private static async Task<bool> TryTakeSchemaLockAsync(DbConnection connection, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            // Wait up to 30 s for the lock; the command timeout has to outlast that.
            var result = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT GET_LOCK('umbrellaranked_schema', 30);",
                commandTimeout: 45,
                cancellationToken: cancellationToken));

            if (result == 1)
            {
                return true;
            }

            logger.LogWarning("Could not take the schema lock within 30 s. Another server may still be updating the tables; continuing without it.");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not take the schema lock. Continuing without it.");
            return false;
        }
    }

    private static async Task ReleaseSchemaLockAsync(DbConnection connection, ILogger logger)
    {
        try
        {
            await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT RELEASE_LOCK('umbrellaranked_schema');",
                cancellationToken: CancellationToken.None));
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not release the schema lock (it is released when the connection closes).");
        }
    }

    private static async Task InitializeSchemaAsync(
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

        foreach (var columnName in AddedIn110PlayerColumns)
        {
            await EnsureColumnAsync(connection, dialect, "ur_cs2_player_stats", columnName, "INT NOT NULL DEFAULT 0", cancellationToken);
        }

        await EnsureColumnAsync(connection, dialect, "ur_cs2_player_stats", "teamkills", "INT NOT NULL DEFAULT 0", cancellationToken);

        await EnsureColumnAsync(connection, dialect, "ur_cs2_weapon_stats", "headshots", "INT NOT NULL DEFAULT 0", cancellationToken);

        await MigrateSteam2IdsAsync(connection, dialect, logger, cancellationToken);

        await EnsureIndexAsync(connection, dialect, logger, "ur_cs2_player_stats", "idx_ur_cs2_player_stats_last_seen", "CREATE INDEX idx_ur_cs2_player_stats_last_seen ON ur_cs2_player_stats (last_seen)", cancellationToken);
        await EnsureIndexAsync(connection, dialect, logger, "ur_cs2_player_stats", "idx_ur_cs2_player_stats_points_top", "CREATE INDEX idx_ur_cs2_player_stats_points_top ON ur_cs2_player_stats (points, kills, assists, playtime)", cancellationToken);
        await EnsureIndexAsync(connection, dialect, logger, "ur_cs2_player_stats", "idx_ur_cs2_player_stats_kills_top", "CREATE INDEX idx_ur_cs2_player_stats_kills_top ON ur_cs2_player_stats (kills, assists, points, playtime)", cancellationToken);
        await EnsureIndexAsync(connection, dialect, logger, "ur_cs2_player_stats", "idx_ur_cs2_player_stats_playtime_top", "CREATE INDEX idx_ur_cs2_player_stats_playtime_top ON ur_cs2_player_stats (playtime, name)", cancellationToken);
        await EnsureIndexAsync(connection, dialect, logger, "ur_cs2_weapon_stats", "idx_ur_cs2_weapon_stats_weapon_kills_top", "CREATE INDEX idx_ur_cs2_weapon_stats_weapon_kills_top ON ur_cs2_weapon_stats (weapon, kills, steamid)", cancellationToken);
    }

    /// <summary>
    /// Versions before 1.1.0 keyed players by Steam2 (<c>STEAM_1:Y:Z</c>). Web panels and
    /// other plugins use the SteamID64 (<c>76561197960265728 + Z * 2 + Y</c>), so rows still
    /// in the old format are rewritten in place, in one transaction.
    /// </summary>
    /// <remarks>
    /// A row whose SteamID64 already exists is left alone. That only happens when a server
    /// still on an older version shares the database and keeps writing Steam2 rows, and it
    /// writes absolute totals, so merging the two rows could count the same stats twice.
    /// </remarks>
    private static async Task MigrateSteam2IdsAsync(
        DbConnection connection,
        SqlDialect dialect,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var legacyFilter = dialect.IsSqlite
            ? "steamid GLOB 'STEAM_[0-5]:[01]:[0-9]*' AND SUBSTR(steamid, 11) NOT GLOB '*[^0-9]*'"
            : "steamid LIKE 'STEAM%' AND steamid REGEXP '^STEAM_[0-5]:[01]:[0-9]+$'";
        var steamId64Sql = dialect.IsSqlite
            ? "CAST(76561197960265728 + CAST(SUBSTR(steamid, 11) AS INTEGER) * 2 + CAST(SUBSTR(steamid, 9, 1) AS INTEGER) AS TEXT)"
            : "CAST(76561197960265728 + CAST(SUBSTR(steamid, 11) AS UNSIGNED) * 2 + CAST(SUBSTR(steamid, 9, 1) AS UNSIGNED) AS CHAR)";
        var updateIgnore = dialect.IsSqlite ? "UPDATE OR IGNORE" : "UPDATE IGNORE";

        var countLegacyPlayersSql = $"SELECT COUNT(*) FROM ur_cs2_player_stats WHERE {legacyFilter};";
        var countLegacyWeaponsSql = $"SELECT COUNT(*) FROM ur_cs2_weapon_stats WHERE {legacyFilter};";

        var legacyPlayers = await connection.ExecuteScalarAsync<int>(new CommandDefinition(countLegacyPlayersSql, cancellationToken: cancellationToken));
        var legacyWeapons = await connection.ExecuteScalarAsync<int>(new CommandDefinition(countLegacyWeaponsSql, cancellationToken: cancellationToken));
        if (legacyPlayers == 0 && legacyWeapons == 0)
        {
            return;
        }

        // The ignore variants skip a row whose new key already exists instead of failing
        // the whole statement. Weapon rows only move once their player row has moved, so a
        // player that stays on Steam2 keeps its weapon stats under the same key.
        var migratePlayersSql = $"{updateIgnore} ur_cs2_player_stats SET steamid = {steamId64Sql} WHERE {legacyFilter};";
        var migrateWeaponsSql = $"""
            {updateIgnore} ur_cs2_weapon_stats SET steamid = {steamId64Sql}
            WHERE {legacyFilter}
              AND steamid NOT IN (SELECT steamid FROM ur_cs2_player_stats);
            """;

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(migratePlayersSql, transaction: transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(migrateWeaponsSql, transaction: transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await RollbackQuietlyAsync(transaction, logger);
            throw;
        }

        // Counted from the table rather than from the UPDATE: MySqlConnector reports
        // matched rows by default, which includes the rows the IGNORE skipped.
        var remainingPlayers = await connection.ExecuteScalarAsync<int>(new CommandDefinition(countLegacyPlayersSql, cancellationToken: cancellationToken));
        if (legacyPlayers > remainingPlayers)
        {
            logger.LogInformation(
                "Migrated {Count} players from Steam2 IDs to SteamID64.",
                legacyPlayers - remainingPlayers);
        }

        if (remainingPlayers > 0)
        {
            logger.LogWarning(
                "{Count} players still use Steam2 IDs because a SteamID64 row for them already exists. " +
                "Another server on a version older than 1.1.0 is probably sharing this database; update every server that uses it.",
                remainingPlayers);
        }
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
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken));
        }
        catch (DbException exception) when (IsDuplicateObjectError(exception))
        {
            // Another server added it between the check and the ALTER.
            var existsNow = dialect.IsSqlite
                ? await SqliteColumnExistsAsync(connection, tableName, columnName, cancellationToken)
                : await MySqlColumnExistsAsync(connection, tableName, columnName, cancellationToken);
            if (!existsNow)
            {
                throw;
            }
        }
    }

    // MySQL 1060 duplicate column, 1061 duplicate key name; SQLite reports them by message.
    private static bool IsDuplicateObjectError(DbException exception)
    {
        if (exception is MySqlException mySqlException)
        {
            return mySqlException.Number is 1060 or 1061;
        }

        return exception.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase);
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
        catch (DbException exception) when (IsDuplicateObjectError(exception))
        {
            // Another server created it between the check and the CREATE INDEX.
            if (!await IndexExistsAsync(connection, dialect, tableName, indexName, cancellationToken))
            {
                logger.LogError(
                    exception,
                    "Could not create required Umbrella Ranked index {IndexName} on {TableName}.",
                    indexName,
                    tableName);
                throw;
            }
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

    private static Task<bool> IndexExistsAsync(
        DbConnection connection,
        SqlDialect dialect,
        string tableName,
        string indexName,
        CancellationToken cancellationToken)
    {
        return dialect.IsSqlite
            ? SqliteIndexExistsAsync(connection, tableName, indexName, cancellationToken)
            : MySqlIndexExistsAsync(connection, tableName, indexName, cancellationToken);
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
