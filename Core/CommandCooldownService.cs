using CounterStrikeSharp.API;

namespace UmbrellaRanked.Core;

public sealed class CommandCooldownService
{
    private readonly object _sync = new();
    private readonly Dictionary<string, double> _lastCommandAt = new(StringComparer.Ordinal);

    public bool TryConsume(string playerKey, double cooldownSeconds, out double remainingSeconds)
    {
        lock (_sync)
        {
            var now = Server.EngineTime;
            if (_lastCommandAt.TryGetValue(playerKey, out var lastCommandAt))
            {
                var elapsed = now - lastCommandAt;
                if (elapsed < cooldownSeconds)
                {
                    remainingSeconds = cooldownSeconds - elapsed;
                    return false;
                }
            }

            _lastCommandAt[playerKey] = now;
            remainingSeconds = 0;
            return true;
        }
    }

    public void Clear(string playerKey)
    {
        lock (_sync)
        {
            _lastCommandAt.Remove(playerKey);
        }
    }
}
