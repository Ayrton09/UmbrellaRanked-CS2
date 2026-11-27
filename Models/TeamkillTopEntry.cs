namespace UmbrellaRanked.Models;

public sealed class TeamkillTopEntry
{
    public string Name { get; set; } = string.Empty;

    public int Kills { get; set; }

    public int Teamkills { get; set; }

    public double TeamkillPercentage => Kills > 0 ? Teamkills * 100.0 / Kills : 0;
}
