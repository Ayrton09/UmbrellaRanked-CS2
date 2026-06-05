using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using Microsoft.Extensions.Localization;

namespace UmbrellaRanked.Core;

public sealed class PlaytimeService
{
    private readonly IStringLocalizer _localizer;

    public PlaytimeService(IStringLocalizer localizer)
    {
        _localizer = localizer;
    }

    public string Format(CCSPlayerController? player, int totalSeconds)
    {
        var days = totalSeconds / 86400;
        var remaining = totalSeconds % 86400;
        var hours = remaining / 3600;
        var minutes = (remaining % 3600) / 60;

        return days > 0
            ? _localizer.ForPlayer(player, "time.format.days", days, hours, minutes)
            : _localizer.ForPlayer(player, "time.format", hours, minutes);
    }
}
