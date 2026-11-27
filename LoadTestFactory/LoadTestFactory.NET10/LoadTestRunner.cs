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
        T[] parallelContexts,
        int warmupInSeconds,
        int durationInSeconds,
        TestScenario<T>[] scenarios,
        string workingDirectory = null,
        bool consoleOutput = true,
        CancellationToken cancellationToken = default)
    {
        GlobalState.CaptureState();

        LoadTestContext<T> ctx = new LoadTestContext<T>(parallelContexts, warmupInSeconds, durationInSeconds,
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
                MaxDegreeOfParallelism = parallelContexts.Length,
                CancellationToken = cancellationToken,
            };

            await Parallel.ForAsync<int>(0, parallelContexts.Length, options, async (i, ct) =>
            {
                T context = parallelContexts[i];
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

    public static async Task<SteppedLoadTestResult> RunSteppedLoadAsync<T>(
        T[] parallelContexts,
        int warmupInSeconds,
        int durationInSeconds,
        int stepAmount,
        TestScenario<T>[] scenarios,
        string workingDirectory = null,
        bool consoleOutput = true,
        CancellationToken cancellationToken = default)
    {
        GlobalState.CaptureState();

        LoadTestContext<T> ctx = new LoadTestContext<T>(parallelContexts, warmupInSeconds, durationInSeconds,
                scenarios, workingDirectory, consoleOutput, cancellationToken);

        try
        {
            ctx.InitializeAndStartApp();

            ctx.StartMonitoringGC();

            ctx.CalibrateTimestamps();
            long measurementStartTimestamp = ctx.MeasurementStartTimestamp;
            long measurementEndTimestamp = ctx.MeasurementEndTimestamp;

            ctx.StartProgressMonitor();

            int[] delays = LoadTestContext<T>.GetDelaysForEachParallel(
                parallelContexts.Length, stepAmount, durationInSeconds);

            ParallelOptions options = new()
            {
                MaxDegreeOfParallelism = parallelContexts.Length,
                CancellationToken = cancellationToken,
            };

            await Parallel.ForAsync<int>(0, parallelContexts.Length, options, async (i, ct) =>
            {
                T context = parallelContexts[i];
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

                long activationTimestamp = measurementStartTimestamp + delays[i] * Stopwatch.Frequency;

                long remainingTicks = activationTimestamp - Stopwatch.GetTimestamp();

                if (remainingTicks > 0)
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency),
                        ct);
                }

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

            // TODO:

            return new SteppedLoadTestResult();
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
