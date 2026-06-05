using System.Data.Common;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using UmbrellaRanked.Config;

namespace UmbrellaRanked.Data;

internal sealed class MySqlRankRepository : DapperRankRepositoryBase
{
    private static readonly SqlDialect MySqlDialect = new(
        false,
        """
        CREATE TABLE IF NOT EXISTS ur_cs2_player_stats (
            steamid VARCHAR(32) NOT NULL PRIMARY KEY,
            name VARCHAR(64) NOT NULL,
            kills INT NOT NULL DEFAULT 0,
            deaths INT NOT NULL DEFAULT 0,
            assists INT NOT NULL DEFAULT 0,
            points INT NOT NULL DEFAULT 0,
            playtime INT NOT NULL DEFAULT 0,
            last_seen INT NOT NULL DEFAULT 0,
            last_reset INT NOT NULL DEFAULT 0
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """,
        """
        CREATE TABLE IF NOT EXISTS ur_cs2_weapon_stats (
            steamid VARCHAR(32) NOT NULL,
            weapon VARCHAR(64) NOT NULL,
            kills INT NOT NULL DEFAULT 0,
            PRIMARY KEY (steamid, weapon)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """,
        """
        INSERT INTO ur_cs2_player_stats (steamid, name, kills, deaths, assists, points, playtime, last_seen, last_reset)
        VALUES (@SteamId, @Name, @Kills, @Deaths, @Assists, @Points, @PlaytimeSeconds, @LastSeenUnixTime, @LastResetUnixTime)
        ON DUPLICATE KEY UPDATE
            name = VALUES(name),
            kills = VALUES(kills),
            deaths = VALUES(deaths),
            assists = VALUES(assists),
            points = VALUES(points),
            playtime = VALUES(playtime),
            last_seen = VALUES(last_seen),
            last_reset = VALUES(last_reset);
        """,
        """
        INSERT INTO ur_cs2_weapon_stats (steamid, weapon, kills)
        VALUES (@SteamId, @Weapon, @Kills)
        ON DUPLICATE KEY UPDATE
            kills = VALUES(kills);
        """,
        """
        INSERT INTO ur_cs2_player_stats (steamid, name, kills, deaths, assists, points, playtime, last_seen, last_reset)
        VALUES (@SteamId, @Name, 0, 0, 0, 0, @PlaytimeSeconds, @ResetUnixTime, @ResetUnixTime)
        ON DUPLICATE KEY UPDATE
            name = VALUES(name),
            kills = 0,
            deaths = 0,
            assists = 0,
            points = 0,
            playtime = VALUES(playtime),
            last_seen = VALUES(last_seen),
            last_reset = VALUES(last_reset);
        """);

    private readonly string _connectionString;

    public MySqlRankRepository(UmbrellaRankedConfig.MySqlConnectionSettings settings, ILogger logger)
        : base(logger)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = settings.Host,
            Port = settings.Port,
            Database = settings.Database,
            UserID = settings.Username,
            Password = settings.Password,
            ConnectionTimeout = settings.ConnectionTimeoutSeconds,
            MinimumPoolSize = settings.MinimumPoolSize,
            MaximumPoolSize = settings.MaximumPoolSize,
            AllowUserVariables = true,
            AllowPublicKeyRetrieval = true,
            SslMode = MySqlSslMode.Preferred
        };

        _connectionString = builder.ConnectionString;
    }

    protected override SqlDialect Dialect => MySqlDialect;

    protected override DbConnection CreateConnection()
    {
        return new MySqlConnection(_connectionString);
    }
}
