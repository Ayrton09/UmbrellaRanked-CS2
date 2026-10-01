using System.Globalization;

namespace UmbrellaRanked.Utils;

public static class SteamIdConverter
{
    /// <summary>
    /// The key players are stored under: the SteamID64 in decimal, the format web panels
    /// and other plugins use. Versions before 1.1.0 stored Steam2 (<c>STEAM_1:0:12345</c>);
    /// the schema initializer migrates those rows.
    /// </summary>
    public static string ToStorageId(ulong steamId64)
    {
        return steamId64 == 0
            ? string.Empty
            : steamId64.ToString(CultureInfo.InvariantCulture);
    }
}
