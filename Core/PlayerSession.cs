using UmbrellaRanked.Models;

namespace UmbrellaRanked.Core;

public sealed class PlayerSession
{
    private readonly object _sync = new();
    private readonly Dictionary<string, int> _weaponKills = new(StringComparer.Ordinal);

    private int _kills;
    private int _deaths;
    private int _assists;
    private int _points;
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
            _persistedPlaytimeSeconds = Math.Max(0, (stats?.PlaytimeSeconds ?? 0) + pendingPlaytimeSeconds);
            _lastResetUnixTime = stats?.LastResetUnixTime ?? 0;
            _weaponKills.Clear();

            foreach (var weaponEntry in weaponStats)
            {
                _weaponKills[weaponEntry.Weapon] = weaponEntry.Kills;
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
                _persistedPlaytimeSeconds = GetEffectivePlaytimeSecondsUnsafe(nowUtc);
            }

            IsConnected = false;
            Slot = -1;
            UserId = null;
            _playtimeAnchorUtc = nowUtc;
            return wasConnected;
        }
    }

    public bool TryApplyKill(string normalizedWeapon, int points)
    {
        lock (_sync)
        {
            if (!IsLoaded || IsResetInProgress)
            {
                return false;
            }

            _kills++;
            _points = AddPointsUnsafe(points);
            _weaponKills[normalizedWeapon] = _weaponKills.GetValueOrDefault(normalizedWeapon) + 1;
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
            return new PlayerRankStats(
                SteamId,
                LastKnownName,
                _kills,
                _deaths,
                _assists,
                _points,
                GetEffectivePlaytimeSecondsUnsafe(nowUtc),
                (int)nowUtc.ToUnixTimeSeconds(),
                _lastResetUnixTime);
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
                GetEffectivePlaytimeSecondsUnsafe(nowUtc),
                (int)nowUtc.ToUnixTimeSeconds(),
                _lastResetUnixTime,
                _weaponKills
                    .Select(entry => new WeaponStatEntry(SteamId, entry.Key, entry.Value))
                    .ToList(),
                _version);
        }
    }

    public void MarkSaveSuccessful(PlayerDataSnapshot snapshot, DateTimeOffset nowUtc)
    {
        lock (_sync)
        {
            _persistedPlaytimeSeconds = snapshot.PlaytimeSeconds;
            _playtimeAnchorUtc = nowUtc;

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
            _weaponKills.Clear();
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
        var total = _persistedPlaytimeSeconds;

        if (!IsConnected || _playtimeAnchorUtc == default)
        {
            return total;
        }

        var elapsedSeconds = (int)(nowUtc - _playtimeAnchorUtc).TotalSeconds;
        if (elapsedSeconds > 0 && elapsedSeconds < 86400)
        {
            total += elapsedSeconds;
        }

        return total;
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

        if (_playtimeAnchorUtc == default)
        {
            return 0;
        }

        var elapsedSeconds = (int)(nowUtc - _playtimeAnchorUtc).TotalSeconds;
        return elapsedSeconds > 0 && elapsedSeconds < 86400 ? elapsedSeconds : 0;
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
}
