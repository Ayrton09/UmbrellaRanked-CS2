using System.Collections.Concurrent;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using UmbrellaRanked.Models;
using UmbrellaRanked.Utils;

namespace UmbrellaRanked.Core;

public sealed class PlayerSessionService
{
    private readonly ConcurrentDictionary<string, PlayerSession> _sessionsBySteamId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, string> _steamIdsBySlot = new();
    private readonly ILogger _logger;
    private int _connectedPlayerCount;

    public PlayerSessionService(ILogger logger)
    {
        _logger = logger;
    }

    public PlayerSession Attach(PlayerIdentity identity, DateTimeOffset nowUtc)
    {
        var session = _sessionsBySteamId.GetOrAdd(
            identity.SteamId,
            _ => new PlayerSession(identity.SteamId, identity.SteamId64, identity.Name));

        if (session.Attach(identity, nowUtc))
        {
            Interlocked.Increment(ref _connectedPlayerCount);
        }

        _steamIdsBySlot[identity.Slot] = identity.SteamId;
        return session;
    }

    public bool TryResolveIdentity(CCSPlayerController? player, out PlayerIdentity identity)
    {
        identity = null!;

        if (!IsRealPlayer(player))
        {
            return false;
        }

        var steamId64 = TryGetSteamId64(player!);
        if (steamId64 == 0)
        {
            return false;
        }

        identity = new PlayerIdentity(
            steamId64,
            SteamIdConverter.ToSteam2(steamId64),
            StringSanitizer.SanitizePlayerName(player!.PlayerName),
            player.Slot,
            player.UserId);

        return true;
    }

    public bool TryGetSession(CCSPlayerController? player, out PlayerSession session)
    {
        session = null!;

        if (player == null || !player.IsValid)
        {
            return false;
        }

        if (_steamIdsBySlot.TryGetValue(player.Slot, out var steamId) &&
            steamId != null &&
            _sessionsBySteamId.TryGetValue(steamId, out var existingSession))
        {
            if (IsSameLivePlayer(player, existingSession))
            {
                session = existingSession;
                return true;
            }

            _steamIdsBySlot.TryRemove(player.Slot, out _);
        }

        if (!TryResolveIdentity(player, out var identity))
        {
            return false;
        }

        if (!_sessionsBySteamId.TryGetValue(identity.SteamId, out var matchedSession))
        {
            return false;
        }

        session = matchedSession;
        if (session.Attach(identity, DateTimeOffset.UtcNow))
        {
            Interlocked.Increment(ref _connectedPlayerCount);
        }

        _steamIdsBySlot[player.Slot] = identity.SteamId;
        return true;
    }

    public bool TryGetSessionBySteamId(string steamId, out PlayerSession session)
    {
        return _sessionsBySteamId.TryGetValue(steamId, out session!);
    }

    public bool TryGetSessionBySlot(int slot, out PlayerSession session)
    {
        session = null!;

        if (_steamIdsBySlot.TryGetValue(slot, out var steamId) &&
            steamId != null &&
            _sessionsBySteamId.TryGetValue(steamId, out var matchedSession))
        {
            session = matchedSession;
            return true;
        }

        return false;
    }

    public bool TryGetConnectedPlayer(PlayerSession session, out CCSPlayerController player)
    {
        player = null!;

        if (session.Slot < 0)
        {
            return false;
        }

        CCSPlayerController? current;
        try
        {
            current = Utilities.GetPlayerFromSlot(session.Slot);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Unable to resolve connected player for slot {Slot}.", session.Slot);
            return false;
        }

        if (!IsRealPlayer(current))
        {
            return false;
        }

        if (current!.UserId != session.UserId)
        {
            return false;
        }

        var steamId64 = TryGetSteamId64(current);
        if (steamId64 == 0 || SteamIdConverter.ToSteam2(steamId64) != session.SteamId)
        {
            return false;
        }

        player = current;
        return true;
    }

    public void MarkPlayerLoaded(PlayerSession session, PlayerRankStats? stats, IReadOnlyCollection<WeaponStatEntry> weapons, DateTimeOffset nowUtc)
    {
        session.ApplyLoadedData(stats, weapons, nowUtc);
    }

    public void MarkPlayerLoadFailed(PlayerSession session)
    {
        session.MarkLoadFailed();
    }

    public void HandleDisconnect(int slot, DateTimeOffset nowUtc)
    {
        if (!_steamIdsBySlot.TryRemove(slot, out var steamId))
        {
            return;
        }

        if (_sessionsBySteamId.TryGetValue(steamId, out var session))
        {
            if (session.MarkDisconnected(nowUtc))
            {
                Interlocked.Decrement(ref _connectedPlayerCount);
            }
        }
    }

    public int GetConnectedPlayerCount()
    {
        return Math.Max(0, Volatile.Read(ref _connectedPlayerCount));
    }

    public SessionServiceStatus GetStatus()
    {
        var sessions = _sessionsBySteamId.Values.ToArray();
        return new SessionServiceStatus(
            GetConnectedPlayerCount(),
            sessions.Length,
            sessions.Count(session => session.IsLoaded),
            sessions.Count(session => session.IsLoading),
            sessions.Count(session => session.HasPendingSave));
    }

    public void UpdateName(CCSPlayerController? player, string? name)
    {
        if (!TryGetSession(player, out var session))
        {
            return;
        }

        session.UpdateName(StringSanitizer.SanitizePlayerName(name));
    }

    public IReadOnlyList<PlayerSession> GetSaveCandidates(bool includeDisconnected, bool force)
    {
        return _sessionsBySteamId.Values
            .Where(session => session.IsLoaded)
            .Where(session => !session.IsResetInProgress)
            .Where(session => includeDisconnected || session.IsConnected)
            .Where(session => force || session.HasPendingSave)
            .ToList();
    }

    public IReadOnlyCollection<string> GetConnectedSteamIds()
    {
        return _sessionsBySteamId.Values
            .Where(session => session.IsConnected)
            .Select(session => session.SteamId)
            .ToArray();
    }

    public void RemoveIfCompleted(PlayerSession session)
    {
        if (!session.CanBeRemoved())
        {
            return;
        }

        _sessionsBySteamId.TryRemove(session.SteamId, out _);
    }

    public IEnumerable<PlayerSession> GetAllSessions()
    {
        return _sessionsBySteamId.Values;
    }

    private static bool IsRealPlayer(CCSPlayerController? player)
    {
        return player != null && player.IsValid && !player.IsBot;
    }

    private ulong TryGetSteamId64(CCSPlayerController player)
    {
        try
        {
            if (player.AuthorizedSteamID is { } authorizedSteamId)
            {
                return authorizedSteamId.SteamId64;
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "AuthorizedSteamID lookup failed for slot {Slot}", player.Slot);
        }

        try
        {
            return player.SteamID;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "SteamID lookup failed for slot {Slot}", player.Slot);
        }

        return 0;
    }

    private bool IsSameLivePlayer(CCSPlayerController player, PlayerSession session)
    {
        if (!IsRealPlayer(player) ||
            !session.IsConnected ||
            session.Slot != player.Slot ||
            session.UserId != player.UserId)
        {
            return false;
        }

        var steamId64 = TryGetSteamId64(player);
        return steamId64 != 0 && string.Equals(SteamIdConverter.ToSteam2(steamId64), session.SteamId, StringComparison.Ordinal);
    }
}
