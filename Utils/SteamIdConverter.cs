namespace UmbrellaRanked.Utils;

public static class SteamIdConverter
{
    public static string ToSteam2(ulong steamId64)
    {
        if (steamId64 == 0)
        {
            return string.Empty;
        }

        var accountId = steamId64 & 0xFFFFFFFF;
        var authServer = accountId % 2;
        var accountNumber = (accountId - authServer) / 2;

        return $"STEAM_1:{authServer}:{accountNumber}";
    }
}
