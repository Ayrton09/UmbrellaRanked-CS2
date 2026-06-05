using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using UmbrellaRanked.Models;
using CssTimer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace UmbrellaRanked.Core;

public sealed class AutosaveService : IDisposable
{
    private readonly BasePlugin _plugin;
    private readonly PlayerSessionService _sessionService;
    private readonly RankService _rankService;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _flushLock = new(1, 1);

    private CssTimer? _autosaveTimer;
    private DateTimeOffset? _lastSuccessUtc;
    private DateTimeOffset? _lastFailureUtc;
    private string _lastError = string.Empty;

    public AutosaveService(
        BasePlugin plugin,
        PlayerSessionService sessionService,
        RankService rankService,
        ILogger logger)
    {
        _plugin = plugin;
        _sessionService = sessionService;
        _rankService = rankService;
        _logger = logger;
    }

    public void Restart(double intervalSeconds)
    {
        Stop();

        if (intervalSeconds <= 0)
        {
            return;
        }

        _autosaveTimer = _plugin.AddTimer((float)intervalSeconds, TriggerAutosave, TimerFlags.REPEAT);
    }

    public void Stop()
    {
        _autosaveTimer?.Kill();
        _autosaveTimer = null;
    }

    public async Task FlushAsync(bool force, bool includeDisconnected, CancellationToken cancellationToken)
    {
        await _flushLock.WaitAsync(cancellationToken);

        try
        {
            var saveCandidates = _sessionService.GetSaveCandidates(includeDisconnected, force);
            await _rankService.SaveSessionsAsync(saveCandidates, force, cancellationToken);
            MarkSuccess();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            MarkFailure(exception);
            throw;
        }
        finally
        {
            _flushLock.Release();
        }
    }

    public void Dispose()
    {
        Stop();
        _flushLock.Dispose();
    }

    public AutosaveStatus GetStatus()
    {
        return new AutosaveStatus(
            _autosaveTimer != null,
            _lastSuccessUtc,
            _lastFailureUtc,
            _lastError);
    }

    private void TriggerAutosave()
    {
        if (!_flushLock.Wait(0))
        {
            _logger.LogDebug("Skipping autosave because another flush is already running.");
            return;
        }

        _ = ExecuteAutosaveAsync();
    }

    private async Task ExecuteAutosaveAsync()
    {
        try
        {
            var saveCandidates = _sessionService.GetSaveCandidates(includeDisconnected: true, force: true);
            await _rankService.SaveSessionsAsync(saveCandidates, force: true, CancellationToken.None);
            MarkSuccess();
        }
        catch (Exception exception)
        {
            MarkFailure(exception);
            _logger.LogError(exception, "Autosave failed.");
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private void MarkSuccess()
    {
        _lastSuccessUtc = DateTimeOffset.UtcNow;
        _lastError = string.Empty;
    }

    private void MarkFailure(Exception exception)
    {
        _lastFailureUtc = DateTimeOffset.UtcNow;
        _lastError = exception.Message;
    }
}
