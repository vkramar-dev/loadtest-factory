using LoadTestFactory.Metrics;
using LoadTestFactory.Notifications;
using LoadTestFactory.Reports;
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
                measurementStartTimestamp, measurementEndTimestamp, durationInSeconds, consoleOutput);
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
                warnings.Add(new LoadTestWarning("DISK_WRITE_BOTTLENECK",
                    $"Load generator waited {blockingCount:N0} time(s) for measurement buffers " +
                    $"(longest wait {maxBlockingTicks * 1000.0 / Stopwatch.Frequency:N0} ms). " +
                    "Disk write speed or OS scheduling may be limiting the generated load."));
            }
            progressMonitor = null; // CompleteAsync already disposed its timer.

            ConsoleStageProgress storageProgress = TryStartStage(consoleOutput, "Storage");
            try
            {
                // Completion is an explicit operation: Dispose only releases resources.
                // Every worker's final partial buffer is persisted before processing.
                foreach (MetricsCollector collector in collectors)
                    collector.Complete();

                // Release all collection buffers before allocating processing histograms.
                // The collector metadata (segment files and error codes) remains readable.
                foreach (MetricsCollector collector in collectors)
                    collector.Dispose();
            }
            finally
            {
                if (storageProgress != null)
                    await TryFinishStageAsync(storageProgress);
            }

            long metricsStartedAt = Stopwatch.GetTimestamp();
            ConsoleStageProgress processingProgress = TryStartStage(consoleOutput, "Processing");
            MetricsProcessingResult processed;
            TimeSpan metricsProcessingDuration;
            try
            {
                processed = await MetricsProcessor.ProcessAsync(
                    collectors, scenarios, measurementStartTimestamp, measurementEndTimestamp,
                    durationInSeconds, cancellationToken);
                metricsProcessingDuration = Stopwatch.GetElapsedTime(metricsStartedAt);
            }
            finally
            {
                if (processingProgress != null)
                    await TryFinishStageAsync(processingProgress);
            }

            // Close the GC listener before capturing overall duration.
            gcCollector.Dispose();
            gcCollector = null;
            GCCollector = null;

            LoadTestResult result = new()
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
            {
                try
                {
                    ConsoleOutput.WriteResult(result);
                }
                catch (Exception ex)
                {
                    // Console failures must never discard an otherwise valid result.
                    DiagnosticLogger.Write(ex, PathToFolder);
                }
            }

            if (result != null)
            {
                string reportFileName = Path.Combine(
                    PathToFolder,
                    $"loadtest_report_{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss}_UTC.html");

                HtmlReportGenerator.Save(result, reportFileName);
            }

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
            if (consoleOutput)
            {
                try
                {
                    ConsoleOutput.EndProgressLine();
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Write(ex, PathToFolder);
                }
            }
            if (collectors != null)
            {
                foreach (MetricsCollector collector in collectors)
                    collector?.Dispose();
            }
            gcCollector?.Dispose();
            GCCollector = null;
        }
    }

    // Console progress is informational only: a console failure must never
    // interrupt the test or prevent returning calculated results.
    private static ConsoleStageProgress TryStartStage(bool consoleOutput, string stage)
    {
        if (!consoleOutput)
            return null;

        try
        {
            return new ConsoleStageProgress(stage);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Write(ex, PathToFolder);
            return null;
        }
    }

    private static async ValueTask TryFinishStageAsync(ConsoleStageProgress progress)
    {
        try
        {
            await progress.CompleteAsync();
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Write(ex, PathToFolder);
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
