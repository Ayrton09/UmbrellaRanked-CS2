namespace UmbrellaRanked.Models;

public sealed class TopEntry
{
    public string Name { get; set; } = string.Empty;

    public int Kills { get; set; }

    public int Deaths { get; set; }

    public int Assists { get; set; }

    public int Points { get; set; }

    public double Kda { get; set; }

    public int PlaytimeSeconds { get; set; }
}
