using CounterStrikeSharp.API;

namespace UmbrellaRanked.Core;

public sealed class CommandCooldownService
{
    private const string LoadingNoticePrefix = "loading:";

    private readonly object _sync = new();
    private readonly Dictionary<string, double> _lastCommandAt = new(StringComparer.Ordinal);

    public bool TryConsume(string playerKey, double cooldownSeconds, out double remainingSeconds)
    {
        lock (_sync)
        {
            return TryConsumeUnsafe(playerKey, cooldownSeconds, out remainingSeconds);
        }
    }

    /// <summary>
    /// Rate limits the "your data is still loading" reply on a key of its own so
    /// a rejected command never consumes the player's real command cooldown.
    /// </summary>
    public bool TryConsumeLoadingNotice(string playerKey, double cooldownSeconds)
    {
        lock (_sync)
        {
            return TryConsumeUnsafe(LoadingNoticePrefix + playerKey, cooldownSeconds, out _);
        }
    }

    public void Clear(string playerKey)
    {
        lock (_sync)
        {
            _lastCommandAt.Remove(playerKey);
            _lastCommandAt.Remove(LoadingNoticePrefix + playerKey);
        }
    }

    private bool TryConsumeUnsafe(string key, double cooldownSeconds, out double remainingSeconds)
    {
        var now = Server.EngineTime;
        if (_lastCommandAt.TryGetValue(key, out var lastCommandAt))
        {
            var elapsed = now - lastCommandAt;
            if (elapsed < cooldownSeconds)
            {
                remainingSeconds = cooldownSeconds - elapsed;
                return false;
            }
        }

        _lastCommandAt[key] = now;
        remainingSeconds = 0;
        return true;
    }
}
