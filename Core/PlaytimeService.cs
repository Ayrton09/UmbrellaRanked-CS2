using CounterStrikeSharp.API.Core;

namespace UmbrellaRanked.Core;

public sealed class PlaytimeService
{
    private readonly Func<CCSPlayerController?, string, object[], string> _translate;

    public PlaytimeService(Func<CCSPlayerController?, string, object[], string> translate)
    {
        _translate = translate;
    }

    public string Format(CCSPlayerController? player, int totalSeconds)
    {
        var days = totalSeconds / 86400;
        var remaining = totalSeconds % 86400;
        var hours = remaining / 3600;
        var minutes = (remaining % 3600) / 60;

        return days > 0
            ? _translate(player, "time.format.days", [days, hours, minutes])
            : _translate(player, "time.format", [hours, minutes]);
    }
}
