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
    private CancellationTokenSource _runCancellation = new();
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
        _runCancellation = new CancellationTokenSource();

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

        // Lets an autosave that is already running stop between sessions, so the
        // final flush on unload does not have to wait behind it.
        _runCancellation.Cancel();
    }

    /// <returns><c>true</c> when every session was saved.</returns>
    public async Task<bool> FlushAsync(bool force, bool includeDisconnected, CancellationToken cancellationToken)
    {
        await _flushLock.WaitAsync(cancellationToken);

        try
        {
            var saveCandidates = _sessionService.GetSaveCandidates(includeDisconnected, force);
            if (await _rankService.SaveSessionsAsync(saveCandidates, force, cancellationToken))
            {
                MarkSuccess();
                return true;
            }

            MarkPartialFailure();
            return false;
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
        // _flushLock is deliberately not disposed: an autosave still finishing on the
        // thread pool releases it afterwards, and releasing a disposed semaphore throws
        // in a task nobody observes. It never allocates a wait handle, so nothing leaks.
        Stop();
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

        // Task.Run: SQLite executes synchronously and this timer fires on the game thread.
        var cancellationToken = _runCancellation.Token;
        _ = Task.Run(() => ExecuteAutosaveAsync(cancellationToken));
    }

    private async Task ExecuteAutosaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            var saveCandidates = _sessionService.GetSaveCandidates(includeDisconnected: true, force: true);
            if (await _rankService.SaveSessionsAsync(saveCandidates, force: true, cancellationToken))
            {
                MarkSuccess();
            }
            else
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    MarkPartialFailure();
                    _logger.LogWarning("Autosave completed with at least one session that could not be saved.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped for unload or restart; the final flush saves what is left.
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

    private void MarkPartialFailure()
    {
        _lastFailureUtc = DateTimeOffset.UtcNow;
        _lastError = "One or more sessions could not be saved. See the server log for details.";
    }

    private void MarkFailure(Exception exception)
    {
        _lastFailureUtc = DateTimeOffset.UtcNow;
        _lastError = exception.Message;
    }
}
