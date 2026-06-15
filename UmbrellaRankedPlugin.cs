using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using UmbrellaRanked.Config;
using UmbrellaRanked.Core;
using UmbrellaRanked.Data;
using UmbrellaRanked.Menus;
using UmbrellaRanked.Models;
using UmbrellaRanked.Utils;
using CssTimer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace UmbrellaRanked;

[MinimumApiVersion(175)]
public sealed class UmbrellaRankedPlugin : BasePlugin, IPluginConfig<UmbrellaRankedConfig>
{
    private const int ConfigVersion = 1;
    private const int MinimumAllowedKillsRequired = 100;
    private const int MinimumAllowedPruneInactiveDays = 35;
    private const string TopPlayersMenuContext = "ranked.top.players";
    private const string TopTimeMenuContext = "ranked.top.time";
    private const string TopWeaponsMenuContext = "ranked.top.weapons";
    private const string ResetRankMenuContext = "ranked.reset";
    private static readonly IReadOnlyList<WeaponCategoryDefinition> WeaponCategoryDefinitions =
    [
        new("rifles", "weapon.category.rifles"),
        new("snipers", "weapon.category.snipers"),
        new("pistols", "weapon.category.pistols"),
        new("smgs", "weapon.category.smgs"),
        new("heavy", "weapon.category.heavy"),
        new("grenades", "weapon.category.grenades"),
        new("melee", "weapon.category.melee"),
        new("other", "weapon.category.other")
    ];

    private IRankRepository? _repository;
    private PlayerSessionService? _sessionService;
    private WeaponStatsService? _weaponStatsService;
    private CommandCooldownService? _commandCooldownService;
    private PlaytimeService? _playtimeService;
    private RankService? _rankService;
    private AutosaveService? _autosaveService;
    private WasdMenuService? _wasdMenuService;
    private CssTimer? _pruneTimer;
    private CancellationTokenSource _shutdown = new();
    private CancellationToken _shutdownToken;
    private bool _loadConnectedPlayersWhenGlobalsAreReady;
    private readonly Queue<Action> _mainThreadActions = new();
    private readonly object _mainThreadActionsLock = new();
    private string _currentMapName = string.Empty;
    private bool _isUnloading;

    public override string ModuleName => "Umbrella Ranked System";
    public override string ModuleVersion => "1.0.1";
    public override string ModuleAuthor => "Ayrton09";
    public override string ModuleDescription => string.Empty;

    public FakeConVar<bool> CompetitiveEnabledCvar { get; } = new("css_rank_enabled", string.Empty, true);

    public UmbrellaRankedConfig Config { get; set; } = new();

    public void OnConfigParsed(UmbrellaRankedConfig config)
    {
        config.Version = ConfigVersion;
        config.MySql ??= new UmbrellaRankedConfig.MySqlConnectionSettings();
        config.Sqlite ??= new UmbrellaRankedConfig.SqliteConnectionSettings();
        config.DisabledRankMapPatterns ??= [];
        config.Points ??= new UmbrellaRankedConfig.PointsSettings();
        config.Top1Sound ??= new UmbrellaRankedConfig.Top1SoundSettings();
        config.DisabledRankMapPatterns = config.DisabledRankMapPatterns
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(pattern => pattern.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (config.MinimumKillsRequired < MinimumAllowedKillsRequired)
        {
            Logger.LogWarning(
                "MinimumKillsRequired cannot be lower than {Minimum}. Clamping to {Minimum}.",
                MinimumAllowedKillsRequired,
                MinimumAllowedKillsRequired);
            config.MinimumKillsRequired = MinimumAllowedKillsRequired;
        }

        if (config.MinimumPlayersForStats < 1)
        {
            Logger.LogWarning("MinimumPlayersForStats cannot be lower than 1. Clamping to 1.");
            config.MinimumPlayersForStats = 1;
        }

        if (config.CommandCooldownSeconds < 0)
        {
            Logger.LogWarning("CommandCooldownSeconds cannot be negative. Clamping to 0.");
            config.CommandCooldownSeconds = 0;
        }

        if (config.AutosaveIntervalSeconds < 0)
        {
            Logger.LogWarning("AutosaveIntervalSeconds cannot be negative. Clamping to 0.");
            config.AutosaveIntervalSeconds = 0;
        }

        if (config.PruneInactiveDays < MinimumAllowedPruneInactiveDays)
        {
            Logger.LogWarning(
                "PruneInactiveDays cannot be lower than {Minimum}. Clamping to {Minimum}.",
                MinimumAllowedPruneInactiveDays,
                MinimumAllowedPruneInactiveDays);
            config.PruneInactiveDays = MinimumAllowedPruneInactiveDays;
        }

        if (config.PruneCheckIntervalHours < 0)
        {
            Logger.LogWarning("PruneCheckIntervalHours cannot be negative. Clamping to 0.");
            config.PruneCheckIntervalHours = 0;
        }

        if (config.ResetRankCooldownDays < 0)
        {
            Logger.LogWarning("ResetRankCooldownDays cannot be negative. Clamping to 0.");
            config.ResetRankCooldownDays = 0;
        }

        if (config.TopAnnouncementThreshold < 0)
        {
            Logger.LogWarning("TopAnnouncementThreshold cannot be negative. Clamping to 0.");
            config.TopAnnouncementThreshold = 0;
        }

        if (config.LeaderboardLimit <= 0)
        {
            Logger.LogWarning("LeaderboardLimit must be greater than 0. Resetting to 50.");
            config.LeaderboardLimit = 50;
        }

        if (config.TopCacheSeconds < 0)
        {
            Logger.LogWarning("TopCacheSeconds cannot be negative. Clamping to 0.");
            config.TopCacheSeconds = 0;
        }

        NormalizePointSettings(config.Points);

        if (config.DatabaseMode == DatabaseMode.MySql)
        {
            if (string.IsNullOrWhiteSpace(config.MySql.Host) ||
                string.IsNullOrWhiteSpace(config.MySql.Database) ||
                string.IsNullOrWhiteSpace(config.MySql.Username))
            {
                throw new InvalidOperationException("MySQL mode requires Host, Database, and Username to be configured.");
            }

            if (config.MySql.MaximumPoolSize == 0)
            {
                Logger.LogWarning("MySql.MaximumPoolSize must be greater than 0. Resetting to 50.");
                config.MySql.MaximumPoolSize = 50;
            }

            if (config.MySql.MinimumPoolSize > config.MySql.MaximumPoolSize)
            {
                Logger.LogWarning("MySql.MinimumPoolSize cannot be greater than MaximumPoolSize. Clamping to MaximumPoolSize.");
                config.MySql.MinimumPoolSize = config.MySql.MaximumPoolSize;
            }
        }

        if (config.DatabaseMode == DatabaseMode.Sqlite &&
            string.IsNullOrWhiteSpace(config.Sqlite.FilePath))
        {
            throw new InvalidOperationException("SQLite mode requires Sqlite.FilePath to be configured.");
        }

        Config = config;
    }

    public override void Load(bool hotReload)
    {
        _shutdown.Dispose();
        _shutdown = new CancellationTokenSource();
        _shutdownToken = _shutdown.Token;
        _isUnloading = false;

        _sessionService = new PlayerSessionService(Logger);
        _weaponStatsService = new WeaponStatsService();
        _commandCooldownService = new CommandCooldownService();
        _playtimeService = new PlaytimeService(Localizer);
        _repository = CreateRepository();
        _rankService = new RankService(_repository, _sessionService, _weaponStatsService, Logger);
        _autosaveService = new AutosaveService(this, _sessionService, _rankService, Logger);
        _wasdMenuService = new WasdMenuService();

        try
        {
            _repository.InitializeAsync(_shutdownToken).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Logger.LogCritical(exception, "Umbrella Ranked failed to initialize its {DatabaseMode} backend. No fallback was attempted.", Config.DatabaseMode);
            throw;
        }

        CompetitiveEnabledCvar.ValueChanged += OnCompetitiveEnabledCvarChanged;
        RegisterFakeConVars(this);
        RegisterPluginCallbacks();

        _autosaveService.Restart(Config.AutosaveIntervalSeconds);
        RestartPruneTimer();

        _loadConnectedPlayersWhenGlobalsAreReady = true;

        if (Config.PruneOnStartup && Config.PruneInactiveDays > 0)
        {
            RunBackground(PruneNowAsync(showStartedMessage: false, steamIdForReply: null), "initial prune");
        }
    }

    public override void Unload(bool hotReload)
    {
        _isUnloading = true;
        CompetitiveEnabledCvar.ValueChanged -= OnCompetitiveEnabledCvarChanged;
        ClearMainThreadQueue();

        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _pruneTimer?.Kill();
        _pruneTimer = null;

        _wasdMenuService?.CloseAll();
        _autosaveService?.Stop();

        try
        {
            _autosaveService?.FlushAsync(force: true, includeDisconnected: true, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Final Umbrella Ranked flush failed during unload.");
        }

        try
        {
            _repository?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Failed to dispose the Umbrella Ranked repository cleanly.");
        }

        _autosaveService?.Dispose();
        _wasdMenuService?.Dispose();
        _shutdown.Dispose();
    }

    [GameEventHandler]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        if (IsRealPlayer(@event.Userid))
        {
            StartPlayerLoad(@event.Userid!);
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerChangename(EventPlayerChangename @event, GameEventInfo info)
    {
        _sessionService?.UpdateName(@event.Userid, @event.Newname);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (IsRealPlayer(@event.Userid))
        {
            _wasdMenuService?.Close(@event.Userid!);
        }

        if (!TryGetCompetitiveRankService(out var rankService))
        {
            return HookResult.Continue;
        }

        var attacker = @event.Attacker;
        var victim = @event.Userid;
        var assister = @event.Assister;
        var isSuicide = IsRealPlayer(attacker) && IsSamePlayer(attacker, victim);
        var isTeamKill = IsRealPlayer(attacker) && IsRealPlayer(victim) && !isSuicide && IsSameTeam(attacker!, victim!);

        if (isTeamKill)
        {
            rankService.TryRecordPoints(attacker!, -Config.Points.TeamKillPenalty);
        }
        else if (IsRealPlayer(attacker) && !isSuicide)
        {
            rankService.TryRecordKill(attacker!, @event.Weapon, GetKillPointValue(@event));
        }

        if (!isSuicide &&
            !isTeamKill &&
            IsRealPlayer(assister) &&
            !IsSamePlayer(assister, attacker) &&
            !IsSamePlayer(assister, victim))
        {
            rankService.TryRecordAssist(assister!, Config.Points.Assist);
        }

        if (IsRealPlayer(victim))
        {
            rankService.TryRecordDeath(victim!, GetDeathPenaltyValue(@event, isSuicide, isTeamKill));
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundMvp(EventRoundMvp @event, GameEventInfo info)
    {
        if (TryGetCompetitiveRankService(out var rankService) && IsRealPlayer(@event.Userid))
        {
            rankService.TryRecordPoints(@event.Userid!, Config.Points.Mvp);
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        if (TryGetCompetitiveRankService(out var rankService) && IsRealPlayer(@event.Userid))
        {
            rankService.TryRecordPoints(@event.Userid!, Config.Points.BombPlant);
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
    {
        if (TryGetCompetitiveRankService(out var rankService) && IsRealPlayer(@event.Userid))
        {
            rankService.TryRecordPoints(@event.Userid!, Config.Points.BombDefuse);
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBombExploded(EventBombExploded @event, GameEventInfo info)
    {
        if (TryGetCompetitiveRankService(out var rankService) && IsRealPlayer(@event.Userid))
        {
            rankService.TryRecordPoints(@event.Userid!, Config.Points.BombExplode);
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnHostageRescued(EventHostageRescued @event, GameEventInfo info)
    {
        if (TryGetCompetitiveRankService(out var rankService) && IsRealPlayer(@event.Userid))
        {
            rankService.TryRecordPoints(@event.Userid!, Config.Points.HostageRescue);
        }

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (!TryGetCompetitiveRankService(out var rankService) ||
            (Config.Points.TeamWin == 0 && Config.Points.TeamLossPenalty == 0))
        {
            return HookResult.Continue;
        }

        AwardRoundTeamPoints(@event.Winner, rankService);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        if (IsRealPlayer(@event.Userid))
        {
            _wasdMenuService?.Close(@event.Userid!);
        }

        return HookResult.Continue;
    }

    private void OnClientDisconnect(int playerSlot)
    {
        if (_sessionService == null)
        {
            return;
        }

        PlayerSession? session = null;
        if (_sessionService.TryGetSessionBySlot(playerSlot, out var matchedSession))
        {
            session = matchedSession;
        }

        _wasdMenuService?.HandleDisconnect(playerSlot);
        _sessionService.HandleDisconnect(playerSlot, DateTimeOffset.UtcNow);

        if (session != null)
        {
            _commandCooldownService?.Clear(session.SteamId);

            if (!session.IsResetInProgress)
            {
                RunBackground(_rankService!.SaveSessionAsync(session, force: true, _shutdownToken), "disconnect save");
            }
        }
    }

    private void OnMapEnd()
    {
        try
        {
            _autosaveService?.FlushAsync(force: true, includeDisconnected: true, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Map-end flush failed.");
        }
    }

    private void OnMapStart(string mapName)
    {
        _currentMapName = mapName ?? string.Empty;
    }

    private void OnServerPrecacheResources(ResourceManifest manifest)
    {
        var resourcePath = GetSoundResourcePath();
        if (!string.IsNullOrEmpty(resourcePath))
        {
            NativeAPI.AddResource(resourcePath);
        }
    }

    private void OnTick()
    {
        DrainMainThreadQueue();
        _wasdMenuService?.OnTick();
        LoadConnectedPlayersWhenGlobalsAreReady();
    }

    private void OnRankCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!TryPrepareLoadedPlayerCommand(player, out var session))
        {
            return;
        }

        if (!IsCompetitiveRankingEnabledForCurrentMap())
        {
            PrintLocalized(player!, "rank.disabled");
            return;
        }

        RunBackground(ExecuteRankCommandAsync(session), "rank command");
    }

    private void OnTopCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!TryPrepareLoadedPlayerCommand(player, out var session))
        {
            return;
        }

        if (!IsCompetitiveRankingEnabledForCurrentMap())
        {
            PrintLocalized(player!, "rank.disabled");
            return;
        }

        RunBackground(ExecuteTopCommandAsync(session), "top command");
    }

    private void OnTopTimeCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!TryPrepareLoadedPlayerCommand(player, out var session))
        {
            return;
        }

        RunBackground(ExecuteTopTimeCommandAsync(session), "toptime command");
    }

    private void OnTopWeaponsCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!TryPrepareLoadedPlayerCommand(player, out var session))
        {
            return;
        }

        if (!IsCompetitiveRankingEnabledForCurrentMap())
        {
            PrintLocalized(player!, "rank.disabled");
            return;
        }

        RunBackground(ExecuteTopWeaponsMenuAsync(session), "topweapons command");
    }

    private void OnResetRankCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!TryPrepareLoadedPlayerCommand(player, out var session))
        {
            return;
        }

        if (!Config.AllowResetRank)
        {
            PrintLocalized(player!, "reset.disabled");
            return;
        }

        if (session.IsResetInProgress)
        {
            PrintLocalized(player!, "reset.in_progress");
            return;
        }

        var stats = _rankService!.CaptureCurrentStats(session);
        var remaining = GetResetCooldownRemaining(stats.LastResetUnixTime);
        if (remaining.HasValue)
        {
            var remainingDays = remaining.Value / 86400;
            var remainingHours = (remaining.Value % 86400) / 3600;
            PrintLocalized(player!, "reset.cooldown", remainingDays, remainingHours);
            return;
        }

        QueueForSession(session, connectedPlayer => OpenResetConfirmMenu(connectedPlayer, session.SteamId));
    }

    private void OnPruneNowCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (_repository == null)
        {
            commandInfo.ReplyToCommand(Localizer.ForPlayer(player, "prune.data_loading"));
            return;
        }

        if (player != null && !AdminManager.PlayerHasPermissions(player, "@css/root"))
        {
            commandInfo.ReplyToCommand(Localizer.ForPlayer(player, "admin.permission"));
            return;
        }

        commandInfo.ReplyToCommand(Localizer.ForPlayer(player, "prune.started"));
        var steamId = TryGetCallerSteamId(player);
        RunBackground(PruneNowAsync(showStartedMessage: true, steamIdForReply: steamId), "manual prune");
    }

    private void OnRankStatusCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player != null && !AdminManager.PlayerHasPermissions(player, "@css/root"))
        {
            commandInfo.ReplyToCommand(Localizer.ForPlayer(player, "admin.permission"));
            return;
        }

        var sessions = _sessionService?.GetStatus() ?? new SessionServiceStatus(0, 0, 0, 0, 0);
        var autosave = _autosaveService?.GetStatus() ?? new AutosaveStatus(false, null, null, string.Empty);
        var rankStatus = _rankService?.GetStatus() ?? new RankServiceStatus(0, null);
        var activeMenus = _wasdMenuService?.ActiveMenuCount ?? 0;
        var competitiveEnabled = IsCompetitiveRankingEnabledForCurrentMap();

        commandInfo.ReplyToCommand("[Umbrella Ranked] Status");
        commandInfo.ReplyToCommand($"Backend: {Config.DatabaseMode} | Config: {(Config.Enabled ? "on" : "off")} | CVar: {(IsRuntimeRankCvarEnabled() ? "on" : "off")} | Competitive: {(competitiveEnabled ? "on" : "off")} | Mode: {Config.RankingMode}");
        commandInfo.ReplyToCommand($"Map: {GetCurrentMapDisplayName()} | Map blocked: {(IsCurrentMapRankDisabled() ? "yes" : "no")}");
        commandInfo.ReplyToCommand($"Players: connected {sessions.ConnectedPlayers}, loaded {sessions.LoadedSessions}, loading {sessions.LoadingSessions}, sessions {sessions.TotalSessions}");
        commandInfo.ReplyToCommand($"Pending saves: {sessions.PendingSaveSessions} | Autosave: {(autosave.IsRunning ? "on" : "off")} | Menus: {activeMenus}");
        commandInfo.ReplyToCommand($"Top cache: {rankStatus.CachedLeaderboardCount} entries | TTL: {Config.TopCacheSeconds:n0}s | Last refresh: {FormatStatusTime(rankStatus.LastCacheRefreshUtc)}");
        commandInfo.ReplyToCommand($"Last autosave OK: {FormatStatusTime(autosave.LastSuccessUtc)} | Last error: {(string.IsNullOrWhiteSpace(autosave.LastError) ? "none" : autosave.LastError)}");
    }

    private HookResult OnSayCommand(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (!IsRealPlayer(player))
        {
            return HookResult.Continue;
        }

        var message = NormalizeChatMessage(commandInfo.ArgString);
        if (string.IsNullOrEmpty(message))
        {
            return HookResult.Continue;
        }

        switch (message.ToLowerInvariant())
        {
            case "rank":
                OnRankCommand(player, commandInfo);
                return HookResult.Handled;
            case "top":
                OnTopCommand(player, commandInfo);
                return HookResult.Handled;
            case "toparmas":
            case "topweapons":
                OnTopWeaponsCommand(player, commandInfo);
                return HookResult.Handled;
            case "toptime":
                OnTopTimeCommand(player, commandInfo);
                return HookResult.Handled;
            case "resetrank":
            case "rrank":
                OnResetRankCommand(player, commandInfo);
                return HookResult.Handled;
            default:
                return HookResult.Continue;
        }
    }

    private async Task ExecuteRankCommandAsync(PlayerSession session)
    {
        try
        {
            var saveSucceeded = await _rankService!.SaveSessionAsync(session, force: true, _shutdownToken);
            if (!saveSucceeded)
            {
                QueueForSession(session, player => PrintLocalized(player, "rank.position.error"));
                return;
            }

            if (!IsCompetitiveRankingEnabledForCurrentMap())
            {
                QueueForSession(session, player => PrintLocalized(player, "rank.disabled"));
                return;
            }

            var position = await _rankService.GetRankPositionAsync(session.SteamId, Config.MinimumKillsRequired, Config.RankingMode, _shutdownToken);
            var stats = _rankService.CaptureCurrentStats(session);
            var positionValue = position.GetValueOrDefault();

            QueueForSession(session, player =>
            {
                var playtime = _playtimeService!.Format(player, stats.PlaytimeSeconds);

                if (positionValue > 0)
                {
                    player.PrintToChat(Localizer.ForPlayer(player, "rank.message", positionValue, stats.Points, stats.Kills, stats.Deaths, stats.Assists, stats.Kda, playtime));
                }
                else
                {
                    player.PrintToChat(Localizer.ForPlayer(player, "rank.unranked", stats.Points, stats.Kills, stats.Deaths, stats.Assists, stats.Kda, playtime));
                }
            });
        }
        catch (Exception exception)
        {
            if (IsExpectedShutdownException(exception))
            {
                return;
            }

            Logger.LogError(exception, "Failed to execute the rank command.");
            QueueForSession(session, player => PrintLocalized(player, "rank.position.error"));
        }
    }

    private async Task ExecuteTopCommandAsync(PlayerSession session)
    {
        try
        {
            if (!IsCompetitiveRankingEnabledForCurrentMap())
            {
                QueueForSession(session, player => PrintLocalized(player, "rank.disabled"));
                return;
            }

            var entries = await _rankService!.GetTopPlayersCachedAsync(
                Config.MinimumKillsRequired,
                Config.LeaderboardLimit,
                Config.RankingMode,
                GetTopCacheTtl(),
                _shutdownToken);

            if (!IsCompetitiveRankingEnabledForCurrentMap())
            {
                return;
            }

            QueueForSession(session, player => OpenTopPlayersMenu(player, entries));
        }
        catch (Exception exception)
        {
            if (IsExpectedShutdownException(exception))
            {
                return;
            }

            Logger.LogError(exception, "Failed to load top players.");
            QueueForSession(session, player => PrintLocalized(player, "top.load_error"));
        }
    }

    private async Task ExecuteTopTimeCommandAsync(PlayerSession session)
    {
        try
        {
            var entries = await _rankService!.GetTopPlaytimeCachedAsync(Config.LeaderboardLimit, GetTopCacheTtl(), _shutdownToken);
            QueueForSession(session, player => OpenTopTimeMenu(player, entries));
        }
        catch (Exception exception)
        {
            if (IsExpectedShutdownException(exception))
            {
                return;
            }

            Logger.LogError(exception, "Failed to load top playtime.");
            QueueForSession(session, player => PrintLocalized(player, "toptime.load_error"));
        }
    }

    private async Task ExecuteTopWeaponsMenuAsync(PlayerSession session)
    {
        try
        {
            if (!IsCompetitiveRankingEnabledForCurrentMap())
            {
                QueueForSession(session, player => PrintLocalized(player, "rank.disabled"));
                return;
            }

            var weapons = await _rankService!.GetTrackedWeaponsCachedAsync(GetTopCacheTtl(), _shutdownToken);

            if (!IsCompetitiveRankingEnabledForCurrentMap())
            {
                return;
            }

            QueueForSession(session, player => OpenWeaponMenu(player, session.SteamId, weapons));
        }
        catch (Exception exception)
        {
            if (IsExpectedShutdownException(exception))
            {
                return;
            }

            Logger.LogError(exception, "Failed to load the weapon menu.");
            QueueForSession(session, player => PrintLocalized(player, "weapon.menu.load_error"));
        }
    }

    private async Task ExecuteTopWeaponAsync(string steamId, string weapon)
    {
        if (_sessionService == null || !_sessionService.TryGetSessionBySteamId(steamId, out var session))
        {
            return;
        }

        if (!IsCompetitiveRankingEnabledForCurrentMap())
        {
            return;
        }

        try
        {
            var entries = await _rankService!.GetTopWeaponCachedAsync(weapon, Config.LeaderboardLimit, GetTopCacheTtl(), _shutdownToken);

            if (!IsCompetitiveRankingEnabledForCurrentMap())
            {
                return;
            }

            QueueForSession(session, player =>
            {
                if (_wasdMenuService?.IsContextOpen(player, TopWeaponsMenuContext) == true)
                {
                    OpenTopWeaponMenu(player, steamId, weapon, entries);
                }
            });
        }
        catch (Exception exception)
        {
            if (IsExpectedShutdownException(exception))
            {
                return;
            }

            Logger.LogError(exception, "Failed to load weapon ranking for {Weapon}.", weapon);
            QueueForSession(session, player => PrintLocalized(player, "weapon.top.load_error"));
        }
    }

    private async Task ExecuteTopWeaponCategoryAsync(string steamId, WeaponMenuCategory category)
    {
        if (_sessionService == null || !_sessionService.TryGetSessionBySteamId(steamId, out var session))
        {
            return;
        }

        if (!IsCompetitiveRankingEnabledForCurrentMap())
        {
            return;
        }

        try
        {
            var entries = await _rankService!.GetTopWeaponCategoryCachedAsync(category.Weapons, Config.LeaderboardLimit, GetTopCacheTtl(), _shutdownToken);

            if (!IsCompetitiveRankingEnabledForCurrentMap())
            {
                return;
            }

            QueueForSession(session, player =>
            {
                if (_wasdMenuService?.IsContextOpen(player, TopWeaponsMenuContext) == true)
                {
                    OpenTopWeaponCategoryMenu(player, category, entries);
                }
            });
        }
        catch (Exception exception)
        {
            if (IsExpectedShutdownException(exception))
            {
                return;
            }

            Logger.LogError(exception, "Failed to load weapon category ranking for {Category}.", category.TitleKey);
            QueueForSession(session, player => PrintLocalized(player, "weapon.top.load_error"));
        }
    }

    private async Task ExecuteResetRankAsync(string steamId)
    {
        if (_sessionService == null || !_sessionService.TryGetSessionBySteamId(steamId, out var session))
        {
            return;
        }

        if (!IsCompetitiveRankingEnabledForCurrentMap())
        {
            QueueForSession(session, player => PrintLocalized(player, "rank.disabled"));
            return;
        }

        if (!Config.AllowResetRank)
        {
            QueueForSession(session, player => PrintLocalized(player, "reset.disabled"));
            return;
        }

        if (session.IsResetInProgress)
        {
            QueueForSession(session, player => PrintLocalized(player, "reset.in_progress"));
            return;
        }

        var stats = _rankService!.CaptureCurrentStats(session);
        var remaining = GetResetCooldownRemaining(stats.LastResetUnixTime);
        if (remaining.HasValue)
        {
            QueueForSession(session, player =>
            {
                var remainingDays = remaining.Value / 86400;
                var remainingHours = (remaining.Value % 86400) / 3600;
                PrintLocalized(player, "reset.cooldown", remainingDays, remainingHours);
            });

            return;
        }

        var success = await _rankService.ResetRankAsync(session, _shutdownToken);
        QueueForSession(session, player => PrintLocalized(player, success ? "reset.success" : "reset.error"));
    }

    private async Task ExecuteWelcomeAnnouncementAsync(string steamId)
    {
        if (_sessionService == null ||
            _rankService == null ||
            !_sessionService.TryGetSessionBySteamId(steamId, out var session) ||
            !session.IsLoaded ||
            !IsCompetitiveRankingEnabledForCurrentMap())
        {
            return;
        }

        var position = await _rankService.GetWelcomePositionAsync(
            session,
            Config.MinimumKillsRequired,
            Config.TopAnnouncementThreshold,
            Config.RankingMode,
            _shutdownToken);

        if (!position.HasValue)
        {
            return;
        }

        QueueForSteamId(steamId, _ =>
        {
            if (_rankService == null ||
                _sessionService == null ||
                !_sessionService.TryGetSessionBySteamId(steamId, out var connectedSession) ||
                !IsCompetitiveRankingEnabledForCurrentMap())
            {
                return;
            }

            var stats = _rankService.CaptureCurrentStats(connectedSession);
            if (position.Value == 1)
            {
                BroadcastLocalized("announce.top1", stats.Name);
                PlayTop1Sound();
            }
            else
            {
                BroadcastLocalized("announce.topn", position.Value, stats.Name);
            }
        });
    }

    private async Task PruneNowAsync(bool showStartedMessage, string? steamIdForReply)
    {
        if (_rankService == null || _sessionService == null || Config.PruneInactiveDays <= 0)
        {
            return;
        }

        try
        {
            var deletedPlayers = await _rankService.PruneInactivePlayersAsync(
                Config.PruneInactiveDays,
                _sessionService.GetConnectedSteamIds(),
                _shutdownToken);

            if (showStartedMessage && steamIdForReply != null)
            {
                QueueForSteamId(steamIdForReply, player => PrintLocalized(player, "prune.completed", deletedPlayers));
            }
        }
        catch (Exception exception)
        {
            if (IsExpectedShutdownException(exception))
            {
                return;
            }

            Logger.LogError(exception, "Inactive player prune failed.");
        }
    }

    private void RestartPruneTimer()
    {
        _pruneTimer?.Kill();
        _pruneTimer = null;

        if (Config.PruneInactiveDays <= 0 || Config.PruneCheckIntervalHours <= 0)
        {
            return;
        }

        _pruneTimer = AddTimer((float)(Config.PruneCheckIntervalHours * 3600.0), () =>
        {
            RunBackground(PruneNowAsync(showStartedMessage: false, steamIdForReply: null), "scheduled prune");
        }, TimerFlags.REPEAT);
    }

    private bool StartPlayerLoad(CCSPlayerController player)
    {
        if (_sessionService == null || _rankService == null)
        {
            return false;
        }

        if (!_sessionService.TryResolveIdentity(player, out var identity))
        {
            return false;
        }

        RunBackground(LoadPlayerAndMaybeAnnounceAsync(identity), $"load {identity.SteamId}");
        return true;
    }

    private void OpenTopPlayersMenu(CCSPlayerController player, IReadOnlyList<TopEntry> entries)
    {
        var items = entries
            .Select((entry, index) =>
            {
                var position = index + 1;
                var key = index == 0 ? "top.line.first" : "top.line.rest";
                var text = index == 0
                    ? Localizer.ForPlayer(player, key, entry.Name, entry.Points)
                    : Localizer.ForPlayer(player, key, position, entry.Name, entry.Points);

                return new WasdMenuItem(
                    text,
                    selectedPlayer =>
                    {
                        _wasdMenuService!.Push(selectedPlayer, CreateTopPlayerDetailPage(selectedPlayer, entry, position));
                    });
            })
            .ToList();

        _wasdMenuService!.Open(player, new WasdMenuPage(
            Localizer.ForPlayer(player, "top.title"),
            items,
            $"{Localizer.ForPlayer(player, "top.subtitle", Config.RankingMode, Config.MinimumKillsRequired)}",
            Localizer.ForPlayer(player, "top.empty"),
            TopPlayersMenuContext));
    }

    private void OpenTopTimeMenu(CCSPlayerController player, IReadOnlyList<PlaytimeTopEntry> entries)
    {
        var items = entries
            .Select((entry, index) =>
            {
                var position = index + 1;
                var playtime = _playtimeService!.Format(player, entry.PlaytimeSeconds);
                var key = index == 0 ? "toptime.line.first" : "toptime.line.rest";
                var text = index == 0
                    ? Localizer.ForPlayer(player, key, entry.Name, playtime)
                    : Localizer.ForPlayer(player, key, position, entry.Name, playtime);

                return new WasdMenuItem(
                    text,
                    selectedPlayer =>
                    {
                        _wasdMenuService!.Push(selectedPlayer, CreateTopPlaytimeDetailPage(selectedPlayer, entry, position));
                    });
            })
            .ToList();

        _wasdMenuService!.Open(player, new WasdMenuPage(
            Localizer.ForPlayer(player, "toptime.title"),
            items,
            null,
            Localizer.ForPlayer(player, "toptime.empty"),
            TopTimeMenuContext));
    }

    private void OpenWeaponMenu(CCSPlayerController player, string steamId, IReadOnlyList<string> weapons)
    {
        var categories = BuildWeaponMenuCategories(weapons);
        var items = categories
            .Select(category => new WasdMenuItem(
                Localizer.ForPlayer(player, category.TitleKey),
                selectedPlayer =>
                {
                    _wasdMenuService!.Push(selectedPlayer, CreateWeaponCategoryPage(selectedPlayer, steamId, category));
                }))
            .ToList();

        _wasdMenuService!.Open(player, new WasdMenuPage(
            Localizer.ForPlayer(player, "weapon.menu.title"),
            items,
            Localizer.ForPlayer(player, "weapon.menu.subtitle"),
            Localizer.ForPlayer(player, "weapon.menu.empty"),
            TopWeaponsMenuContext));
    }

    private WasdMenuPage CreateWeaponCategoryPage(CCSPlayerController player, string steamId, WeaponMenuCategory category)
    {
        var items = new List<WasdMenuItem>
        {
            new(
                Localizer.ForPlayer(player, "weapon.category.top"),
                _ =>
                {
                    RunBackground(ExecuteTopWeaponCategoryAsync(steamId, category), $"weapon category top {category.TitleKey}");
                })
        };

        items.AddRange(category.Weapons
            .Select(weapon => new WasdMenuItem(
                weapon,
                _ =>
                {
                    RunBackground(ExecuteTopWeaponAsync(steamId, weapon), $"weapon top {weapon}");
                })));

        return new WasdMenuPage(
            Localizer.ForPlayer(player, category.TitleKey),
            items,
            Localizer.ForPlayer(player, "weapon.category.subtitle"),
            Localizer.ForPlayer(player, "weapon.menu.empty"),
            TopWeaponsMenuContext);
    }

    private void OpenTopWeaponCategoryMenu(CCSPlayerController player, WeaponMenuCategory category, IReadOnlyList<WeaponTopEntry> entries)
    {
        var items = entries
            .Select((entry, index) =>
            {
                var position = index + 1;
                var key = index == 0 ? "weapon.category.top.line.first" : "weapon.category.top.line.rest";
                var text = index == 0
                    ? Localizer.ForPlayer(player, key, entry.Name, entry.Kills)
                    : Localizer.ForPlayer(player, key, position, entry.Name, entry.Kills);

                return new WasdMenuItem(
                    text,
                    selectedPlayer =>
                    {
                        _wasdMenuService!.Push(selectedPlayer, CreateWeaponCategoryEntryDetailPage(selectedPlayer, category, entry, position));
                    });
            })
            .ToList();

        _wasdMenuService!.PushIfContext(player, TopWeaponsMenuContext, new WasdMenuPage(
            Localizer.ForPlayer(player, "weapon.category.top.title", Localizer.ForPlayer(player, category.TitleKey)),
            items,
            null,
            Localizer.ForPlayer(player, "weapon.top.empty"),
            TopWeaponsMenuContext));
    }

    private void OpenTopWeaponMenu(CCSPlayerController player, string steamId, string weapon, IReadOnlyList<WeaponTopEntry> entries)
    {
        var items = entries
            .Select((entry, index) =>
            {
                var position = index + 1;
                var key = index == 0 ? "weapon.top.line.first" : "weapon.top.line.rest";
                var text = index == 0
                    ? Localizer.ForPlayer(player, key, entry.Name, entry.Kills)
                    : Localizer.ForPlayer(player, key, position, entry.Name, entry.Kills);

                return new WasdMenuItem(
                    text,
                    selectedPlayer =>
                    {
                        _wasdMenuService!.Push(selectedPlayer, CreateWeaponEntryDetailPage(selectedPlayer, weapon, entry, position));
                    });
            })
            .ToList();

        _wasdMenuService!.PushIfContext(player, TopWeaponsMenuContext, new WasdMenuPage(
            Localizer.ForPlayer(player, "weapon.top.title", weapon),
            items,
            null,
            Localizer.ForPlayer(player, "weapon.top.empty"),
            TopWeaponsMenuContext));
    }

    private void OpenResetConfirmMenu(CCSPlayerController player, string steamId)
    {
        var items = new[]
        {
            new WasdMenuItem(
                Localizer.ForPlayer(player, "reset.confirm.yes"),
                selectedPlayer =>
                {
                    _wasdMenuService!.Close(selectedPlayer);
                    RunBackground(ExecuteResetRankAsync(steamId), $"reset {steamId}");
                }),
            new WasdMenuItem(
                Localizer.ForPlayer(player, "reset.confirm.no"),
                selectedPlayer =>
                {
                    _wasdMenuService!.Close(selectedPlayer);
                })
        };

        _wasdMenuService!.Open(player, new WasdMenuPage(
            Localizer.ForPlayer(player, "reset.confirm.title"),
            items,
            contextTag: ResetRankMenuContext));
    }

    private WasdMenuPage CreateTopPlayerDetailPage(CCSPlayerController player, TopEntry entry, int position)
    {
        var playtime = _playtimeService!.Format(player, entry.PlaytimeSeconds);
        return new WasdMenuPage(
            $"#{position} {entry.Name}",
            new[]
            {
                InfoLine(Localizer.ForPlayer(player, "detail.points", entry.Points)),
                InfoLine(Localizer.ForPlayer(player, "detail.kills", entry.Kills)),
                InfoLine(Localizer.ForPlayer(player, "detail.deaths", entry.Deaths)),
                InfoLine(Localizer.ForPlayer(player, "detail.assists", entry.Assists)),
                InfoLine(Localizer.ForPlayer(player, "detail.kda", entry.Kda)),
                InfoLine(Localizer.ForPlayer(player, "detail.playtime", playtime))
            },
            Localizer.ForPlayer(player, "top.title"),
            contextTag: TopPlayersMenuContext);
    }

    private WasdMenuPage CreateTopPlaytimeDetailPage(CCSPlayerController player, PlaytimeTopEntry entry, int position)
    {
        var playtime = _playtimeService!.Format(player, entry.PlaytimeSeconds);
        return new WasdMenuPage(
            $"#{position} {entry.Name}",
            new[]
            {
                InfoLine(Localizer.ForPlayer(player, "detail.playtime", playtime))
            },
            Localizer.ForPlayer(player, "toptime.title"),
            contextTag: TopTimeMenuContext);
    }

    private WasdMenuPage CreateWeaponEntryDetailPage(CCSPlayerController player, string weapon, WeaponTopEntry entry, int position)
    {
        return new WasdMenuPage(
            $"#{position} {entry.Name}",
            new[]
            {
                InfoLine(Localizer.ForPlayer(player, "detail.weapon", weapon)),
                InfoLine(Localizer.ForPlayer(player, "detail.kills", entry.Kills))
            },
            Localizer.ForPlayer(player, "weapon.top.title", weapon),
            contextTag: TopWeaponsMenuContext);
    }

    private WasdMenuPage CreateWeaponCategoryEntryDetailPage(CCSPlayerController player, WeaponMenuCategory category, WeaponTopEntry entry, int position)
    {
        return new WasdMenuPage(
            $"#{position} {entry.Name}",
            new[]
            {
                InfoLine(Localizer.ForPlayer(player, "detail.category", Localizer.ForPlayer(player, category.TitleKey))),
                InfoLine(Localizer.ForPlayer(player, "detail.kills", entry.Kills))
            },
            Localizer.ForPlayer(player, "weapon.category.top.title", Localizer.ForPlayer(player, category.TitleKey)),
            contextTag: TopWeaponsMenuContext);
    }

    private static IReadOnlyList<WeaponMenuCategory> BuildWeaponMenuCategories(IReadOnlyList<string> weapons)
    {
        var buckets = WeaponCategoryDefinitions
            .ToDictionary(definition => definition.Id, _ => new List<string>(), StringComparer.Ordinal);

        foreach (var weapon in weapons.OrderBy(weapon => weapon, StringComparer.OrdinalIgnoreCase))
        {
            var categoryId = ResolveWeaponCategoryId(weapon);
            buckets[categoryId].Add(weapon);
        }

        return WeaponCategoryDefinitions
            .Select(definition => new WeaponMenuCategory(definition.TitleKey, buckets[definition.Id]))
            .Where(category => category.Weapons.Count > 0)
            .ToList();
    }

    private static string ResolveWeaponCategoryId(string weapon)
    {
        var key = weapon.Trim().ToLowerInvariant();

        return key switch
        {
            "ak47" or "m4a1" or "m4a1-s" or "famas" or "galilar" or "aug" or "sg556" => "rifles",
            "awp" or "ssg08" or "scar20" or "g3sg1" => "snipers",
            "glock" or "hkp2000" or "usp-s" or "p250" or "deagle" or "elite" or "fiveseven" or "tec9" or "cz75a" or "revolver" => "pistols",
            "mac10" or "mp9" or "mp7" or "mp5sd" or "ump45" or "p90" or "bizon" => "smgs",
            "nova" or "xm1014" or "mag7" or "sawedoff" or "m249" or "negev" => "heavy",
            "he grenade" or "flashbang" or "smoke grenade" or "molotov/inc" or "decoy" or "fire" => "grenades",
            "knife" or "taser" or "zeus x27" => "melee",
            _ => "other"
        };
    }

    private static WasdMenuItem InfoLine(string text)
    {
        return new WasdMenuItem(text, isSelectable: false);
    }

    private bool TryPrepareLoadedPlayerCommand(CCSPlayerController? player, out PlayerSession session)
    {
        session = null!;

        if (!IsRealPlayer(player) || _sessionService == null || _commandCooldownService == null)
        {
            return false;
        }

        if (!_sessionService.TryResolveIdentity(player, out var identity))
        {
            PrintLocalized(player!, "data.loading");
            return false;
        }

        if (!_commandCooldownService.TryConsume(identity.SteamId, Config.CommandCooldownSeconds, out var remainingSeconds))
        {
            PrintLocalized(player!, "cooldown.message", remainingSeconds);
            return false;
        }

        if (!_sessionService.TryGetSession(player, out session) || !session.IsLoaded)
        {
            StartPlayerLoad(player!);
            PrintLocalized(player!, "data.loading");
            return false;
        }

        return true;
    }

    private void PrintLocalized(CCSPlayerController player, string key, params object[] args)
    {
        player.PrintToChat(Localizer.ForPlayer(player, key, args));
    }

    private void BroadcastLocalized(string key, params object[] args)
    {
        if (!TryGetRealPlayers(out var players))
        {
            return;
        }

        foreach (var player in players)
        {
            player.PrintToChat(Localizer.ForPlayer(player, key, args));
        }
    }

    private void QueueForSession(PlayerSession session, Action<CCSPlayerController> action)
    {
        EnqueueMainThreadAction(() =>
        {
            if (_sessionService != null && _sessionService.TryGetConnectedPlayer(session, out var player))
            {
                action(player);
            }
        });
    }

    private void QueueForSteamId(string steamId, Action<CCSPlayerController> action)
    {
        EnqueueMainThreadAction(() =>
        {
            if (_sessionService != null &&
                _sessionService.TryGetSessionBySteamId(steamId, out var session) &&
                _sessionService.TryGetConnectedPlayer(session, out var player))
            {
                action(player);
            }
        });
    }

    private void RunBackground(Task task, string operationName)
    {
        _ = task.ContinueWith(
            continuation =>
            {
                var exception = continuation.Exception?.Flatten();
                if (exception == null || exception.InnerExceptions.All(IsExpectedShutdownException))
                {
                    return;
                }

                Logger.LogError(exception, "Umbrella Ranked background task failed during {Operation}.", operationName);
            },
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private bool IsExpectedShutdownException(Exception exception)
    {
        if (exception is AggregateException aggregateException)
        {
            return aggregateException.Flatten().InnerExceptions.All(IsExpectedShutdownException);
        }

        return exception is OperationCanceledException ||
            (_isUnloading && exception is ObjectDisposedException);
    }

    private void RegisterPluginCallbacks()
    {
        AddCommand("css_rank", "Show your rank stats.", OnRankCommand);
        AddCommand("css_top", "Show the top ranked players.", OnTopCommand);
        AddCommand("css_toptime", "Show the top playtime leaderboard.", OnTopTimeCommand);
        AddCommand("css_topweapons", "Open the weapon leaderboard menu.", OnTopWeaponsCommand);
        AddCommand("css_toparmas", "Open the weapon leaderboard menu.", OnTopWeaponsCommand);
        AddCommand("css_resetrank", "Reset your rank stats.", OnResetRankCommand);
        AddCommand("css_rrank", "Reset your rank stats.", OnResetRankCommand);
        AddCommand("css_rank_prunenow", "Run an inactive player prune immediately.", OnPruneNowCommand);
        AddCommand("css_rank_status", "Show Umbrella Ranked internal status.", OnRankStatusCommand);
        AddCommandListener("say", OnSayCommand, HookMode.Pre);
        AddCommandListener("say_team", OnSayCommand, HookMode.Pre);

        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnServerPrecacheResources>(OnServerPrecacheResources);
        RegisterListener<Listeners.OnTick>(OnTick);
    }

    private void OnCompetitiveEnabledCvarChanged(object? sender, bool enabled)
    {
        if (!enabled)
        {
            CloseCompetitiveRankMenus();
        }

        Logger.LogInformation("Runtime CVar css_rank_enabled changed to {Enabled}.", enabled);
    }

    private void CloseCompetitiveRankMenus()
    {
        _wasdMenuService?.CloseByContext(TopPlayersMenuContext);
        _wasdMenuService?.CloseByContext(TopWeaponsMenuContext);
        _wasdMenuService?.CloseByContext(ResetRankMenuContext);
    }

    private void EnqueueMainThreadAction(Action action)
    {
        if (_isUnloading)
        {
            return;
        }

        lock (_mainThreadActionsLock)
        {
            if (!_isUnloading)
            {
                _mainThreadActions.Enqueue(action);
            }
        }
    }

    private void DrainMainThreadQueue()
    {
        while (true)
        {
            Action action;
            lock (_mainThreadActionsLock)
            {
                if (_mainThreadActions.Count == 0)
                {
                    return;
                }

                action = _mainThreadActions.Dequeue();
            }

            try
            {
                action();
            }
            catch (Exception exception)
            {
                Logger.LogError(exception, "Queued Umbrella Ranked main-thread action failed.");
            }
        }
    }

    private void ClearMainThreadQueue()
    {
        lock (_mainThreadActionsLock)
        {
            _mainThreadActions.Clear();
        }
    }

    private async Task LoadPlayerAndMaybeAnnounceAsync(PlayerIdentity identity)
    {
        await _rankService!.EnsurePlayerLoadedAsync(identity, _shutdownToken);

        if (!IsCompetitiveRankingEnabledForCurrentMap() || Config.TopAnnouncementThreshold <= 0 || _sessionService == null)
        {
            return;
        }

        if (!_sessionService.TryGetSessionBySteamId(identity.SteamId, out var session) || !session.IsLoaded)
        {
            return;
        }

        var stats = _rankService.CaptureCurrentStats(session);
        if (stats.Kills < Config.MinimumKillsRequired)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(3), _shutdownToken);
        await ExecuteWelcomeAnnouncementAsync(identity.SteamId);
    }

    private IRankRepository CreateRepository()
    {
        return Config.DatabaseMode switch
        {
            DatabaseMode.MySql => new MySqlRankRepository(Config.MySql, Logger),
            DatabaseMode.Sqlite => new SqliteRankRepository(
                Config.Sqlite,
                ResolveSqlitePath(Config.Sqlite.FilePath),
                Path.Combine(ModuleDirectory, "sqlite"),
                Logger),
            _ => throw new InvalidOperationException($"Unsupported database mode: {Config.DatabaseMode}")
        };
    }

    private string ResolveSqlitePath(string configuredPath)
    {
        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(ModuleDirectory, configuredPath));
    }

    private static bool IsRealPlayer(CCSPlayerController? player)
    {
        return player != null && player.IsValid && !player.IsBot;
    }

    private static bool IsSamePlayer(CCSPlayerController? first, CCSPlayerController? second)
    {
        if (first == null || second == null || !first.IsValid || !second.IsValid)
        {
            return false;
        }

        if (first.UserId.HasValue && second.UserId.HasValue)
        {
            return first.UserId.Value == second.UserId.Value;
        }

        return first.Slot == second.Slot;
    }

    private static bool IsSameTeam(CCSPlayerController first, CCSPlayerController second)
    {
        return first.TeamNum > 1 && first.TeamNum == second.TeamNum;
    }

    private int GetKillPointValue(EventPlayerDeath deathEvent)
    {
        var points = Config.Points.Kill;

        if (deathEvent.Headshot)
        {
            points += Config.Points.HeadshotBonus;
        }

        points += GetSpecialWeaponPointBonus(deathEvent.Weapon);

        return points;
    }

    private int GetDeathPenaltyValue(EventPlayerDeath deathEvent, bool isSuicide, bool isTeamKill)
    {
        if (isTeamKill)
        {
            return 0;
        }

        var penalty = Config.Points.DeathPenalty;
        if (isSuicide)
        {
            return penalty + Config.Points.SuicidePenalty;
        }

        return GetSpecialWeaponPointBonus(deathEvent.Weapon) > 0
            ? GetKillPointValue(deathEvent)
            : penalty;
    }

    private int GetSpecialWeaponPointBonus(string? rawWeapon)
    {
        var weapon = _weaponStatsService?.NormalizeWeaponName(rawWeapon) ?? string.Empty;
        if (weapon.Equals("Knife", StringComparison.OrdinalIgnoreCase))
        {
            return Config.Points.KnifeKillBonus;
        }

        if (weapon.Equals("Zeus x27", StringComparison.OrdinalIgnoreCase))
        {
            return Config.Points.TaserKillBonus;
        }

        return 0;
    }

    private int? GetResetCooldownRemaining(int lastResetUnixTime)
    {
        if (Config.ResetRankCooldownDays <= 0 || lastResetUnixTime <= 0)
        {
            return null;
        }

        var nextResetUnix = lastResetUnixTime + (Config.ResetRankCooldownDays * 86400);
        var currentUnix = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (currentUnix >= nextResetUnix)
        {
            return null;
        }

        return nextResetUnix - currentUnix;
    }

    private void AwardRoundTeamPoints(int winnerTeam, RankService rankService)
    {
        if (winnerTeam <= 1 || !TryGetRealPlayers(out var players))
        {
            return;
        }

        foreach (var player in players)
        {
            if ((int)player.TeamNum == winnerTeam)
            {
                rankService.TryRecordPoints(player, Config.Points.TeamWin);
            }
            else if ((int)player.TeamNum > 1)
            {
                rankService.TryRecordPoints(player, -Config.Points.TeamLossPenalty);
            }
        }
    }

    private bool TryGetCompetitiveRankService(out RankService rankService)
    {
        rankService = null!;
        if (!IsCompetitiveRankingEnabledForCurrentMap() || _rankService == null)
        {
            return false;
        }

        if (_sessionService == null || _sessionService.GetConnectedPlayerCount() < Config.MinimumPlayersForStats)
        {
            return false;
        }

        rankService = _rankService;
        return true;
    }

    private void NormalizePointSettings(UmbrellaRankedConfig.PointsSettings points)
    {
        points.Kill = ClampPointValue(points.Kill, nameof(points.Kill));
        points.HeadshotBonus = ClampPointValue(points.HeadshotBonus, nameof(points.HeadshotBonus));
        points.KnifeKillBonus = ClampPointValue(points.KnifeKillBonus, nameof(points.KnifeKillBonus));
        points.TaserKillBonus = ClampPointValue(points.TaserKillBonus, nameof(points.TaserKillBonus));
        points.Assist = ClampPointValue(points.Assist, nameof(points.Assist));
        points.DeathPenalty = ClampPointValue(points.DeathPenalty, nameof(points.DeathPenalty));
        points.SuicidePenalty = ClampPointValue(points.SuicidePenalty, nameof(points.SuicidePenalty));
        points.TeamKillPenalty = ClampPointValue(points.TeamKillPenalty, nameof(points.TeamKillPenalty));
        points.Mvp = ClampPointValue(points.Mvp, nameof(points.Mvp));
        points.BombPlant = ClampPointValue(points.BombPlant, nameof(points.BombPlant));
        points.BombDefuse = ClampPointValue(points.BombDefuse, nameof(points.BombDefuse));
        points.BombExplode = ClampPointValue(points.BombExplode, nameof(points.BombExplode));
        points.HostageRescue = ClampPointValue(points.HostageRescue, nameof(points.HostageRescue));
        points.TeamWin = ClampPointValue(points.TeamWin, nameof(points.TeamWin));
        points.TeamLossPenalty = ClampPointValue(points.TeamLossPenalty, nameof(points.TeamLossPenalty));
    }

    private int ClampPointValue(int value, string settingName)
    {
        if (value >= 0)
        {
            return value;
        }

        Logger.LogWarning("Points.{SettingName} cannot be negative. Clamping to 0.", settingName);
        return 0;
    }

    private TimeSpan GetTopCacheTtl()
    {
        return TimeSpan.FromSeconds(Math.Max(0, Config.TopCacheSeconds));
    }

    private bool IsCompetitiveRankingEnabledForCurrentMap()
    {
        return Config.Enabled && IsRuntimeRankCvarEnabled() && !IsCurrentMapRankDisabled();
    }

    private bool IsRuntimeRankCvarEnabled()
    {
        return CompetitiveEnabledCvar.Value;
    }

    private bool IsCurrentMapRankDisabled()
    {
        if (string.IsNullOrWhiteSpace(_currentMapName) || Config.DisabledRankMapPatterns.Count == 0)
        {
            return false;
        }

        var mapName = NormalizeMapPattern(_currentMapName);
        var leafMapName = GetLeafMapName(mapName);

        return Config.DisabledRankMapPatterns.Any(pattern =>
        {
            var normalizedPattern = NormalizeMapPattern(pattern);
            return WildcardMatches(mapName, normalizedPattern) ||
                WildcardMatches(leafMapName, normalizedPattern);
        });
    }

    private string GetCurrentMapDisplayName()
    {
        return string.IsNullOrWhiteSpace(_currentMapName) ? "unknown" : _currentMapName;
    }

    private static string GetLeafMapName(string mapName)
    {
        var slashIndex = mapName.LastIndexOf('/');
        return slashIndex >= 0 && slashIndex < mapName.Length - 1
            ? mapName[(slashIndex + 1)..]
            : mapName;
    }

    private static string NormalizeMapPattern(string value)
    {
        return value.Trim().Replace('\\', '/').ToLowerInvariant();
    }

    private static bool WildcardMatches(string value, string pattern)
    {
        if (pattern.Length == 0)
        {
            return false;
        }

        if (!pattern.Contains('*', StringComparison.Ordinal))
        {
            return string.Equals(value, pattern, StringComparison.Ordinal);
        }

        var parts = pattern.Split('*', StringSplitOptions.None);
        var position = 0;

        if (parts[0].Length > 0)
        {
            if (!value.StartsWith(parts[0], StringComparison.Ordinal))
            {
                return false;
            }

            position = parts[0].Length;
        }

        for (var index = 1; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length == 0)
            {
                continue;
            }

            var foundAt = value.IndexOf(part, position, StringComparison.Ordinal);
            if (foundAt < 0)
            {
                return false;
            }

            position = foundAt + part.Length;
        }

        var lastPart = parts[^1];
        return pattern.EndsWith('*') ||
            lastPart.Length == 0 ||
            value.EndsWith(lastPart, StringComparison.Ordinal);
    }

    private static string FormatStatusTime(DateTimeOffset? value)
    {
        return value.HasValue
            ? value.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture)
            : "never";
    }

    private string? TryGetCallerSteamId(CCSPlayerController? player)
    {
        if (_sessionService == null || !IsRealPlayer(player))
        {
            return null;
        }

        return _sessionService.TryResolveIdentity(player, out var identity)
            ? identity.SteamId
            : null;
    }

    private static string NormalizeChatMessage(string? rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return string.Empty;
        }

        var message = rawMessage.Trim();
        if (message.Length >= 2 && message[0] == '"' && message[^1] == '"')
        {
            message = message[1..^1].Trim();
        }

        if (message.StartsWith('!') || message.StartsWith('/'))
        {
            return string.Empty;
        }

        return message;
    }

    private string GetSoundResourcePath()
    {
        if (Config.Top1Sound.PlaybackMode != SoundPlaybackMode.SoundEvent)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(Config.Top1Sound.ResourcePath))
        {
            return StringSanitizer.NormalizeOptionalSoundResource(Config.Top1Sound.ResourcePath);
        }

        var value = StringSanitizer.NormalizeSoundSample(Config.Top1Sound.Value);
        if (string.IsNullOrEmpty(value) || !(value.Contains('/') || value.Contains('.')))
        {
            return string.Empty;
        }

        return StringSanitizer.NormalizeOptionalSoundResource(value);
    }

    private void PlayTop1Sound()
    {
        var soundSample = StringSanitizer.NormalizeSoundSample(Config.Top1Sound.Value);
        if (string.IsNullOrWhiteSpace(soundSample) ||
            Config.Top1Sound.PlaybackMode == SoundPlaybackMode.Disabled)
        {
            return;
        }

        try
        {
            if (Config.Top1Sound.PlaybackMode == SoundPlaybackMode.ClientCommand)
            {
                if (!TryGetRealPlayers(out var players))
                {
                    return;
                }

                var escapedSample = StringSanitizer.SanitizeSoundClientCommandArgument(soundSample);
                if (string.IsNullOrEmpty(escapedSample))
                {
                    return;
                }

                var volume = Math.Clamp(Config.Top1Sound.Volume, 0.0f, 1.0f).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                foreach (var player in players)
                {
                    player.ExecuteClientCommand($"playvol \"{escapedSample}\" {volume}");
                }

                return;
            }

            var worldspawn = Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("worldspawn").FirstOrDefault();
            worldspawn?.EmitSound(soundSample, null, Config.Top1Sound.Volume, Config.Top1Sound.Pitch);
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "Failed to play the configured top-1 sound.");
        }
    }

    private void LoadConnectedPlayersWhenGlobalsAreReady()
    {
        if (!_loadConnectedPlayersWhenGlobalsAreReady)
        {
            return;
        }

        if (!TryGetRealPlayers(out var players))
        {
            return;
        }

        _loadConnectedPlayersWhenGlobalsAreReady = false;
        foreach (var player in players)
        {
            StartPlayerLoad(player);
        }
    }

    private bool TryGetRealPlayers(out IReadOnlyList<CCSPlayerController> players)
    {
        players = Array.Empty<CCSPlayerController>();

        try
        {
            players = Utilities.GetPlayers().Where(IsRealPlayer).ToArray();
            return true;
        }
        catch (InvalidOperationException exception) when (IsGlobalVariablesNotReady(exception))
        {
            return false;
        }
        catch (Exception exception)
        {
            Logger.LogDebug(exception, "Unable to enumerate players safely.");
            return false;
        }
    }

    private static bool IsGlobalVariablesNotReady(Exception exception)
    {
        return exception.Message.Contains("Global Variables not initialized yet", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record WeaponCategoryDefinition(string Id, string TitleKey);

    private sealed record WeaponMenuCategory(string TitleKey, IReadOnlyList<string> Weapons);
}

