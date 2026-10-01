namespace UmbrellaRanked.Models;

public sealed class WeaponStatEntry
{
    public WeaponStatEntry()
    {
    }

    public WeaponStatEntry(string steamId, string weapon, int kills, int headshots)
    {
        SteamId = steamId;
        Weapon = weapon;
        Kills = kills;
        Headshots = headshots;
    }

    public string SteamId { get; set; } = string.Empty;

    public string Weapon { get; set; } = string.Empty;

    public int Kills { get; set; }

    public int Headshots { get; set; }
}
