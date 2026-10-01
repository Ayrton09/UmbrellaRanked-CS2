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
            headshots INT NOT NULL DEFAULT 0,
            mvps INT NOT NULL DEFAULT 0,
            rounds_won INT NOT NULL DEFAULT 0,
            rounds_lost INT NOT NULL DEFAULT 0,
            rounds_ct INT NOT NULL DEFAULT 0,
            rounds_t INT NOT NULL DEFAULT 0,
            matches_won INT NOT NULL DEFAULT 0,
            matches_lost INT NOT NULL DEFAULT 0,
            matches_tied INT NOT NULL DEFAULT 0,
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
            headshots INT NOT NULL DEFAULT 0,
            PRIMARY KEY (steamid, weapon)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        """,
        """
        INSERT INTO ur_cs2_player_stats (steamid, name, kills, deaths, assists, points, headshots, mvps, rounds_won, rounds_lost, rounds_ct, rounds_t, matches_won, matches_lost, matches_tied, playtime, last_seen, last_reset)
        VALUES (@SteamId, @Name, @Kills, @Deaths, @Assists, @Points, @Headshots, @Mvps, @RoundsWon, @RoundsLost, @RoundsCt, @RoundsT, @MatchesWon, @MatchesLost, @MatchesTied, @PlaytimeSeconds, @LastSeenUnixTime, @LastResetUnixTime)
        ON DUPLICATE KEY UPDATE
            name = VALUES(name),
            kills = VALUES(kills),
            deaths = VALUES(deaths),
            assists = VALUES(assists),
            points = VALUES(points),
            headshots = VALUES(headshots),
            mvps = VALUES(mvps),
            rounds_won = VALUES(rounds_won),
            rounds_lost = VALUES(rounds_lost),
            rounds_ct = VALUES(rounds_ct),
            rounds_t = VALUES(rounds_t),
            matches_won = VALUES(matches_won),
            matches_lost = VALUES(matches_lost),
            matches_tied = VALUES(matches_tied),
            playtime = VALUES(playtime),
            last_seen = VALUES(last_seen),
            last_reset = VALUES(last_reset);
        """,
        """
        INSERT INTO ur_cs2_weapon_stats (steamid, weapon, kills, headshots)
        VALUES (@SteamId, @Weapon, @Kills, @Headshots)
        ON DUPLICATE KEY UPDATE
            kills = VALUES(kills),
            headshots = VALUES(headshots);
        """,
        """
        INSERT INTO ur_cs2_player_stats (steamid, name, kills, deaths, assists, points, headshots, mvps, rounds_won, rounds_lost, rounds_ct, rounds_t, matches_won, matches_lost, matches_tied, playtime, last_seen, last_reset)
        VALUES (@SteamId, @Name, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, @PlaytimeSeconds, @ResetUnixTime, @ResetUnixTime)
        ON DUPLICATE KEY UPDATE
            name = VALUES(name),
            kills = 0,
            deaths = 0,
            assists = 0,
            points = 0,
            headshots = 0,
            mvps = 0,
            rounds_won = 0,
            rounds_lost = 0,
            rounds_ct = 0,
            rounds_t = 0,
            matches_won = 0,
            matches_lost = 0,
            matches_tied = 0,
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

    protected override async ValueTask ClearConnectionPoolsAsync()
    {
        // MySqlConnector's pools are static, but this is the plugin's own private
        // copy of the assembly, so only this plugin's pools are affected.
        await MySqlConnection.ClearAllPoolsAsync(CancellationToken.None);
    }
}
