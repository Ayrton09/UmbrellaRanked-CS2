using UmbrellaRanked.Models;

namespace UmbrellaRanked.Core;

public sealed class PlayerSession
{
    private readonly object _sync = new();
    private readonly Dictionary<string, WeaponCounters> _weaponStats = new(StringComparer.Ordinal);

    private int _kills;
    private int _deaths;
    private int _assists;
    private int _points;
    private int _headshots;
    private int _mvps;
    private int _roundsWon;
    private int _roundsLost;
    private int _roundsCt;
    private int _roundsT;
    private int _matchesWon;
    private int _matchesLost;
    private int _matchesTied;
    private int _persistedPlaytimeSeconds;
    private int _lastResetUnixTime;
    private DateTimeOffset _playtimeAnchorUtc;
    private bool _hasPendingSave;
    private long _version;
    private readonly SemaphoreSlim _persistenceLock = new(1, 1);

    public PlayerSession(string steamId, ulong steamId64, string initialName)
    {
        SteamId = steamId;
        SteamId64 = steamId64;
        LastKnownName = initialName;
        Slot = -1;
    }

    public string SteamId { get; }

    public ulong SteamId64 { get; }

    public string LastKnownName { get; private set; }

    public int Slot { get; private set; }

    public int? UserId { get; private set; }

    public bool IsConnected { get; private set; }

    public bool IsLoaded { get; private set; }

    public bool IsLoading { get; private set; }

    public bool IsResetInProgress { get; private set; }

    public bool HasPendingSave
    {
        get
        {
            lock (_sync)
            {
                return _hasPendingSave;
            }
        }
    }

    public SemaphoreSlim PersistenceLock => _persistenceLock;

    public bool BeginLoad()
    {
        lock (_sync)
        {
            if (IsLoading || IsLoaded)
            {
                return false;
            }

            IsLoading = true;
            return true;
        }
    }

    public bool Attach(PlayerIdentity identity, DateTimeOffset nowUtc)
    {
        lock (_sync)
        {
            var nameChanged = !string.Equals(LastKnownName, identity.Name, StringComparison.Ordinal);
            var wasConnected = IsConnected;

            LastKnownName = identity.Name;
            Slot = identity.Slot;
            UserId = identity.UserId;
            IsConnected = true;

            if (!wasConnected || _playtimeAnchorUtc == default)
            {
                _playtimeAnchorUtc = nowUtc;
            }

            if (nameChanged && IsLoaded)
            {
                MarkDirtyUnsafe();
            }

            return !wasConnected;
        }
    }

    public void ApplyLoadedData(PlayerRankStats? stats, IReadOnlyCollection<WeaponStatEntry> weaponStats, DateTimeOffset nowUtc)
    {
        lock (_sync)
        {
            var pendingPlaytimeSeconds = GetPreLoadPlaytimeSecondsUnsafe(nowUtc);

            _kills = stats?.Kills ?? 0;
            _deaths = stats?.Deaths ?? 0;
            _assists = stats?.Assists ?? 0;
            _points = stats?.Points ?? 0;
            _headshots = stats?.Headshots ?? 0;
            _mvps = stats?.Mvps ?? 0;
            _roundsWon = stats?.RoundsWon ?? 0;
            _roundsLost = stats?.RoundsLost ?? 0;
            _roundsCt = stats?.RoundsCt ?? 0;
            _roundsT = stats?.RoundsT ?? 0;
            _matchesWon = stats?.MatchesWon ?? 0;
            _matchesLost = stats?.MatchesLost ?? 0;
            _matchesTied = stats?.MatchesTied ?? 0;
            _persistedPlaytimeSeconds = Math.Max(0, (stats?.PlaytimeSeconds ?? 0) + pendingPlaytimeSeconds);
            _lastResetUnixTime = stats?.LastResetUnixTime ?? 0;
            _weaponStats.Clear();

            foreach (var weaponEntry in weaponStats)
            {
                _weaponStats[weaponEntry.Weapon] = new WeaponCounters(weaponEntry.Kills, weaponEntry.Headshots);
            }

            IsLoading = false;
            IsLoaded = true;
            _hasPendingSave = pendingPlaytimeSeconds > 0;
            _version = _hasPendingSave ? 1 : 0;
            _playtimeAnchorUtc = nowUtc;
        }
    }

    public void MarkLoadFailed()
    {
        lock (_sync)
        {
            IsLoading = false;
        }
    }

    public bool MarkDisconnected(DateTimeOffset nowUtc)
    {
        lock (_sync)
        {
            var wasConnected = IsConnected;
            if (IsConnected)
            {
                var previousPlaytimeSeconds = _persistedPlaytimeSeconds;
                _persistedPlaytimeSeconds = GetEffectivePlaytimeSecondsUnsafe(nowUtc);

                // The seconds banked here are not in the database yet. Only a
                // loaded session may be marked dirty: flagging a failed load
                // would keep the session pinned in memory forever.
                if (IsLoaded && _persistedPlaytimeSeconds > previousPlaytimeSeconds)
                {
                    MarkDirtyUnsafe();
                }
            }

            IsConnected = false;
            Slot = -1;
            UserId = null;
            _playtimeAnchorUtc = nowUtc;
            return wasConnected;
        }
    }

    public bool TryApplyKill(string normalizedWeapon, bool headshot, int points)
    {
        lock (_sync)
        {
            if (!IsLoaded || IsResetInProgress)
            {
                return false;
            }

            var headshotIncrement = headshot ? 1 : 0;
            var weapon = _weaponStats.GetValueOrDefault(normalizedWeapon);

            _kills++;
            _headshots += headshotIncrement;
            _points = AddPointsUnsafe(points);
            _weaponStats[normalizedWeapon] = new WeaponCounters(weapon.Kills + 1, weapon.Headshots + headshotIncrement);
            MarkDirtyUnsafe();
            return true;
        }
    }

    public bool TryApplyMvp(int points)
    {
        lock (_sync)
        {
            if (!IsLoaded || IsResetInProgress)
            {
                return false;
            }

            _mvps++;
            _points = AddPointsUnsafe(points);
            MarkDirtyUnsafe();
            return true;
        }
    }

    public bool TryApplyRoundResult(bool playedAsCounterTerrorist, bool won, int points)
    {
        lock (_sync)
        {
            if (!IsLoaded || IsResetInProgress)
            {
                return false;
            }

            if (playedAsCounterTerrorist)
            {
                _roundsCt++;
            }
            else
            {
                _roundsT++;
            }

            if (won)
            {
                _roundsWon++;
            }
            else
            {
                _roundsLost++;
            }

            _points = AddPointsUnsafe(points);
            MarkDirtyUnsafe();
            return true;
        }
    }

    public bool TryApplyMatchResult(MatchResult result)
    {
        lock (_sync)
        {
            if (!IsLoaded || IsResetInProgress)
            {
                return false;
            }

            switch (result)
            {
                case MatchResult.Won:
                    _matchesWon++;
                    break;
                case MatchResult.Lost:
                    _matchesLost++;
                    break;
                default:
                    _matchesTied++;
                    break;
            }

            MarkDirtyUnsafe();
            return true;
        }
    }

    public bool TryApplyDeath(int penaltyPoints)
    {
        lock (_sync)
        {
            if (!IsLoaded || IsResetInProgress)
            {
                return false;
            }

            _deaths++;
            _points = AddPointsUnsafe(-penaltyPoints);
            MarkDirtyUnsafe();
            return true;
        }
    }

    public bool TryApplyAssist(int points)
    {
        lock (_sync)
        {
            if (!IsLoaded || IsResetInProgress)
            {
                return false;
            }

            _assists++;
            _points = AddPointsUnsafe(points);
            MarkDirtyUnsafe();
            return true;
        }
    }

    public bool TryApplyPoints(int points)
    {
        lock (_sync)
        {
            if (!IsLoaded || IsResetInProgress || points == 0)
            {
                return false;
            }

            _points = AddPointsUnsafe(points);
            MarkDirtyUnsafe();
            return true;
        }
    }

    public void UpdateName(string name)
    {
        lock (_sync)
        {
            if (string.Equals(LastKnownName, name, StringComparison.Ordinal))
            {
                return;
            }

            LastKnownName = name;

            if (IsLoaded)
            {
                MarkDirtyUnsafe();
            }
        }
    }

    public PlayerRankStats CaptureCurrentStats(DateTimeOffset nowUtc)
    {
        lock (_sync)
        {
            return new PlayerRankStats
            {
                SteamId = SteamId,
                Name = LastKnownName,
                Kills = _kills,
                Deaths = _deaths,
                Assists = _assists,
                Points = _points,
                Headshots = _headshots,
                Mvps = _mvps,
                RoundsWon = _roundsWon,
                RoundsLost = _roundsLost,
                RoundsCt = _roundsCt,
                RoundsT = _roundsT,
                MatchesWon = _matchesWon,
                MatchesLost = _matchesLost,
                MatchesTied = _matchesTied,
                PlaytimeSeconds = GetEffectivePlaytimeSecondsUnsafe(nowUtc),
                LastSeenUnixTime = (int)nowUtc.ToUnixTimeSeconds(),
                LastResetUnixTime = _lastResetUnixTime
            };
        }
    }

    public PlayerDataSnapshot? CaptureSaveSnapshot(DateTimeOffset nowUtc, bool force)
    {
        lock (_sync)
        {
            if (!IsLoaded || IsResetInProgress)
            {
                return null;
            }

            if (!force && !_hasPendingSave)
            {
                return null;
            }

            return new PlayerDataSnapshot(
                SteamId,
                LastKnownName,
                _kills,
                _deaths,
                _assists,
                _points,
                _headshots,
                _mvps,
                _roundsWon,
                _roundsLost,
                _roundsCt,
                _roundsT,
                _matchesWon,
                _matchesLost,
                _matchesTied,
                GetEffectivePlaytimeSecondsUnsafe(nowUtc),
                (int)nowUtc.ToUnixTimeSeconds(),
                _lastResetUnixTime,
                _weaponStats
                    .Select(entry => new WeaponStatEntry(SteamId, entry.Key, entry.Value.Kills, entry.Value.Headshots))
                    .ToList(),
                _version);
        }
    }

    public void MarkSaveSuccessful(PlayerDataSnapshot snapshot, DateTimeOffset capturedAtUtc)
    {
        lock (_sync)
        {
            // A disconnect can land between capture and completion, so persisted
            // playtime must never move backwards to the snapshot value.
            var creditedSeconds = Math.Max(0, snapshot.PlaytimeSeconds - _persistedPlaytimeSeconds);
            _persistedPlaytimeSeconds += creditedSeconds;

            // Advance the anchor by exactly the whole seconds that were credited so
            // the sub-second remainder carries over instead of being lost per save.
            _playtimeAnchorUtc = IsConnected && _playtimeAnchorUtc != default
                ? _playtimeAnchorUtc.AddSeconds(creditedSeconds)
                : capturedAtUtc;

            if (_version == snapshot.Version)
            {
                _hasPendingSave = false;
            }
        }
    }

    public bool TryBeginReset(DateTimeOffset nowUtc, out ResetRankSnapshot snapshot)
    {
        lock (_sync)
        {
            if (!IsLoaded || IsResetInProgress)
            {
                snapshot = null!;
                return false;
            }

            IsResetInProgress = true;
            snapshot = new ResetRankSnapshot(
                SteamId,
                LastKnownName,
                GetEffectivePlaytimeSecondsUnsafe(nowUtc),
                (int)nowUtc.ToUnixTimeSeconds());

            return true;
        }
    }

    public void CompleteReset(ResetRankSnapshot snapshot, DateTimeOffset nowUtc)
    {
        lock (_sync)
        {
            _kills = 0;
            _deaths = 0;
            _assists = 0;
            _points = 0;
            _headshots = 0;
            _mvps = 0;
            _roundsWon = 0;
            _roundsLost = 0;
            _roundsCt = 0;
            _roundsT = 0;
            _matchesWon = 0;
            _matchesLost = 0;
            _matchesTied = 0;
            _weaponStats.Clear();
            _persistedPlaytimeSeconds = snapshot.PlaytimeSeconds;
            _lastResetUnixTime = snapshot.ResetUnixTime;
            _playtimeAnchorUtc = nowUtc;
            IsResetInProgress = false;
            _hasPendingSave = false;
            _version++;
        }
    }

    public void CancelReset()
    {
        lock (_sync)
        {
            IsResetInProgress = false;
        }
    }

    public bool CanBeRemoved()
    {
        lock (_sync)
        {
            return !IsConnected && !IsLoading && !IsResetInProgress && !_hasPendingSave;
        }
    }

    private int GetEffectivePlaytimeSecondsUnsafe(DateTimeOffset nowUtc)
    {
        return _persistedPlaytimeSeconds + GetElapsedSecondsUnsafe(nowUtc);
    }

    private int GetElapsedSecondsUnsafe(DateTimeOffset nowUtc)
    {
        if (!IsConnected || _playtimeAnchorUtc == default)
        {
            return 0;
        }

        var elapsedSeconds = (int)(nowUtc - _playtimeAnchorUtc).TotalSeconds;
        return elapsedSeconds > 0 && elapsedSeconds < 86400 ? elapsedSeconds : 0;
    }

    private int GetPreLoadPlaytimeSecondsUnsafe(DateTimeOffset nowUtc)
    {
        if (IsLoaded)
        {
            return 0;
        }

        if (!IsConnected)
        {
            return Math.Max(0, _persistedPlaytimeSeconds);
        }

        return GetElapsedSecondsUnsafe(nowUtc);
    }

    private int AddPointsUnsafe(int delta)
    {
        var nextValue = (long)_points + delta;
        return (int)Math.Clamp(nextValue, 0, int.MaxValue);
    }

    private void MarkDirtyUnsafe()
    {
        _hasPendingSave = true;
        _version++;
    }

    private readonly record struct WeaponCounters(int Kills, int Headshots);
}
