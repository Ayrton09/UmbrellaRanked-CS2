using System.Net;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace UmbrellaRanked.Menus;

public sealed class WasdMenuService : IDisposable
{
    private const int MaxMenuLineCharacters = 42;

    private static readonly Dictionary<string, PlayerButtons> ButtonMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["W"] = PlayerButtons.Forward,
        ["S"] = PlayerButtons.Back,
        ["A"] = PlayerButtons.Moveleft,
        ["D"] = PlayerButtons.Moveright,
        ["E"] = PlayerButtons.Use,
        ["R"] = PlayerButtons.Reload
    };

    private readonly Dictionary<int, WasdMenuState> _states = new();
    private readonly Dictionary<int, float> _savedVelocity = new();
    private readonly int _itemsPerPage;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _initialInputDelay;
    private readonly TimeSpan _inputDebounce;
    private readonly TimeSpan _selectionCooldown;
    private readonly TimeSpan _renderInterval;

    public WasdMenuService(
        int itemsPerPage = 3,
        double timeoutSeconds = 45.0,
        int initialInputDelayMilliseconds = 800,
        int inputDebounceMilliseconds = 140,
        int selectionCooldownMilliseconds = 650,
        int renderIntervalMilliseconds = 0)
    {
        _itemsPerPage = Math.Clamp(itemsPerPage, 2, 4);
        _timeout = TimeSpan.FromSeconds(Math.Max(10.0, timeoutSeconds));
        _initialInputDelay = TimeSpan.FromMilliseconds(Math.Max(0, initialInputDelayMilliseconds));
        _inputDebounce = TimeSpan.FromMilliseconds(Math.Max(80, inputDebounceMilliseconds));
        _selectionCooldown = TimeSpan.FromMilliseconds(Math.Max(inputDebounceMilliseconds, selectionCooldownMilliseconds));
        _renderInterval = renderIntervalMilliseconds <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(Math.Max(16, renderIntervalMilliseconds));
    }

    public bool IsOpen(CCSPlayerController player)
    {
        return IsValidPlayer(player) && _states.TryGetValue(player.Slot, out var state) && state.UserId == player.UserId;
    }

    public bool IsContextOpen(CCSPlayerController player, string contextTag)
    {
        return IsValidPlayer(player) &&
               _states.TryGetValue(player.Slot, out var state) &&
               state.UserId == player.UserId &&
               string.Equals(state.CurrentPage?.ContextTag, contextTag, StringComparison.Ordinal);
    }

    public int ActiveMenuCount => _states.Count;

    public void Open(CCSPlayerController player, WasdMenuPage page)
    {
        if (!IsValidPlayer(player))
        {
            return;
        }

        Close(player);

        WasdMenuState state = new(player.Slot, player.UserId)
        {
            CurrentPage = page,
            SelectedIndex = FindFirstSelectableIndex(page),
            OpenedAtUtc = DateTimeOffset.UtcNow,
            LastInputUtc = DateTimeOffset.UtcNow,
            LastInteractionUtc = DateTimeOffset.UtcNow,
            PreviousButtons = player.Buttons
        };

        state.PageIndex = GetPageIndex(state.SelectedIndex);
        _states[player.Slot] = state;
        Freeze(player, page.FreezePlayer);
        Render(player, state, force: true);
    }

    public void Push(CCSPlayerController player, WasdMenuPage page)
    {
        if (!TryGetValidState(player, out var state) || state.CurrentPage == null)
        {
            Open(player, page);
            return;
        }

        OpenSubMenu(player, state, page);
    }

    public bool PushIfContext(CCSPlayerController player, string contextTag, WasdMenuPage page)
    {
        if (!TryGetValidState(player, out var state) ||
            state.CurrentPage == null ||
            !string.Equals(state.CurrentPage.ContextTag, contextTag, StringComparison.Ordinal))
        {
            return false;
        }

        OpenSubMenu(player, state, page);
        return true;
    }

    public void Close(CCSPlayerController player)
    {
        if (!IsValidPlayer(player))
        {
            return;
        }

        CloseInternal(player.Slot, player);
    }

    public void Close(int slot)
    {
        CloseInternal(slot, null);
    }

    public void CloseAll()
    {
        try
        {
            foreach (int slot in _states.Keys.ToArray())
            {
                CCSPlayerController? player = Utilities.GetPlayerFromSlot(slot);
                CloseInternal(slot, IsValidPlayer(player) ? player : null);
            }
        }
        catch
        {
            _states.Clear();
            _savedVelocity.Clear();
        }
    }

    public void CloseIfContext(CCSPlayerController player, string contextTag)
    {
        if (!IsContextOpen(player, contextTag))
        {
            return;
        }

        Close(player);
    }

    public void CloseByContext(string contextTag)
    {
        try
        {
            foreach (int slot in _states
                         .Where(entry => string.Equals(entry.Value.CurrentPage?.ContextTag, contextTag, StringComparison.Ordinal))
                         .Select(entry => entry.Key)
                         .ToArray())
            {
                CCSPlayerController? player = Utilities.GetPlayerFromSlot(slot);
                CloseInternal(slot, IsValidPlayer(player) ? player : null);
            }
        }
        catch
        {
            foreach (int slot in _states
                         .Where(entry => string.Equals(entry.Value.CurrentPage?.ContextTag, contextTag, StringComparison.Ordinal))
                         .Select(entry => entry.Key)
                         .ToArray())
            {
                HandleDisconnect(slot);
            }
        }
    }

    public void HandleDisconnect(int slot)
    {
        _states.Remove(slot);
        _savedVelocity.Remove(slot);
    }

    public void OnTick()
    {
        if (_states.Count == 0)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<CCSPlayerController> players;

        try
        {
            players = Utilities.GetPlayers().Where(p => p is { IsValid: true }).ToArray();
        }
        catch
        {
            return;
        }

        foreach (CCSPlayerController player in players)
        {
            if (!_states.TryGetValue(player.Slot, out var state))
            {
                continue;
            }

            if (!IsValidPlayer(player) || state.UserId != player.UserId)
            {
                CloseInternal(state.Slot, null);
                continue;
            }

            if (now - state.LastInteractionUtc > _timeout)
            {
                CloseInternal(state.Slot, player);
                continue;
            }

            HandleButtonInput(player, state, now);
            Render(player, state, force: false);
        }
    }

    public void Dispose()
    {
        CloseAll();
        _states.Clear();
        _savedVelocity.Clear();
    }

    private void OpenSubMenu(CCSPlayerController player, WasdMenuState state, WasdMenuPage page)
    {
        state.History.Push(state.CurrentPage!);
        state.CurrentPage = page;
        state.SelectedIndex = FindFirstSelectableIndex(page);
        state.PageIndex = GetPageIndex(state.SelectedIndex);
        state.OpenedAtUtc = DateTimeOffset.UtcNow;
        state.LastInputUtc = DateTimeOffset.UtcNow;
        state.LastInteractionUtc = DateTimeOffset.UtcNow;
        state.PreviousButtons = player.Buttons;
        state.IsSelecting = false;
        Freeze(player, page.FreezePlayer);
        Render(player, state, force: true);
    }

    private void CloseInternal(int slot, CCSPlayerController? player)
    {
        if (!_states.Remove(slot))
        {
            return;
        }

        if (player is { IsValid: true })
        {
            Unfreeze(player);
            TryPrintToCenterHtml(player, " ");
        }
        else
        {
            _savedVelocity.Remove(slot);
        }
    }

    private void HandleButtonInput(CCSPlayerController player, WasdMenuState state, DateTimeOffset now)
    {
        if (state.CurrentPage == null)
        {
            CloseInternal(state.Slot, player);
            return;
        }

        PlayerButtons current = player.Buttons;

        if (now - state.OpenedAtUtc < _initialInputDelay ||
            now - state.LastInputUtc < _inputDebounce)
        {
            state.PreviousButtons = current;
            return;
        }

        if (JustPressed(state.PreviousButtons, current, "R"))
        {
            NavigateBack(player, state);
            Touch(state, now);
        }
        else if (JustPressed(state.PreviousButtons, current, PlayerButtons.Jump | PlayerButtons.Duck | PlayerButtons.Cancel))
        {
            CloseInternal(state.Slot, player);
            state.PreviousButtons = current;
            return;
        }
        else if (JustPressed(state.PreviousButtons, current, "W"))
        {
            MoveSelection(state, -1);
            Touch(state, now);
            Render(player, state, force: true);
        }
        else if (JustPressed(state.PreviousButtons, current, "S"))
        {
            MoveSelection(state, 1);
            Touch(state, now);
            Render(player, state, force: true);
        }
        else if (JustPressed(state.PreviousButtons, current, "A"))
        {
            MovePage(state, -1);
            Touch(state, now);
            Render(player, state, force: true);
        }
        else if (JustPressed(state.PreviousButtons, current, "D"))
        {
            MovePage(state, 1);
            Touch(state, now);
            Render(player, state, force: true);
        }
        else if (JustPressed(state.PreviousButtons, current, "E"))
        {
            if (now - state.LastSelectUtc >= _selectionCooldown)
            {
                state.LastSelectUtc = now;
                Select(player, state);
                Touch(state, now);
            }
        }

        state.PreviousButtons = current;
    }

    private void MoveSelection(WasdMenuState state, int delta)
    {
        WasdMenuPage page = state.CurrentPage!;
        if (page.Items.Count == 0)
        {
            state.SelectedIndex = 0;
            state.PageIndex = 0;
            return;
        }

        int current = Math.Clamp(state.SelectedIndex, 0, page.Items.Count - 1);
        for (int attempt = 0; attempt < page.Items.Count; attempt++)
        {
            current = (current + delta + page.Items.Count) % page.Items.Count;
            if (page.Items[current].IsSelectable)
            {
                state.SelectedIndex = current;
                state.PageIndex = GetPageIndex(current);
                return;
            }
        }

        state.SelectedIndex = Math.Clamp(state.SelectedIndex, 0, page.Items.Count - 1);
        state.PageIndex = GetPageIndex(state.SelectedIndex);
    }

    private void MovePage(WasdMenuState state, int delta)
    {
        WasdMenuPage page = state.CurrentPage!;
        int totalPages = GetTotalPages(page);
        state.PageIndex = (state.PageIndex + delta + totalPages) % totalPages;

        int start = state.PageIndex * _itemsPerPage;
        int end = Math.Min(page.Items.Count, start + _itemsPerPage);
        for (int index = start; index < end; index++)
        {
            if (page.Items[index].IsSelectable)
            {
                state.SelectedIndex = index;
                return;
            }
        }

        state.SelectedIndex = Math.Clamp(start, 0, Math.Max(0, page.Items.Count - 1));
    }

    private void Select(CCSPlayerController player, WasdMenuState state)
    {
        WasdMenuPage page = state.CurrentPage!;
        if (state.IsSelecting || page.Items.Count == 0)
        {
            return;
        }

        int index = Math.Clamp(state.SelectedIndex, 0, page.Items.Count - 1);
        WasdMenuItem item = page.Items[index];
        if (!item.IsSelectable || item.OnSelected == null)
        {
            return;
        }

        state.IsSelecting = true;
        try
        {
            item.OnSelected(player);
        }
        finally
        {
            state.IsSelecting = false;
        }
    }

    private void NavigateBack(CCSPlayerController player, WasdMenuState state)
    {
        if (state.History.Count == 0)
        {
            CloseInternal(state.Slot, player);
            return;
        }

        state.CurrentPage = state.History.Pop();
        state.SelectedIndex = FindFirstSelectableIndex(state.CurrentPage);
        state.PageIndex = GetPageIndex(state.SelectedIndex);
        state.OpenedAtUtc = DateTimeOffset.UtcNow;
        state.PreviousButtons = player.Buttons;
        Freeze(player, state.CurrentPage.FreezePlayer);
        Render(player, state, force: true);
    }

    private void Render(CCSPlayerController player, WasdMenuState state, bool force)
    {
        WasdMenuPage? page = state.CurrentPage;
        if (page == null || !player.IsValid)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!force && _renderInterval > TimeSpan.Zero && now - state.LastRenderUtc < _renderInterval)
        {
            return;
        }

        state.LastRenderUtc = now;

        int totalPages = GetTotalPages(page);
        state.PageIndex = Math.Clamp(state.PageIndex, 0, totalPages - 1);
        int start = state.PageIndex * _itemsPerPage;
        List<WasdMenuItem> items = page.Items.Skip(start).Take(_itemsPerPage).ToList();

        List<string> lines = new()
        {
            $"<font color='#7dd3fc'><b>{Escape(CompactText(page.Title, 30))}</b></font> <font color='#9ca3af'>{state.PageIndex + 1}/{totalPages}</font>"
        };

        if (!string.IsNullOrWhiteSpace(page.Subtitle))
        {
            lines.Add($"<font color='#d1d5db'>{Escape(CompactText(page.Subtitle!, MaxMenuLineCharacters))}</font>");
        }

        if (items.Count == 0)
        {
            lines.Add($"<font color='#fbbf24'>{Escape(CompactText(page.EmptyText, MaxMenuLineCharacters))}</font>");
            lines.Add("<font color='#fcd34d'>Jump/Duck</font> close");
        }
        else
        {
            for (int index = 0; index < items.Count; index++)
            {
                int absoluteIndex = start + index;
                WasdMenuItem item = items[index];
                bool selected = absoluteIndex == state.SelectedIndex;
                string color = item.IsSelectable
                    ? selected ? "#a7f3d0" : "#f9fafb"
                    : "#9ca3af";
                string prefix = selected ? ">" : " ";
                lines.Add($"<font color='{color}'>{prefix} {Escape(CompactText(item.Text, MaxMenuLineCharacters))}</font>");
            }

            lines.Add("<font color='#fcd34d'>W/S</font> <font color='#fcd34d'>A/D</font> <font color='#fcd34d'>E</font> <font color='#fcd34d'>R</font>");
        }

        TryPrintToCenterHtml(player, string.Join("<br>", lines));
    }

    private void Freeze(CCSPlayerController player, bool freeze)
    {
        try
        {
            if (player.PlayerPawn?.Value == null)
            {
                return;
            }

            if (!freeze)
            {
                Unfreeze(player);
                return;
            }

            if (!_savedVelocity.ContainsKey(player.Slot))
            {
                _savedVelocity[player.Slot] = player.PlayerPawn.Value.VelocityModifier;
            }

            player.PlayerPawn.Value.VelocityModifier = 0f;
        }
        catch
        {
            _savedVelocity.Remove(player.Slot);
        }
    }

    private void Unfreeze(CCSPlayerController player)
    {
        try
        {
            if (!_savedVelocity.TryGetValue(player.Slot, out float velocity) || player.PlayerPawn?.Value == null)
            {
                _savedVelocity.Remove(player.Slot);
                return;
            }

            player.PlayerPawn.Value.VelocityModifier = velocity;
            _savedVelocity.Remove(player.Slot);
        }
        catch
        {
            _savedVelocity.Remove(player.Slot);
        }
    }

    private bool TryGetValidState(CCSPlayerController player, out WasdMenuState state)
    {
        if (IsValidPlayer(player) &&
            _states.TryGetValue(player.Slot, out state!) &&
            state.UserId == player.UserId)
        {
            return true;
        }

        state = null!;
        return false;
    }

    private int GetTotalPages(WasdMenuPage page)
    {
        return Math.Max(1, (int)Math.Ceiling(page.Items.Count / (double)_itemsPerPage));
    }

    private int GetPageIndex(int selectedIndex)
    {
        return Math.Max(0, selectedIndex / _itemsPerPage);
    }

    private static int FindFirstSelectableIndex(WasdMenuPage page)
    {
        for (int index = 0; index < page.Items.Count; index++)
        {
            if (page.Items[index].IsSelectable)
            {
                return index;
            }
        }

        return 0;
    }

    private static void Touch(WasdMenuState state, DateTimeOffset now)
    {
        state.LastInputUtc = now;
        state.LastInteractionUtc = now;
    }

    private static string Escape(string value)
    {
        return WebUtility.HtmlEncode(value);
    }

    private static string CompactText(string value, int maxCharacters)
    {
        if (value.Length <= maxCharacters)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, Math.Max(0, maxCharacters - 3)), "...");
    }

    private static void TryPrintToCenterHtml(CCSPlayerController player, string html)
    {
        try
        {
            player.PrintToCenterHtml(html);
        }
        catch
        {
            // Center HTML can throw during plugin teardown or very early server lifecycle.
        }
    }

    private static bool JustPressed(PlayerButtons oldButtons, PlayerButtons newButtons, string key)
    {
        return ButtonMap.TryGetValue(key, out PlayerButtons button) && JustPressed(oldButtons, newButtons, button);
    }

    private static bool JustPressed(PlayerButtons oldButtons, PlayerButtons newButtons, PlayerButtons buttons)
    {
        return (newButtons & buttons) != 0 && (oldButtons & buttons) == 0;
    }

    private static bool IsValidPlayer(CCSPlayerController? player)
    {
        return player != null && player.IsValid && !player.IsBot;
    }

    private sealed class WasdMenuState
    {
        public WasdMenuState(int slot, int? userId)
        {
            Slot = slot;
            UserId = userId;
        }

        public int Slot { get; }

        public int? UserId { get; }

        public Stack<WasdMenuPage> History { get; } = new();

        public WasdMenuPage? CurrentPage { get; set; }

        public int SelectedIndex { get; set; }

        public int PageIndex { get; set; }

        public DateTimeOffset OpenedAtUtc { get; set; }

        public DateTimeOffset LastInputUtc { get; set; }

        public DateTimeOffset LastInteractionUtc { get; set; }

        public DateTimeOffset LastRenderUtc { get; set; }

        public DateTimeOffset LastSelectUtc { get; set; }

        public PlayerButtons PreviousButtons { get; set; }

        public bool IsSelecting { get; set; }
    }
}
