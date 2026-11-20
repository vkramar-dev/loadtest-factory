using LoadTestFactory.Notifications;
using LoadTestFactory.Metrics;
using System.Diagnostics;

namespace LoadTestFactory;

public static class LoadTestRunner
{
    public static event EventHandler<LoadTestProgressEventArgs> ProgressChanged;

    internal static string PathToFolder;

    internal static GCPauseCollector GCCollector;

    public static async Task<LoadTestResult> RunAsync(
        int parallelAmount,
        int warmupInSeconds,
        int durationInSeconds,
        TestScenario[] scenarios,
        string workingDirectory = null,
        bool consoleOutput = true,
        CancellationToken cancellationToken = default)
    {
        long overallStartedAt = Stopwatch.GetTimestamp();
        LoadTestProgressMonitor progressMonitor = null;
        MetricsCollector[] collectors = null;
        GCPauseCollector gcCollector = null;

        try
        {
            ValidateParams(parallelAmount, warmupInSeconds, durationInSeconds, scenarios, workingDirectory);

            PathToFolder = GetPathToFolder(workingDirectory);

            TestEnvironment testEnvironment = TestEnvironmentCollector.Capture(PathToFolder);
            LoadTestWarning[] startupWarnings = TestEnvironmentCollector.GetStartupWarnings(
                testEnvironment, checked(MetricsCollector.InitialBufferBytes * parallelAmount));

            if (consoleOutput)
            {
                try
                {
                    ConsoleOutput.WriteHeader(parallelAmount, warmupInSeconds, durationInSeconds,
                        scenarios.Length, testEnvironment, startupWarnings);
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Write(ex, PathToFolder);
                }
            }

            collectors = new MetricsCollector[parallelAmount];
            for (int i = 0; i < parallelAmount; ++i)
                collectors[i] = new MetricsCollector(i);

            FullyFragmentedTestScenarioCircle scenariosCircle = new(scenarios);
            ParallelOptions options = new()
            {
                MaxDegreeOfParallelism = parallelAmount,
            };

            gcCollector = new GCPauseCollector();
            GCCollector = gcCollector;
            gcCollector.Start();

            // One calibrated Stopwatch time axis for workers, GC and report buckets.
            long beforeUtc = Stopwatch.GetTimestamp();
            DateTime clockAnchorUtc = DateTime.UtcNow;
            long afterUtc = Stopwatch.GetTimestamp();
            long runStartTimestamp = beforeUtc + (afterUtc - beforeUtc) / 2;

            long measurementStartTimestamp = runStartTimestamp
                + (long)warmupInSeconds * Stopwatch.Frequency;
            long measurementEndTimestamp = measurementStartTimestamp
                + (long)durationInSeconds * Stopwatch.Frequency;

            progressMonitor = new LoadTestProgressMonitor(collectors, runStartTimestamp,
                measurementStartTimestamp, measurementEndTimestamp, consoleOutput);
            progressMonitor.Start();

            await Parallel.ForAsync<int>(0, parallelAmount, options, async (i, ct) =>
            {
                var iterator = scenariosCircle.GetIterator();

                while (!ct.IsCancellationRequested)
                {
                    if (Stopwatch.GetTimestamp() >= measurementStartTimestamp)
                        break;

                    TestScenario scenario = iterator.Next();
                    await scenario.ExecuteSafeAsync();
                }

                MetricsCollector currentCollector = collectors[i];
                while (true)
                {
                    if (Stopwatch.GetTimestamp() >= measurementEndTimestamp)
                        break;

                    TestScenario scenario = iterator.Next();
                    await scenario.ExecuteMeasuredAsync(currentCollector, measurementEndTimestamp);
                }
            });

            // Workers are finished. A completion after the configured measurement
            // end is stored in CompletionTail, not the normal statistics.
            await progressMonitor.CompleteAsync();

            GCPause[] pauses = gcCollector.GetPauses(clockAnchorUtc, runStartTimestamp,
                measurementStartTimestamp, measurementEndTimestamp);
            progressMonitor.WriteGcPauseSummary(pauses.Length);

            long blockingCount = 0;
            long blockingTicks = 0;
            long maxBlockingTicks = 0;
            foreach (MetricsCollector collector in collectors)
            {
                blockingCount += collector.BufferBlockingCount;
                blockingTicks += collector.BufferBlockingTicks;
                maxBlockingTicks = Math.Max(maxBlockingTicks, collector.MaxBufferBlockingTicks);
            }

            List<LoadTestWarning> warnings = new(startupWarnings);
            if (blockingCount > 0 && maxBlockingTicks >= Stopwatch.Frequency / 100)
            {
                warnings.Add(new LoadTestWarning("GENERATOR_BLOCKING",
                    $"Measurement collectors waited for free buffers {blockingCount:N0} time(s) " +
                    $"(max {maxBlockingTicks * 1000.0 / Stopwatch.Frequency:N0} ms). " +
                    "Storage throughput or scheduling may be limiting the load generator."));
                progressMonitor.WriteWarnings(warnings.Skip(startupWarnings.Length));
            }
            progressMonitor = null; // CompleteAsync already disposed its timer.

            // Completion is an explicit operation: Dispose only releases resources.
            // Every worker's final partial buffer is persisted before processing.
            foreach (MetricsCollector collector in collectors)
                collector.Complete();

            // Release all collection buffers before allocating processing histograms.
            // The collector metadata (segment files and error codes) remains readable.
            foreach (MetricsCollector collector in collectors)
                collector.Dispose();

            long metricsStartedAt = Stopwatch.GetTimestamp();
            MetricsProcessingResult processed = await MetricsProcessor.ProcessAsync(
                collectors, scenarios, measurementStartTimestamp, measurementEndTimestamp,
                durationInSeconds, cancellationToken);
            TimeSpan metricsProcessingDuration = TimeSpan.FromSeconds(
                (Stopwatch.GetTimestamp() - metricsStartedAt) / (double)Stopwatch.Frequency);

            // Close the GC listener before capturing overall duration.
            gcCollector.Dispose();
            gcCollector = null;
            GCCollector = null;

            LoadTestResult result = new LoadTestResult
            {
                ParallelAmount = parallelAmount,
                WarmupDuration = TimeSpan.FromSeconds(warmupInSeconds),
                MeasurementDuration = TimeSpan.FromSeconds(durationInSeconds),
                ActualOverallDuration = TimeSpan.FromSeconds(
                    (Stopwatch.GetTimestamp() - overallStartedAt) / (double)Stopwatch.Frequency),
                MetricsProcessingDuration = metricsProcessingDuration,
                MeasurementStartedAtUtc = new DateTimeOffset(clockAnchorUtc.AddSeconds(warmupInSeconds)),
                Environment = testEnvironment,
                Warnings = warnings,
                Engine = new EngineDiagnostics
                {
                    BufferBlockingCount = blockingCount,
                    TotalBufferBlockingTime = TimeSpan.FromSeconds(blockingTicks / (double)Stopwatch.Frequency),
                    MaxBufferBlockingTime = TimeSpan.FromSeconds(maxBlockingTicks / (double)Stopwatch.Frequency)
                },
                GcPauses = pauses,
                OverallStatistics = processed.Overall,
                CompletionTail = processed.CompletionTail,
                Scenarios = processed.Scenarios
            };

            if (consoleOutput)
                Console.WriteLine(result.ToString());

            return result;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Write(ex, PathToFolder ?? workingDirectory);
            return null;
        }
        finally
        {
            if (progressMonitor != null)
                await progressMonitor.DisposeAsync();
            if (collectors != null)
            {
                foreach (MetricsCollector collector in collectors)
                    collector?.Dispose();
            }
            gcCollector?.Dispose();
            GCCollector = null;
        }
    }

    // Event invocation and subscriber isolation belong to LoadTestProgressMonitor.
    internal static EventHandler<LoadTestProgressEventArgs> GetProgressChangedSubscribers() => ProgressChanged;

    private static void ValidateParams(
        int parallelAmount,
        int warmupInSeconds,
        int durationInSeconds,
        TestScenario[] scenarios,
        string workingDirectory = null)
    {
        const int MaxDurationInSeconds = 24 * 60 * 60;

        ArgumentOutOfRangeException.ThrowIfLessThan(parallelAmount, 1);

        ArgumentOutOfRangeException.ThrowIfNegative(warmupInSeconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(warmupInSeconds, MaxDurationInSeconds);

        ArgumentOutOfRangeException.ThrowIfLessThan(durationInSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(durationInSeconds, MaxDurationInSeconds);

        ArgumentNullException.ThrowIfNull(scenarios);
        if (scenarios.Length == 0)
            throw new ArgumentException(
                "At least one scenario is required.",
                nameof(scenarios));

        if (workingDirectory != null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
            Path.GetFullPath(workingDirectory);
        }
    }

    private static string GetPathToFolder(string workingDirectory)
    {
        if (String.IsNullOrWhiteSpace(workingDirectory))
        {
            workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            workingDirectory = Path.Combine(workingDirectory, "LoadTest_Factory");
        }

        DirectoryInfo appDir = Directory.CreateDirectory(workingDirectory);
        foreach (FileInfo file in appDir.GetFiles("load_test_*_*.bin"))
            file.Delete();


        string testFileName = Path.Combine(workingDirectory, $"LoadTestFactory{Guid.NewGuid():N}.tmp");
        File.Create(testFileName).Dispose();
        File.Delete(testFileName);

        return workingDirectory;
    }

}
