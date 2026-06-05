namespace UmbrellaRanked.Models;

public sealed class WeaponStatEntry
{
    public WeaponStatEntry()
    {
    }

    public WeaponStatEntry(string steamId, string weapon, int kills)
    {
        SteamId = steamId;
        Weapon = weapon;
        Kills = kills;
    }

    public string SteamId { get; set; } = string.Empty;

    public string Weapon { get; set; } = string.Empty;

    public int Kills { get; set; }
}
