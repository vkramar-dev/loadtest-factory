global using Xunit;
using System.Diagnostics;
using LoadTestFactory.Measurements;
using LoadTestFactory.Metrics;

// MeasuredCall has a process-wide ID registry and the public runner permits one run at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace LoadTestFactory.Tests;

internal sealed class TestRun : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("lte-tests-");
    private readonly MetricsCollector previous = MetricsCollector.Current.Value;

    public long Start { get; } = Stopwatch.GetTimestamp();
    public MetricsCollector Collector { get; }

    public TestRun()
    {
        Collector = new MetricsCollector(0, directory.FullName);
        MetricsCollector.Current.Value = Collector;
    }

    public Task<MetricsProcessingResult> ReadAsync(params TestScenario[] scenarios)
    {
        // Include all calls just made, even if a debugger or the scheduler paused a test.
        int seconds = checked((int)((Stopwatch.GetTimestamp() - Start) / Stopwatch.Frequency) + 1);
        return ReadWindowAsync(seconds, scenarios);
    }

    public Task<MetricsProcessingResult> ReadWindowAsync(int seconds, params TestScenario[] scenarios)
    {
        Collector.Complete();
        return MetricsProcessor.ProcessAsync([Collector], scenarios, Start,
            Start + seconds * Stopwatch.Frequency, seconds);
    }

    public void Dispose()
    {
        MetricsCollector.Current.Value = previous;
        Collector.Dispose();
        directory.Delete(recursive: true);
    }
}
