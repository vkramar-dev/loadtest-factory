using LoadTestFactory.Metrics;
using LoadTestFactory.Notifications;
using LoadTestFactory.Reports;
using System.Diagnostics;
using static LoadTestFactory.LoadTestContext;

namespace LoadTestFactory;

public static class LoadTestRunner
{
    public static event EventHandler<LoadTestProgressEventArgs> ProgressChanged;

    public static Task<LoadTestResult> RunAsync(
        int parallelAmount,
        int warmupInSeconds,
        int durationInSeconds,
        TestHandler scenario,
        string workingDirectory = null,
        bool consoleOutput = true,
        CancellationToken cancellationToken = default)
    {
        return LoadTestRunner.RunAsync(
            parallelAmount,
            warmupInSeconds,
            durationInSeconds,
            new TestScenario[] { new TestScenario("Test Scenario", 1, scenario) },
            workingDirectory,
            consoleOutput,
            cancellationToken);
    }

    public static async Task<LoadTestResult> RunAsync(
        int parallelAmount,
        int warmupInSeconds,
        int durationInSeconds,
        TestScenario[] scenarios,
        string workingDirectory = null,
        bool consoleOutput = true,
        CancellationToken cancellationToken = default)
    {
        GlobalState.CaptureState();

        LoadTestContext ctx = new LoadTestContext(parallelAmount, warmupInSeconds, durationInSeconds,
                scenarios, workingDirectory, consoleOutput, cancellationToken);

        try
        {
            ctx.InitializeAndStartApp();

            ctx.StartMonitoringGC();

            ctx.CalibrateTimestamps();
            long measurementStartTimestamp = ctx.MeasurementStartTimestamp;
            long measurementEndTimestamp = ctx.MeasurementEndTimestamp;

            ctx.StartProgressMonitor();

            ParallelOptions options = new()
            {
                MaxDegreeOfParallelism = parallelAmount,
                CancellationToken = cancellationToken,
            };

            await Parallel.ForAsync<int>(0, parallelAmount, options, async (i, ct) =>
            {
                var iterator = ctx.ScenariosCircle.GetIterator();

                while (!ct.IsCancellationRequested)
                {
                    if (Stopwatch.GetTimestamp() >= measurementStartTimestamp)
                        break;

                    TestScenario scenario = iterator.Next();
                    await scenario.ExecuteSafeAsync();
                }

                MetricsCollector currentCollector = ctx.Collectors[i];
                MetricsCollector.Current.Value = currentCollector;

                while (!ct.IsCancellationRequested)
                {
                    if (Stopwatch.GetTimestamp() >= measurementEndTimestamp)
                        break;

                    TestScenario scenario = iterator.Next();
                    await scenario.ExecuteMeasuredAsync(currentCollector, measurementEndTimestamp);
                }
            });

            // Workers are finished. A completion after the configured measurement
            // end is stored in CompletionTail, not the normal statistics.
            await ctx.ProgressMonitor.CompleteAsync();

            GCPause[] pauses = ctx.GetGCPauses();
            ctx.ProgressMonitor = null; // CompleteAsync already disposed its timer.

            ctx.AddWarningsIfExists();

            await ctx.CompleteCollectors();

            LoadTestResult result = await ctx.CompileLoadTestResult();

            ctx.WriteResultIntoConsole();

            ctx.SaveReportResult();

            return result;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Write(ex, GlobalState.PathToFolder ?? workingDirectory);
            throw;
        }
        finally
        {
            GlobalState.ReleaseState();
            await ctx.CleanUp();
        }
    }

    // Event invocation and subscriber isolation belong to LoadTestProgressMonitor.
    internal static EventHandler<LoadTestProgressEventArgs> GetProgressChangedSubscribers() => ProgressChanged;
}
