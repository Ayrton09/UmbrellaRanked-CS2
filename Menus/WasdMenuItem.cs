using CounterStrikeSharp.API.Core;

namespace UmbrellaRanked.Menus;

public sealed class WasdMenuItem
{
    public WasdMenuItem(
        string text,
        Action<CCSPlayerController>? onSelected = null,
        bool isSelectable = true)
    {
        Text = text;
        OnSelected = onSelected;
        IsSelectable = isSelectable && onSelected != null;
    }

    public string Text { get; }

    public Action<CCSPlayerController>? OnSelected { get; }

    public bool IsSelectable { get; }
}
