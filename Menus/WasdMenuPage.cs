namespace UmbrellaRanked.Menus;

public sealed class WasdMenuPage
{
    public WasdMenuPage(
        string title,
        IEnumerable<WasdMenuItem> items,
        string? subtitle = null,
        string? emptyText = null,
        string? contextTag = null,
        bool freezePlayer = false)
    {
        Title = title;
        Subtitle = subtitle;
        EmptyText = emptyText ?? "No entries.";
        ContextTag = contextTag;
        FreezePlayer = freezePlayer;
        Items = items.ToList();
    }

    public string Title { get; }

    public string? Subtitle { get; }

    public string EmptyText { get; }

    public string? ContextTag { get; }

    public bool FreezePlayer { get; }

    public IReadOnlyList<WasdMenuItem> Items { get; }
}
