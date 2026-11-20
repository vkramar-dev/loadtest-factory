using System.Diagnostics;

namespace LoadTestFactory.Notifications;

// Shows one continuously updated console line for a post-measurement stage.
// The timer is used only when the output is an interactive terminal.
internal sealed class ConsoleStageProgress : IAsyncDisposable
{
    private readonly string _phase;
    private readonly long _startedAt;
    private readonly Timer _timer;
    private int _completed;

    internal ConsoleStageProgress(string phase)
    {
        _phase = phase;
        _startedAt = Stopwatch.GetTimestamp();
        ConsoleOutput.WriteStageProgress(_phase, TimeSpan.Zero, finished: false);

        if (!Console.IsOutputRedirected)
            _timer = new Timer(Tick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void Tick(object state)
    {
        if (Volatile.Read(ref _completed) != 0)
            return;

        try
        {
            ConsoleOutput.WriteStageProgress(_phase, Stopwatch.GetElapsedTime(_startedAt), finished: false);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Write(ex, LoadTestRunner.PathToFolder);
        }
    }

    internal async ValueTask CompleteAsync()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        if (_timer != null)
            await _timer.DisposeAsync();

        ConsoleOutput.WriteStageProgress(_phase, Stopwatch.GetElapsedTime(_startedAt), finished: true);
    }

    public ValueTask DisposeAsync() => CompleteAsync();
}
