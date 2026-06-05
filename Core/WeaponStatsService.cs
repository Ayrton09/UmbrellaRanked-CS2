namespace UmbrellaRanked.Core;

public sealed class WeaponStatsService
{
    public string NormalizeWeaponName(string? rawWeapon)
    {
        var weapon = (rawWeapon ?? string.Empty).Trim();

        if (weapon.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase))
        {
            weapon = weapon[7..];
        }

        if (string.IsNullOrEmpty(weapon))
        {
            return "Unknown";
        }

        if (weapon.Contains("knife", StringComparison.OrdinalIgnoreCase) ||
            weapon.Contains("bayonet", StringComparison.OrdinalIgnoreCase))
        {
            return "Knife";
        }

        return weapon.ToLowerInvariant() switch
        {
            "m4a1_silencer" => "M4A1-S",
            "m4a1_silencer_off" => "M4A1-S",
            "usp_silencer" => "USP-S",
            "usp_silencer_off" => "USP-S",
            "molotov" => "Molotov/Inc",
            "incgrenade" => "Molotov/Inc",
            "hegrenade" => "HE Grenade",
            "flashbang" => "Flashbang",
            "smokegrenade" => "Smoke Grenade",
            "decoy" => "Decoy",
            "inferno" => "Fire",
            "taser" => "Zeus x27",
            "zeus" => "Zeus x27",
            "zeus_x27" => "Zeus x27",
            "world" => "World",
            _ => weapon
        };
    }
}
