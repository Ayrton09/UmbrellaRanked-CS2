namespace UmbrellaRanked.Core;

/// <summary>
/// Who actually played the current round and match. A snapshot of the players on a
/// team is taken when the freeze time ends; round and match results are only paid to
/// players who were in it and are still on the same team, so switching teams after
/// dying, moving to spectator to dodge a loss or joining late earns nothing.
/// Without a snapshot (plugin loaded mid-round) every player counts, as before.
/// Game thread only.
/// </summary>
public sealed class RoundParticipation
{
    private Dictionary<string, int>? _round;
    private readonly HashSet<(string SteamId, int Team)> _match = new();

    public bool HasRoundSnapshot => _round != null;

    public int MatchParticipantCount => _match.Count;

    public void BeginRound(IEnumerable<KeyValuePair<string, int>> playersByTeam)
    {
        var round = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (steamId, team) in playersByTeam)
        {
            round[steamId] = team;
            _match.Add((steamId, team));
        }

        _round = round;
    }

    public void ClearRound()
    {
        _round = null;
    }

    public void ClearMatch()
    {
        _match.Clear();
    }

    public bool CountsForRound(string? steamId, int team)
    {
        if (_round == null)
        {
            return true;
        }

        return steamId != null && _round.TryGetValue(steamId, out var snapshotTeam) && snapshotTeam == team;
    }

    public bool CountsForMatch(string? steamId, int team)
    {
        if (_match.Count == 0)
        {
            return true;
        }

        return steamId != null && _match.Contains((steamId, team));
    }
}
