using LoadTestFactory.Notifications;

namespace LoadTestFactory;

public static class LoadTestRunner
{
    public static event EventHandler<LoadTestProgressEventArgs> ProgressChanged;

    public static Task<LoadTestResult> RunAsync(
        int parallelAmount,
        int warmupInSeconds,
        int durationInSeconds,
        TestHandler<int> scenario,
        string workingDirectory = null,
        bool consoleOutput = true,
        CancellationToken cancellationToken = default)
    {
        return LoadTestRunner.RunAsync(
            Enumerable.Range(0, parallelAmount).ToArray(),
            warmupInSeconds,
            durationInSeconds,
            new TestScenario<int>[] { new TestScenario<int>("Test Scenario", 1, scenario) },
            workingDirectory,
            consoleOutput,
            cancellationToken);
    }

    public static async Task<LoadTestResult> RunAsync<T>(
        T[] contexts,
        int warmupInSeconds,
        int durationInSeconds,
        TestScenario<T>[] scenarios,
        string workingDirectory = null,
        bool consoleOutput = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        int parallelAmount = contexts.Length;
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelAmount, 1, nameof(contexts));

        GlobalState.CaptureState();

        LoadTestContext<T> ctx = new LoadTestContext<T>(parallelAmount, warmupInSeconds, durationInSeconds,
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
                T context = contexts[i];
                var iterator = ctx.ScenariosCircle.GetIterator();

                while (!ct.IsCancellationRequested)
                {
                    if (Stopwatch.GetTimestamp() >= measurementStartTimestamp)
                        break;

                    TestScenario<T> scenario = iterator.Next();
                    await scenario.ExecuteSafeAsync(context);
                }

                MetricsCollector currentCollector = ctx.Collectors[i];
                MetricsCollector.Current.Value = currentCollector;

                while (!ct.IsCancellationRequested)
                {
                    if (Stopwatch.GetTimestamp() >= measurementEndTimestamp)
                        break;

                    TestScenario<T> scenario = iterator.Next();
                    await scenario.ExecuteMeasuredAsync(context, currentCollector, measurementEndTimestamp);
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
            try
            {
                await ctx.CleanUp();
            }
            finally
            {
                GlobalState.ReleaseState();
            }
        }
    }

    // Event invocation and subscriber isolation belong to LoadTestProgressMonitor.
    internal static EventHandler<LoadTestProgressEventArgs> GetProgressChangedSubscribers() => ProgressChanged;
}
