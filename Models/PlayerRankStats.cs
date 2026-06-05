namespace UmbrellaRanked.Models;

public sealed class PlayerRankStats
{
    public PlayerRankStats()
    {
    }

    public PlayerRankStats(
        string steamId,
        string name,
        int kills,
        int deaths,
        int assists,
        int points,
        int playtimeSeconds,
        int lastSeenUnixTime,
        int lastResetUnixTime)
    {
        SteamId = steamId;
        Name = name;
        Kills = kills;
        Deaths = deaths;
        Assists = assists;
        Points = points;
        PlaytimeSeconds = playtimeSeconds;
        LastSeenUnixTime = lastSeenUnixTime;
        LastResetUnixTime = lastResetUnixTime;
    }

    public string SteamId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Kills { get; set; }

    public int Deaths { get; set; }

    public int Assists { get; set; }

    public int Points { get; set; }

    public int PlaytimeSeconds { get; set; }

    public int LastSeenUnixTime { get; set; }

    public int LastResetUnixTime { get; set; }

    public double Kda => Deaths > 0 ? (double)(Kills + Assists) / Deaths : Kills + Assists;
}
