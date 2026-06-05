using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace UmbrellaRanked.Config;

public sealed class UmbrellaRankedConfig : BasePluginConfig
{
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("DatabaseMode")]
    public DatabaseMode DatabaseMode { get; set; } = DatabaseMode.MySql;

    [JsonPropertyName("MinimumKillsRequired")]
    public int MinimumKillsRequired { get; set; } = 100;

    [JsonPropertyName("MinimumPlayersForStats")]
    public int MinimumPlayersForStats { get; set; } = 4;

    [JsonPropertyName("DisabledRankMapPatterns")]
    public List<string> DisabledRankMapPatterns { get; set; } =
    [
        "surf_*",
        "mg_*",
        "bhop_*",
        "jb_*",
        "dr_*",
        "deathrun_*"
    ];

    [JsonPropertyName("RankingMode")]
    public RankingMode RankingMode { get; set; } = RankingMode.Points;

    [JsonPropertyName("Points")]
    public PointsSettings Points { get; set; } = new();

    [JsonPropertyName("CommandCooldownSeconds")]
    public double CommandCooldownSeconds { get; set; } = 3.0;

    [JsonPropertyName("AutosaveIntervalSeconds")]
    public double AutosaveIntervalSeconds { get; set; } = 120.0;

    [JsonPropertyName("PruneInactiveDays")]
    public int PruneInactiveDays { get; set; } = 35;

    [JsonPropertyName("PruneOnStartup")]
    public bool PruneOnStartup { get; set; } = false;

    [JsonPropertyName("PruneCheckIntervalHours")]
    public double PruneCheckIntervalHours { get; set; } = 6.0;

    [JsonPropertyName("AllowResetRank")]
    public bool AllowResetRank { get; set; } = true;

    [JsonPropertyName("ResetRankCooldownDays")]
    public int ResetRankCooldownDays { get; set; } = 30;

    [JsonPropertyName("TopAnnouncementThreshold")]
    public int TopAnnouncementThreshold { get; set; } = 5;

    [JsonPropertyName("LeaderboardLimit")]
    public int LeaderboardLimit { get; set; } = 50;

    [JsonPropertyName("TopCacheSeconds")]
    public double TopCacheSeconds { get; set; } = 20.0;

    [JsonPropertyName("MySql")]
    public MySqlConnectionSettings MySql { get; set; } = new();

    [JsonPropertyName("Sqlite")]
    public SqliteConnectionSettings Sqlite { get; set; } = new();

    [JsonPropertyName("Top1Sound")]
    public Top1SoundSettings Top1Sound { get; set; } = new();

    public sealed class MySqlConnectionSettings
    {
        [JsonPropertyName("Host")]
        public string Host { get; set; } = "127.0.0.1";

        [JsonPropertyName("Port")]
        public uint Port { get; set; } = 3306;

        [JsonPropertyName("Database")]
        public string Database { get; set; } = "umbrella_ranked";

        [JsonPropertyName("Username")]
        public string Username { get; set; } = "root";

        [JsonPropertyName("Password")]
        public string Password { get; set; } = "change-me";

        [JsonPropertyName("ConnectionTimeoutSeconds")]
        public uint ConnectionTimeoutSeconds { get; set; } = 15;

        [JsonPropertyName("MinimumPoolSize")]
        public uint MinimumPoolSize { get; set; } = 0;

        [JsonPropertyName("MaximumPoolSize")]
        public uint MaximumPoolSize { get; set; } = 50;
    }

    public sealed class SqliteConnectionSettings
    {
        [JsonPropertyName("FilePath")]
        public string FilePath { get; set; } = "data/umbrella_ranked.sqlite";

        [JsonPropertyName("BusyTimeoutSeconds")]
        public int BusyTimeoutSeconds { get; set; } = 5;

        [JsonPropertyName("UseWriteAheadLogging")]
        public bool UseWriteAheadLogging { get; set; } = true;
    }

    public sealed class PointsSettings
    {
        [JsonPropertyName("Kill")]
        public int Kill { get; set; } = 2;

        [JsonPropertyName("HeadshotBonus")]
        public int HeadshotBonus { get; set; } = 1;

        [JsonPropertyName("KnifeKillBonus")]
        public int KnifeKillBonus { get; set; } = 3;

        [JsonPropertyName("TaserKillBonus")]
        public int TaserKillBonus { get; set; } = 2;

        [JsonPropertyName("Assist")]
        public int Assist { get; set; } = 1;

        [JsonPropertyName("DeathPenalty")]
        public int DeathPenalty { get; set; } = 2;

        [JsonPropertyName("SuicidePenalty")]
        public int SuicidePenalty { get; set; } = 3;

        [JsonPropertyName("TeamKillPenalty")]
        public int TeamKillPenalty { get; set; } = 5;

        [JsonPropertyName("Mvp")]
        public int Mvp { get; set; } = 1;

        [JsonPropertyName("BombPlant")]
        public int BombPlant { get; set; } = 2;

        [JsonPropertyName("BombDefuse")]
        public int BombDefuse { get; set; } = 3;

        [JsonPropertyName("BombExplode")]
        public int BombExplode { get; set; } = 3;

        [JsonPropertyName("HostageRescue")]
        public int HostageRescue { get; set; } = 3;

        [JsonPropertyName("TeamWin")]
        public int TeamWin { get; set; } = 1;

        [JsonPropertyName("TeamLossPenalty")]
        public int TeamLossPenalty { get; set; } = 1;
    }

    public sealed class Top1SoundSettings
    {
        [JsonPropertyName("PlaybackMode")]
        public SoundPlaybackMode PlaybackMode { get; set; } = SoundPlaybackMode.ClientCommand;

        [JsonPropertyName("Value")]
        public string Value { get; set; } = "sounds/training/bell_normal.vsnd_c";

        [JsonPropertyName("ResourcePath")]
        public string ResourcePath { get; set; } = string.Empty;

        [JsonPropertyName("Volume")]
        public float Volume { get; set; } = 0.3f;

        [JsonPropertyName("Pitch")]
        public float Pitch { get; set; } = 0.0f;
    }
}
