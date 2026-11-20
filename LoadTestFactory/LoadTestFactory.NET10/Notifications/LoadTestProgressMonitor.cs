using System.Diagnostics;

namespace LoadTestFactory.Notifications;

// Progress sampling and presentation are independent of load-generating workers.
internal sealed class LoadTestProgressMonitor : IAsyncDisposable
{
    private readonly MetricsCollector[] _collectors;
    private readonly long _runStart;
    private readonly long _measurementStart;
    private readonly long _measurementEnd;
    private readonly bool _consoleOutput;
    private readonly Timer _timer;

    private long _lastMeasurementSample;
    private long _lastCompleted;
    private int _notifying;

    internal LoadTestProgressMonitor(
        MetricsCollector[] collectors,
        long runStart,
        long measurementStart,
        long measurementEnd,
        bool consoleOutput)
    {
        _collectors = collectors;
        _runStart = runStart;
        _measurementStart = measurementStart;
        _measurementEnd = measurementEnd;
        _consoleOutput = consoleOutput;
        _lastMeasurementSample = measurementStart;

        _timer = new Timer(TimerCallback, null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal void Start()
    {
        _timer.Change(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
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

            long intervalTicks = sampledNow - _lastMeasurementSample;
            if (intervalTicks > 0)
                rps = (completed - _lastCompleted) * Stopwatch.Frequency / (double)intervalTicks;

            _lastCompleted = completed;
            _lastMeasurementSample = sampledNow;
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
                ConsoleOutput.WriteProgress(args);
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

    internal void WriteGcPauseSummary(int count)
    {
        if (!_consoleOutput)
            return;

        try
        {
            ConsoleOutput.WriteMetric("GC pauses:", count.ToString("N0"));
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    internal void WriteWarnings(IEnumerable<LoadTestWarning> warnings)
    {
        if (!_consoleOutput)
            return;

        try
        {
            ConsoleOutput.WriteWarnings(warnings);
        }
        catch (Exception ex)
        {
            LogError(ex);
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
        DiagnosticLogger.Write(ex, LoadTestRunner.PathToFolder);

    public ValueTask DisposeAsync() => _timer.DisposeAsync();
}
