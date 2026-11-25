using System.Diagnostics;
using static LoadTestFactory.LoadTestContext;

namespace LoadTestFactory.Notifications;

// Progress sampling and presentation are independent of load-generating workers.
internal sealed class LoadTestProgressMonitor : IAsyncDisposable
{
    private readonly MetricsCollector[] _collectors;
    private readonly long _runStart;
    private readonly long _measurementStart;
    private readonly long _measurementEnd;
    private readonly int _measurementDurationSeconds;
    private readonly bool _consoleOutput;
    private readonly bool _measureScenarios;
    private readonly Timer _timer;

    private long _lastMeasurementSample;
    private long _lastCompleted;
    private int _notifying;
    private bool _warmupCompletionPublished;

    internal LoadTestProgressMonitor(
        MetricsCollector[] collectors,
        long runStart,
        long measurementStart,
        long measurementEnd,
        int measurementDurationSeconds,
        bool consoleOutput,
        bool measureScenarios)
    {
        _collectors = collectors;
        _runStart = runStart;
        _measurementStart = measurementStart;
        _measurementEnd = measurementEnd;
        _measurementDurationSeconds = measurementDurationSeconds;
        _consoleOutput = consoleOutput;
        _measureScenarios = measureScenarios;
        _lastMeasurementSample = measurementStart;

        _timer = new Timer(TimerCallback, null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal void Start()
    {
        // Schedule the initial notification asynchronously, so a slow event
        // subscriber cannot delay the start of load-generating workers.
        _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    private void TimerCallback(object state)
    {
        // Timer callbacks can overlap when a subscriber is slow.
        if (Interlocked.CompareExchange(ref _notifying, 1, 0) != 0)
            return;

        try
        {
            Publish(isFinal: false);
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
        finally
        {
            Volatile.Write(ref _notifying, 0);
        }
    }

    private void Publish(bool isFinal)
    {
        long now = Stopwatch.GetTimestamp();

        // Make sure Warmup finishes at 100% on its own line, even if a timer
        // tick lands just after the boundary or a callback was delayed.
        if (!_warmupCompletionPublished && _measurementStart > _runStart &&
            (isFinal || now >= _measurementStart))
        {
            _warmupCompletionPublished = true;
            TimeSpan warmupDuration = Stopwatch.GetElapsedTime(_runStart, _measurementStart);
            Notify(new LoadTestProgressEventArgs
            {
                Phase = LoadTestPhase.Warmup,
                Elapsed = warmupDuration,
                Duration = warmupDuration,
                Percentage = 100
            });
        }

        bool warmup = !isFinal && now < _measurementStart;
        LoadTestPhase phase = isFinal ? LoadTestPhase.Completed
            : warmup ? LoadTestPhase.Warmup
            : LoadTestPhase.Measurement;

        long start = warmup ? _runStart : _measurementStart;
        long end = warmup ? _measurementStart : _measurementEnd;
        long sampledNow = Math.Clamp(now, start, end);
        double durationSeconds = (end - start) / (double)Stopwatch.Frequency;
        double elapsedSeconds = (sampledNow - start) / (double)Stopwatch.Frequency;

        long completed = 0;
        long failed = 0;
        double rps = 0;

        if (!warmup)
        {
            foreach (MetricsCollector collector in _collectors)
            {
                // The worker publishes completed before failed.
                failed += collector.GetFailedExecutions();
                completed += collector.GetCompletedExecutions();
            }

            if (isFinal)
            {
                // The final notification reports mean RPS across the configured
                // measurement window, not the potentially tiny last timer interval.
                rps = completed / (double)_measurementDurationSeconds;
            }
            else
            {
                long intervalTicks = sampledNow - _lastMeasurementSample;
                if (intervalTicks > 0)
                    rps = (completed - _lastCompleted) * Stopwatch.Frequency / (double)intervalTicks;

                _lastCompleted = completed;
                _lastMeasurementSample = sampledNow;
            }
        }

        Notify(new LoadTestProgressEventArgs
        {
            Phase = phase,
            Elapsed = TimeSpan.FromSeconds(elapsedSeconds),
            Duration = TimeSpan.FromSeconds(durationSeconds),
            Percentage = durationSeconds > 0 ? Math.Clamp(elapsedSeconds / durationSeconds * 100, 0, 100) : 100,
            Completed = completed,
            Failed = failed,
            Rps = rps
        });
    }

    private void Notify(LoadTestProgressEventArgs args)
    {
        if (_consoleOutput)
        {
            try
            {
                ConsoleOutput.WriteProgress(args, _measureScenarios);
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
        }

        // A failing subscriber must not prevent other subscribers from receiving events.
        EventHandler<LoadTestProgressEventArgs> handlers = LoadTestRunner.GetProgressChangedSubscribers();
        if (handlers == null)
            return;

        foreach (Delegate subscriber in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<LoadTestProgressEventArgs>)subscriber)(null, args);
            }
            catch (Exception ex)
            {
                LogError(new InvalidOperationException("A ProgressChanged event handler failed.", ex));
            }
        }
    }

    internal async ValueTask CompleteAsync()
    {
        // Wait for any running timer callback before sending the final notification.
        await _timer.DisposeAsync();
        try
        {
            Publish(isFinal: true);
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    private static void LogError(Exception ex) =>
        DiagnosticLogger.Write(ex, GlobalState.PathToFolder);

    public ValueTask DisposeAsync() => _timer.DisposeAsync();
}
