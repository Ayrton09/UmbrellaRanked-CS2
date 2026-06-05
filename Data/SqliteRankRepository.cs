using System.Data.Common;
using System.Reflection;
using System.Runtime.Loader;
using Dapper;
using Microsoft.Extensions.Logging;
using UmbrellaRanked.Config;

namespace UmbrellaRanked.Data;

internal sealed class SqliteRankRepository : DapperRankRepositoryBase
{
    private static readonly SqlDialect SqliteDialect = new(
        true,
        """
        CREATE TABLE IF NOT EXISTS ur_cs2_player_stats (
            steamid TEXT NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            kills INTEGER NOT NULL DEFAULT 0,
            deaths INTEGER NOT NULL DEFAULT 0,
            assists INTEGER NOT NULL DEFAULT 0,
            points INTEGER NOT NULL DEFAULT 0,
            playtime INTEGER NOT NULL DEFAULT 0,
            last_seen INTEGER NOT NULL DEFAULT 0,
            last_reset INTEGER NOT NULL DEFAULT 0
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS ur_cs2_weapon_stats (
            steamid TEXT NOT NULL,
            weapon TEXT NOT NULL,
            kills INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (steamid, weapon)
        );
        """,
        """
        INSERT INTO ur_cs2_player_stats (steamid, name, kills, deaths, assists, points, playtime, last_seen, last_reset)
        VALUES (@SteamId, @Name, @Kills, @Deaths, @Assists, @Points, @PlaytimeSeconds, @LastSeenUnixTime, @LastResetUnixTime)
        ON CONFLICT(steamid) DO UPDATE SET
            name = excluded.name,
            kills = excluded.kills,
            deaths = excluded.deaths,
            assists = excluded.assists,
            points = excluded.points,
            playtime = excluded.playtime,
            last_seen = excluded.last_seen,
            last_reset = excluded.last_reset;
        """,
        """
        INSERT INTO ur_cs2_weapon_stats (steamid, weapon, kills)
        VALUES (@SteamId, @Weapon, @Kills)
        ON CONFLICT(steamid, weapon) DO UPDATE SET
            kills = excluded.kills;
        """,
        """
        INSERT INTO ur_cs2_player_stats (steamid, name, kills, deaths, assists, points, playtime, last_seen, last_reset)
        VALUES (@SteamId, @Name, 0, 0, 0, 0, @PlaytimeSeconds, @ResetUnixTime, @ResetUnixTime)
        ON CONFLICT(steamid) DO UPDATE SET
            name = excluded.name,
            kills = 0,
            deaths = 0,
            assists = 0,
            points = 0,
            playtime = excluded.playtime,
            last_seen = excluded.last_seen,
            last_reset = excluded.last_reset;
        """);

    private readonly string _connectionString;
    private readonly int _busyTimeoutMilliseconds;
    private readonly bool _useWriteAheadLogging;
    private readonly Type _sqliteConnectionType;

    public SqliteRankRepository(
        UmbrellaRankedConfig.SqliteConnectionSettings settings,
        string absoluteFilePath,
        string sqliteDependencyDirectory,
        ILogger logger)
        : base(logger)
    {
        LoadSqliteAssembly(sqliteDependencyDirectory, "SQLitePCLRaw.core");
        LoadSqliteAssembly(sqliteDependencyDirectory, "SQLitePCLRaw.provider.e_sqlite3");
        var batteriesAssembly = LoadSqliteAssembly(sqliteDependencyDirectory, "SQLitePCLRaw.batteries_v2");
        var sqliteAssembly = LoadSqliteAssembly(sqliteDependencyDirectory, "Microsoft.Data.Sqlite");

        InitializeSqliteProvider(batteriesAssembly);
        _sqliteConnectionType = sqliteAssembly.GetType("Microsoft.Data.Sqlite.SqliteConnection", throwOnError: true)
            ?? throw new InvalidOperationException("Microsoft.Data.Sqlite.SqliteConnection could not be resolved.");

        var directoryPath = Path.GetDirectoryName(absoluteFilePath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        _busyTimeoutMilliseconds = Math.Max(0, settings.BusyTimeoutSeconds) * 1000;
        _useWriteAheadLogging = settings.UseWriteAheadLogging;
        var builder = new DbConnectionStringBuilder
        {
            ["Data Source"] = absoluteFilePath,
            ["Cache"] = "Shared",
            ["Mode"] = "ReadWriteCreate",
            ["Pooling"] = "True"
        };

        _connectionString = builder.ConnectionString;
    }

    protected override SqlDialect Dialect => SqliteDialect;

    protected override DbConnection CreateConnection()
    {
        return (DbConnection)(Activator.CreateInstance(_sqliteConnectionType, _connectionString)
            ?? throw new InvalidOperationException("Microsoft.Data.Sqlite.SqliteConnection could not be created."));
    }

    protected override async Task OnConnectionOpenedAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (_busyTimeoutMilliseconds > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                $"PRAGMA busy_timeout = {_busyTimeoutMilliseconds}",
                cancellationToken: cancellationToken));
        }

        if (_useWriteAheadLogging)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "PRAGMA journal_mode = WAL",
                cancellationToken: cancellationToken));
        }
    }

    private static Assembly LoadSqliteAssembly(string sqliteDependencyDirectory, string assemblyName)
    {
        var assemblyPath = Path.Combine(sqliteDependencyDirectory, $"{assemblyName}.dll");
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException($"SQLite dependency was not found: {assemblyPath}", assemblyPath);
        }

        var loadContext = AssemblyLoadContext.GetLoadContext(typeof(SqliteRankRepository).Assembly)
            ?? AssemblyLoadContext.Default;

        var loadedAssembly = loadContext.Assemblies.FirstOrDefault(assembly =>
            string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
        if (loadedAssembly != null)
        {
            return loadedAssembly;
        }

        return loadContext.LoadFromAssemblyPath(assemblyPath);
    }

    private static void InitializeSqliteProvider(Assembly batteriesAssembly)
    {
        var batteriesType = batteriesAssembly.GetType("SQLitePCL.Batteries_V2", throwOnError: true)
            ?? throw new InvalidOperationException("SQLitePCL.Batteries_V2 could not be resolved.");

        var initMethod = batteriesType.GetMethod("Init", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("SQLitePCL.Batteries_V2.Init could not be resolved.");

        initMethod.Invoke(null, null);
    }
}
