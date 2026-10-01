namespace UmbrellaRanked.Models;

public sealed class PlayerRankStats
{
    public string SteamId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Kills { get; set; }

    public int Deaths { get; set; }

    public int Assists { get; set; }

    public int Points { get; set; }

    public int Headshots { get; set; }

    public int Mvps { get; set; }

    public int RoundsWon { get; set; }

    public int RoundsLost { get; set; }

    public int RoundsCt { get; set; }

    public int RoundsT { get; set; }

    public int MatchesWon { get; set; }

    public int MatchesLost { get; set; }

    public int MatchesTied { get; set; }

    public int PlaytimeSeconds { get; set; }

    public int LastSeenUnixTime { get; set; }

    public int LastResetUnixTime { get; set; }

    public double Kda => Deaths > 0 ? (double)(Kills + Assists) / Deaths : Kills + Assists;

    public double HeadshotPercentage => Kills > 0 ? Headshots * 100.0 / Kills : 0;
}
