using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using UmbrellaRanked.Config;
using UmbrellaRanked.Data;
using UmbrellaRanked.Models;

namespace UmbrellaRanked.Core;

public sealed class RankService
{
    private readonly IRankRepository _repository;
    private readonly PlayerSessionService _sessionService;
    private readonly WeaponStatsService _weaponStatsService;
    private readonly ILogger _logger;
    private readonly Dictionary<string, CacheEntry> _leaderboardCache = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastCacheRefreshUtc;

    public RankService(
        IRankRepository repository,
        PlayerSessionService sessionService,
        WeaponStatsService weaponStatsService,
        ILogger logger)
    {
        _repository = repository;
        _sessionService = sessionService;
        _weaponStatsService = weaponStatsService;
        _logger = logger;
    }

    /// <summary>
    /// Loads the stats of a session that the game thread already attached. This runs on
    /// a background thread and must never touch the slot, the user id or the connected
    /// flag: a player who left in the meantime would otherwise be resurrected.
    /// </summary>
    /// <returns><c>true</c> only when this call performed the load and it succeeded.</returns>
    public async Task<bool> EnsurePlayerLoadedAsync(PlayerSession session, CancellationToken cancellationToken)
    {
        if (!session.BeginLoad())
        {
            return false;
        }

        try
        {
            var statsTask = _repository.LoadPlayerStatsAsync(session.SteamId, cancellationToken);
            var weaponTask = _repository.LoadWeaponStatsAsync(session.SteamId, cancellationToken);

            await Task.WhenAll(statsTask, weaponTask);

            _sessionService.MarkPlayerLoaded(
                session,
                await statsTask,
                await weaponTask,
                DateTimeOffset.UtcNow);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _sessionService.MarkPlayerLoadFailed(session);
            return false;
        }
        catch (Exception exception)
        {
            _sessionService.MarkPlayerLoadFailed(session);

            // With the database down every retry of every player fails: the full stack
            // is logged once per session and the repeats are one short line.
            if (session.LoadFailureCount <= 1)
            {
                _logger.LogError(exception, "Failed to load rank data for {SteamId}.", session.SteamId);
            }
            else
            {
                _logger.LogWarning(
                    "Failed again to load rank data for {SteamId} (attempt {Attempt}): {Message}",
                    session.SteamId,
                    session.LoadFailureCount,
                    exception.Message);
            }

            return false;
        }
    }

    public bool TryRecordKill(CCSPlayerController player, string? rawWeapon, bool headshot, int points)
    {
        if (!_sessionService.TryGetSession(player, out var session))
        {
            return false;
        }

        return session.TryApplyKill(_weaponStatsService.NormalizeWeaponName(rawWeapon), headshot, points);
    }

    public bool TryRecordMvp(CCSPlayerController player, int points)
    {
        if (!_sessionService.TryGetSession(player, out var session))
        {
            return false;
        }

        return session.TryApplyMvp(points);
    }

    public bool TryRecordTeamKill(CCSPlayerController player, int penaltyPoints)
    {
        if (!_sessionService.TryGetSession(player, out var session))
        {
            return false;
        }

        return session.TryApplyTeamKill(penaltyPoints);
    }

    public bool TryRecordRoundResult(CCSPlayerController player, bool playedAsCounterTerrorist, bool won, int points)
    {
        if (!_sessionService.TryGetSession(player, out var session))
        {
            return false;
        }

        return session.TryApplyRoundResult(playedAsCounterTerrorist, won, points);
    }

    public bool TryRecordMatchResult(CCSPlayerController player, MatchResult result)
    {
        if (!_sessionService.TryGetSession(player, out var session))
        {
            return false;
        }

        return session.TryApplyMatchResult(result);
    }

    public bool TryRecordDeath(CCSPlayerController player, int penaltyPoints)
    {
        if (!_sessionService.TryGetSession(player, out var session))
        {
            return false;
        }

        return session.TryApplyDeath(penaltyPoints);
    }

    public bool TryRecordAssist(CCSPlayerController player, int points)
    {
        if (!_sessionService.TryGetSession(player, out var session))
        {
            return false;
        }

        return session.TryApplyAssist(points);
    }

    public bool TryRecordPoints(CCSPlayerController player, int points)
    {
        if (!_sessionService.TryGetSession(player, out var session))
        {
            return false;
        }

        return session.TryApplyPoints(points);
    }

    public PlayerRankStats CaptureCurrentStats(PlayerSession session)
    {
        return session.CaptureCurrentStats(DateTimeOffset.UtcNow);
    }

    public async Task<bool> SaveSessionAsync(PlayerSession session, bool force, CancellationToken cancellationToken)
    {
        await session.PersistenceLock.WaitAsync(cancellationToken);

        try
        {
            var capturedAtUtc = DateTimeOffset.UtcNow;
            var snapshot = session.CaptureSaveSnapshot(capturedAtUtc, force);
            if (snapshot == null)
            {
                _sessionService.RemoveIfCompleted(session);
                return true;
            }

            await _repository.SaveSnapshotAsync(snapshot, cancellationToken);
            session.MarkSaveSuccessful(snapshot, capturedAtUtc);
            _sessionService.RemoveIfCompleted(session);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to save rank data for {SteamId}.", session.SteamId);
            return false;
        }
        finally
        {
            session.PersistenceLock.Release();
        }
    }

    /// <returns><c>true</c> only when every session was persisted.</returns>
    public async Task<bool> SaveSessionsAsync(IEnumerable<PlayerSession> sessions, bool force, CancellationToken cancellationToken)
    {
        var allSucceeded = true;

        foreach (var session in sessions)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (!await SaveSessionAsync(session, force, cancellationToken))
            {
                allSucceeded = false;
            }
        }

        return allSucceeded;
    }

    public async Task<ResetRankOutcome> ResetRankAsync(PlayerSession session, int cooldownDays, CancellationToken cancellationToken)
    {
        try
        {
            await session.PersistenceLock.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ResetRankOutcome.Failed;
        }

        var started = false;

        try
        {
            var rejection = session.TryBeginReset(DateTimeOffset.UtcNow, cooldownDays, out var snapshot);
            if (rejection.HasValue)
            {
                return rejection.Value;
            }

            started = true;
            await _repository.ResetRankAsync(snapshot, cancellationToken);
            session.CompleteReset(snapshot, DateTimeOffset.UtcNow);
            _sessionService.RemoveIfCompleted(session);
            InvalidateLeaderboardCache();
            return ResetRankOutcome.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (started)
            {
                session.CancelReset();
            }

            return ResetRankOutcome.Failed;
        }
        catch (Exception exception)
        {
            if (started)
            {
                session.CancelReset();
            }

            _logger.LogError(exception, "Failed to reset rank data for {SteamId}.", session.SteamId);
            return ResetRankOutcome.Failed;
        }
        finally
        {
            session.PersistenceLock.Release();
        }
    }

    public Task<int?> GetRankPositionAsync(string steamId, int minimumKills, RankingMode rankingMode, int hideInactiveDays, CancellationToken cancellationToken)
    {
        return _repository.GetRankPositionAsync(steamId, minimumKills, rankingMode, hideInactiveDays, cancellationToken);
    }

    public Task<IReadOnlyList<TopEntry>> GetTopPlayersAsync(int minimumKills, int limit, RankingMode rankingMode, int hideInactiveDays, CancellationToken cancellationToken)
    {
        return _repository.GetTopPlayersAsync(minimumKills, limit, rankingMode, hideInactiveDays, cancellationToken);
    }

    public Task<IReadOnlyList<TopEntry>> GetTopPlayersCachedAsync(int minimumKills, int limit, RankingMode rankingMode, int hideInactiveDays, TimeSpan cacheTtl, CancellationToken cancellationToken)
    {
        return GetCachedAsync(
            $"top:players:{minimumKills}:{limit}:{rankingMode}:{hideInactiveDays}",
            cacheTtl,
            () => _repository.GetTopPlayersAsync(minimumKills, limit, rankingMode, hideInactiveDays, cancellationToken),
            cancellationToken);
    }

    public Task<IReadOnlyList<PlaytimeTopEntry>> GetTopPlaytimeAsync(int limit, int hideInactiveDays, CancellationToken cancellationToken)
    {
        return _repository.GetTopPlaytimeAsync(limit, hideInactiveDays, cancellationToken);
    }

    public Task<IReadOnlyList<PlaytimeTopEntry>> GetTopPlaytimeCachedAsync(int limit, int hideInactiveDays, TimeSpan cacheTtl, CancellationToken cancellationToken)
    {
        return GetCachedAsync(
            $"top:playtime:{limit}:{hideInactiveDays}",
            cacheTtl,
            () => _repository.GetTopPlaytimeAsync(limit, hideInactiveDays, cancellationToken),
            cancellationToken);
    }

    public Task<IReadOnlyList<string>> GetTrackedWeaponsAsync(CancellationToken cancellationToken)
    {
        return _repository.GetTrackedWeaponsAsync(cancellationToken);
    }

    public Task<IReadOnlyList<string>> GetTrackedWeaponsCachedAsync(TimeSpan cacheTtl, CancellationToken cancellationToken)
    {
        return GetCachedAsync(
            "weapons:tracked",
            cacheTtl,
            () => _repository.GetTrackedWeaponsAsync(cancellationToken),
            cancellationToken);
    }

    public Task<IReadOnlyList<WeaponTopEntry>> GetTopWeaponAsync(string weapon, int limit, int hideInactiveDays, CancellationToken cancellationToken)
    {
        return _repository.GetTopWeaponAsync(weapon, limit, hideInactiveDays, cancellationToken);
    }

    public Task<IReadOnlyList<WeaponTopEntry>> GetTopWeaponCachedAsync(string weapon, int limit, int hideInactiveDays, TimeSpan cacheTtl, CancellationToken cancellationToken)
    {
        return GetCachedAsync(
            $"top:weapon:{weapon}:{limit}:{hideInactiveDays}",
            cacheTtl,
            () => _repository.GetTopWeaponAsync(weapon, limit, hideInactiveDays, cancellationToken),
            cancellationToken);
    }

    public Task<IReadOnlyList<WeaponTopEntry>> GetTopWeaponCategoryCachedAsync(IReadOnlyCollection<string> weapons, int limit, int hideInactiveDays, TimeSpan cacheTtl, CancellationToken cancellationToken)
    {
        var keyWeapons = string.Join(",", weapons.OrderBy(weapon => weapon, StringComparer.Ordinal));
        return GetCachedAsync(
            $"top:weapon-category:{keyWeapons}:{limit}:{hideInactiveDays}",
            cacheTtl,
            () => _repository.GetTopWeaponCategoryAsync(weapons, limit, hideInactiveDays, cancellationToken),
            cancellationToken);
    }

    public Task<IReadOnlyList<TeamkillTopEntry>> GetTopTeamkillsCachedAsync(int minimumKills, int limit, int hideInactiveDays, TimeSpan cacheTtl, CancellationToken cancellationToken)
    {
        return GetCachedAsync(
            $"top:teamkills:{minimumKills}:{limit}:{hideInactiveDays}",
            cacheTtl,
            () => _repository.GetTopTeamkillsAsync(minimumKills, limit, hideInactiveDays, cancellationToken),
            cancellationToken);
    }

    public async Task<int?> GetWelcomePositionAsync(PlayerSession session, int minimumKills, int topCount, RankingMode rankingMode, int hideInactiveDays, CancellationToken cancellationToken)
    {
        if (topCount <= 0)
        {
            return null;
        }

        var currentStats = session.CaptureCurrentStats(DateTimeOffset.UtcNow);
        if (currentStats.Kills < minimumKills)
        {
            return null;
        }

        var topSteamIds = await _repository.GetTopSteamIdsAsync(minimumKills, topCount, rankingMode, hideInactiveDays, session.SteamId, cancellationToken);
        for (var index = 0; index < topSteamIds.Count; index++)
        {
            if (string.Equals(topSteamIds[index], session.SteamId, StringComparison.Ordinal))
            {
                return index + 1;
            }
        }

        return null;
    }

    public async Task<int> PruneInactivePlayersAsync(int pruneDays, IReadOnlyCollection<string> excludedSteamIds, CancellationToken cancellationToken)
    {
        var deleted = await _repository.PruneInactivePlayersAsync(pruneDays, excludedSteamIds, cancellationToken);
        if (deleted > 0)
        {
            InvalidateLeaderboardCache();
        }

        return deleted;
    }

    /// <summary>
    /// Drops every cached leaderboard, so a reset or a prune shows up in the next top.
    /// </summary>
    public void InvalidateLeaderboardCache()
    {
        lock (_leaderboardCache)
        {
            _leaderboardCache.Clear();
        }
    }

    public RankServiceStatus GetStatus()
    {
        lock (_leaderboardCache)
        {
            PruneExpiredCacheEntriesUnsafe(DateTimeOffset.UtcNow);
            return new RankServiceStatus(_leaderboardCache.Count, _lastCacheRefreshUtc);
        }
    }

    private async Task<T> GetCachedAsync<T>(string key, TimeSpan cacheTtl, Func<Task<T>> factory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (cacheTtl <= TimeSpan.Zero)
        {
            return await factory();
        }

        // The in-flight task itself is cached, so concurrent callers asking for the
        // same key share one query while a different key is never made to wait.
        Task<object> pending;
        var now = DateTimeOffset.UtcNow;

        lock (_leaderboardCache)
        {
            if (_leaderboardCache.TryGetValue(key, out var cached) && cached.ExpiresAtUtc > now)
            {
                pending = cached.Value;
            }
            else
            {
                PruneExpiredCacheEntriesUnsafe(now);
                pending = InvokeFactoryAsync(factory);
                _leaderboardCache[key] = new CacheEntry(now.Add(cacheTtl), pending);
                _lastCacheRefreshUtc = now;
            }
        }

        try
        {
            return (T)await pending;
        }
        catch
        {
            // A failed query must not stay cached for the rest of the TTL.
            lock (_leaderboardCache)
            {
                if (_leaderboardCache.TryGetValue(key, out var cached) && ReferenceEquals(cached.Value, pending))
                {
                    _leaderboardCache.Remove(key);
                }
            }

            throw;
        }
    }

    private static async Task<object> InvokeFactoryAsync<T>(Func<Task<T>> factory)
    {
        // Yield first so the query never runs while the cache lock is held.
        await Task.Yield();
        return (await factory())!;
    }

    private void PruneExpiredCacheEntriesUnsafe(DateTimeOffset now)
    {
        foreach (var key in _leaderboardCache
                     .Where(entry => entry.Value.ExpiresAtUtc <= now)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            _leaderboardCache.Remove(key);
        }
    }

    private sealed record CacheEntry(DateTimeOffset ExpiresAtUtc, Task<object> Value);
}
