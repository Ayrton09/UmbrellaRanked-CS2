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

    public async Task EnsurePlayerLoadedAsync(PlayerIdentity identity, CancellationToken cancellationToken)
    {
        var session = _sessionService.Attach(identity, DateTimeOffset.UtcNow);
        if (!session.BeginLoad())
        {
            return;
        }

        try
        {
            var statsTask = _repository.LoadPlayerStatsAsync(identity.SteamId, cancellationToken);
            var weaponTask = _repository.LoadWeaponStatsAsync(identity.SteamId, cancellationToken);

            await Task.WhenAll(statsTask, weaponTask);

            _sessionService.MarkPlayerLoaded(
                session,
                await statsTask,
                await weaponTask,
                DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _sessionService.MarkPlayerLoadFailed(session);
        }
        catch (Exception exception)
        {
            _sessionService.MarkPlayerLoadFailed(session);
            _logger.LogError(exception, "Failed to load rank data for {SteamId}.", identity.SteamId);
        }
    }

    public bool TryRecordKill(CCSPlayerController player, string? rawWeapon, int points)
    {
        if (!_sessionService.TryGetSession(player, out var session))
        {
            return false;
        }

        return session.TryApplyKill(_weaponStatsService.NormalizeWeaponName(rawWeapon), points);
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

    public async Task<bool> ResetRankAsync(PlayerSession session, CancellationToken cancellationToken)
    {
        await session.PersistenceLock.WaitAsync(cancellationToken);

        try
        {
            if (!session.TryBeginReset(DateTimeOffset.UtcNow, out var snapshot))
            {
                return false;
            }

            await _repository.ResetRankAsync(snapshot, cancellationToken);
            session.CompleteReset(snapshot, DateTimeOffset.UtcNow);
            _sessionService.RemoveIfCompleted(session);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            session.CancelReset();
            return false;
        }
        catch (Exception exception)
        {
            session.CancelReset();
            _logger.LogError(exception, "Failed to reset rank data for {SteamId}.", session.SteamId);
            return false;
        }
        finally
        {
            session.PersistenceLock.Release();
        }
    }

    public Task<int?> GetRankPositionAsync(string steamId, int minimumKills, RankingMode rankingMode, CancellationToken cancellationToken)
    {
        return _repository.GetRankPositionAsync(steamId, minimumKills, rankingMode, cancellationToken);
    }

    public Task<IReadOnlyList<TopEntry>> GetTopPlayersAsync(int minimumKills, int limit, RankingMode rankingMode, CancellationToken cancellationToken)
    {
        return _repository.GetTopPlayersAsync(minimumKills, limit, rankingMode, cancellationToken);
    }

    public Task<IReadOnlyList<TopEntry>> GetTopPlayersCachedAsync(int minimumKills, int limit, RankingMode rankingMode, TimeSpan cacheTtl, CancellationToken cancellationToken)
    {
        return GetCachedAsync(
            $"top:players:{minimumKills}:{limit}:{rankingMode}",
            cacheTtl,
            () => _repository.GetTopPlayersAsync(minimumKills, limit, rankingMode, cancellationToken),
            cancellationToken);
    }

    public Task<IReadOnlyList<PlaytimeTopEntry>> GetTopPlaytimeAsync(int limit, CancellationToken cancellationToken)
    {
        return _repository.GetTopPlaytimeAsync(limit, cancellationToken);
    }

    public Task<IReadOnlyList<PlaytimeTopEntry>> GetTopPlaytimeCachedAsync(int limit, TimeSpan cacheTtl, CancellationToken cancellationToken)
    {
        return GetCachedAsync(
            $"top:playtime:{limit}",
            cacheTtl,
            () => _repository.GetTopPlaytimeAsync(limit, cancellationToken),
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

    public Task<IReadOnlyList<WeaponTopEntry>> GetTopWeaponAsync(string weapon, int limit, CancellationToken cancellationToken)
    {
        return _repository.GetTopWeaponAsync(weapon, limit, cancellationToken);
    }

    public Task<IReadOnlyList<WeaponTopEntry>> GetTopWeaponCachedAsync(string weapon, int limit, TimeSpan cacheTtl, CancellationToken cancellationToken)
    {
        return GetCachedAsync(
            $"top:weapon:{weapon}:{limit}",
            cacheTtl,
            () => _repository.GetTopWeaponAsync(weapon, limit, cancellationToken),
            cancellationToken);
    }

    public Task<IReadOnlyList<WeaponTopEntry>> GetTopWeaponCategoryCachedAsync(IReadOnlyCollection<string> weapons, int limit, TimeSpan cacheTtl, CancellationToken cancellationToken)
    {
        var keyWeapons = string.Join(",", weapons.OrderBy(weapon => weapon, StringComparer.Ordinal));
        return GetCachedAsync(
            $"top:weapon-category:{keyWeapons}:{limit}",
            cacheTtl,
            () => _repository.GetTopWeaponCategoryAsync(weapons, limit, cancellationToken),
            cancellationToken);
    }

    public async Task<int?> GetWelcomePositionAsync(PlayerSession session, int minimumKills, int topCount, RankingMode rankingMode, CancellationToken cancellationToken)
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

        var topSteamIds = await _repository.GetTopSteamIdsAsync(minimumKills, topCount, rankingMode, cancellationToken);
        for (var index = 0; index < topSteamIds.Count; index++)
        {
            if (string.Equals(topSteamIds[index], session.SteamId, StringComparison.Ordinal))
            {
                return index + 1;
            }
        }

        return null;
    }

    public Task<int> PruneInactivePlayersAsync(int pruneDays, IReadOnlyCollection<string> excludedSteamIds, CancellationToken cancellationToken)
    {
        return _repository.PruneInactivePlayersAsync(pruneDays, excludedSteamIds, cancellationToken);
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
