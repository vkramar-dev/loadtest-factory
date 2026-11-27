using LoadTestFactory.Metrics;
using LoadTestFactory.Notifications;
using LoadTestFactory.Reports;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

namespace LoadTestFactory;

internal static class GlobalState
{
    public static string PathToFolder { get; private set; }

    private static int _state;

    public static void CaptureState()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException(
                "Another load test is already running.");
    }

    public static void SetPathToFolder(string pathToFolder)
    {
        PathToFolder = pathToFolder;
    }

    public static void ReleaseState()
    {
        PathToFolder = null;
        Interlocked.Exchange(ref _state, 0);
    }
}

internal sealed class LoadTestContext<T>
{
    int ParallelAmount;
    int WarmupInSeconds;
    int DurationInSeconds;
    TestScenario<T>[] Scenarios;
    string WorkingDirectory;
    bool IsConsoleOutput;
    CancellationToken CancellationToken;

    internal LoadTestWarning[] StartupWarning;
    internal TestEnvironment TestEnvironment;
    internal MetricsCollector[] Collectors;
    internal FullyFragmentedTestScenarioCircle<T> ScenariosCircle;
    internal GCPauseCollector GCPauseCollector;
    internal DateTime ClockAnchorUtc;
    internal LoadTestProgressMonitor ProgressMonitor;
    internal long MeasurementStartTimestamp;
    internal long MeasurementEndTimestamp;
    internal long RunStartTimestamp;
    internal long OverallStartedAt;
    internal List<LoadTestWarning> Warnings;
    long BlockingCount;
    long BlockingTicks;
    long MaxBlockingTicks;
    LoadTestResult Result;

    internal LoadTestContext(
        int parallelAmount,
        int warmupInSeconds,
        int durationInSeconds,
        TestScenario<T>[] scenarios,
        string workingDirectory,
        bool consoleOutput,
        CancellationToken cancellationToken)
    {
        ParallelAmount = parallelAmount;
        WarmupInSeconds = warmupInSeconds;
        DurationInSeconds = durationInSeconds;
        Scenarios = scenarios;
        WorkingDirectory = workingDirectory;
        IsConsoleOutput = consoleOutput;
        CancellationToken = cancellationToken;
    }

    internal void InitializeAndStartApp()
    {
        ValidateParams(ParallelAmount, WarmupInSeconds, DurationInSeconds, Scenarios, WorkingDirectory);

        GlobalState.SetPathToFolder(GetPathToFolder(WorkingDirectory));

        TestEnvironment = TestEnvironmentCollector.Capture(GlobalState.PathToFolder);
        StartupWarning = TestEnvironmentCollector.GetStartupWarnings(
            TestEnvironment, checked(MetricsCollector.InitialBufferBytes * ParallelAmount));

        if (IsConsoleOutput)
        {
            try
            {
                ConsoleOutput.WriteHeader(ParallelAmount, WarmupInSeconds, DurationInSeconds,
                    Scenarios.Length, TestEnvironment, StartupWarning, GlobalState.PathToFolder);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Write(ex, GlobalState.PathToFolder);
            }
        }

        Collectors = new MetricsCollector[ParallelAmount];
        for (int i = 0; i < ParallelAmount; ++i)
            Collectors[i] = new MetricsCollector(i, GlobalState.PathToFolder);

        ScenariosCircle = new FullyFragmentedTestScenarioCircle<T>(Scenarios);
    }

    private void ValidateParams(
        int parallelAmount,
        int warmupInSeconds,
        int durationInSeconds,
        TestScenario<T>[] scenarios,
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

    private string GetPathToFolder(string workingDirectory)
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

    internal void StartMonitoringGC()
    {
        GCPauseCollector = new GCPauseCollector();
        GCPauseCollector.Start();
    }

    internal void CalibrateTimestamps()
    {
        // One calibrated Stopwatch time axis for workers, GC and report buckets.
        long beforeUtc = Stopwatch.GetTimestamp();
        ClockAnchorUtc = DateTime.UtcNow;
        long afterUtc = Stopwatch.GetTimestamp();
        RunStartTimestamp = beforeUtc + (afterUtc - beforeUtc) / 2;

        MeasurementStartTimestamp = RunStartTimestamp + (long)WarmupInSeconds * Stopwatch.Frequency;
        MeasurementEndTimestamp = MeasurementStartTimestamp + (long)DurationInSeconds * Stopwatch.Frequency;
    }

    internal void StartProgressMonitor()
    {
        OverallStartedAt = Stopwatch.GetTimestamp();

        ProgressMonitor = new LoadTestProgressMonitor(Collectors, RunStartTimestamp,
            MeasurementStartTimestamp, MeasurementEndTimestamp, DurationInSeconds, IsConsoleOutput,
            Scenarios.Any(scenario => scenario.MeasureScenario));

        ProgressMonitor.Start();
    }

    internal GCPause[] GetGCPauses()
    {
        return GCPauseCollector.GetPauses(ClockAnchorUtc, RunStartTimestamp,
                MeasurementStartTimestamp, MeasurementEndTimestamp);
    }

    // Console progress is informational only: a console failure must never
    // interrupt the test or prevent returning calculated results.
    internal ConsoleStageProgress TryStartStage(bool consoleOutput, string stage)
    {
        if (!consoleOutput)
            return null;

        try
        {
            return new ConsoleStageProgress(stage);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Write(ex, GlobalState.PathToFolder);
            return null;
        }
    }

    internal async ValueTask TryFinishStageAsync(ConsoleStageProgress progress)
    {
        try
        {
            await progress.CompleteAsync();
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Write(ex, GlobalState.PathToFolder);
        }
    }

    internal void AddWarningsIfExists()
    {
        BlockingCount = 0;
        BlockingTicks = 0;
        MaxBlockingTicks = 0;
        foreach (MetricsCollector collector in Collectors)
        {
            BlockingCount += collector.BufferBlockingCount;
            BlockingTicks += collector.BufferBlockingTicks;
            MaxBlockingTicks = Math.Max(MaxBlockingTicks, collector.MaxBufferBlockingTicks);
        }

        Warnings = new List<LoadTestWarning>(StartupWarning);
        if (BlockingCount > 0 && MaxBlockingTicks >= Stopwatch.Frequency / 100)
        {
            Warnings.Add(new LoadTestWarning("DISK_WRITE_BOTTLENECK",
                $"Load generator waited {BlockingCount:N0} time(s) for measurement buffers " +
                $"(longest wait {MaxBlockingTicks * 1000.0 / Stopwatch.Frequency:N0} ms). " +
                "Disk write speed or OS scheduling may be limiting the generated load."));
        }
    }

    internal async Task CompleteCollectors()
    {
        ConsoleStageProgress storageProgress = TryStartStage(IsConsoleOutput, "Storage");
        try
        {
            // Completion is an explicit operation: Dispose only releases resources.
            // Every worker's final partial buffer is persisted before processing.
            foreach (MetricsCollector collector in Collectors)
                collector.Complete();

            // Release all collection buffers before allocating processing histograms.
            // The collector metadata (segment files and error codes) remains readable.
            foreach (MetricsCollector collector in Collectors)
                collector.Dispose();
        }
        finally
        {
            if (storageProgress != null)
                await TryFinishStageAsync(storageProgress);
        }
    }

    internal async Task<LoadTestResult> CompileLoadTestResult()
    {
        long metricsStartedAt = Stopwatch.GetTimestamp();
        ConsoleStageProgress processingProgress = TryStartStage(IsConsoleOutput, "Processing");
        MetricsProcessingResult processed;
        TimeSpan metricsProcessingDuration;
        try
        {
            processed = await MetricsProcessor.ProcessAsync(
                Collectors, Scenarios, MeasurementStartTimestamp, MeasurementEndTimestamp,
                DurationInSeconds, CancellationToken);
            metricsProcessingDuration = Stopwatch.GetElapsedTime(metricsStartedAt);
        }
        finally
        {
            if (processingProgress != null)
                await TryFinishStageAsync(processingProgress);
        }

        GCPause[] pauses = GetGCPauses();


        Result = new LoadTestResult()
        {
            ParallelAmount = ParallelAmount,
            WarmupDuration = TimeSpan.FromSeconds(WarmupInSeconds),
            MeasurementDuration = TimeSpan.FromSeconds(DurationInSeconds),
            ActualOverallDuration = TimeSpan.FromSeconds(
                    (Stopwatch.GetTimestamp() - OverallStartedAt) / (double)Stopwatch.Frequency),
            MetricsProcessingDuration = metricsProcessingDuration,
            MeasurementStartedAtUtc = new DateTimeOffset(ClockAnchorUtc.AddSeconds(WarmupInSeconds)),
            Environment = TestEnvironment,
            Warnings = Warnings,
            Engine = new EngineDiagnostics
            {
                BufferBlockingCount = BlockingCount,
                TotalBufferBlockingTime = TimeSpan.FromSeconds(BlockingTicks / (double)Stopwatch.Frequency),
                MaxBufferBlockingTime = TimeSpan.FromSeconds(MaxBlockingTicks / (double)Stopwatch.Frequency)
            },
            GcPauses = pauses,
            OverallStatistics = processed.Overall,
            CompletionTail = processed.CompletionTail,
            Scenarios = processed.Scenarios,
            MeasuredCalls = processed.MeasuredCalls
        };

        return Result;
    }

    internal void WriteResultIntoConsole()
    {
        if (IsConsoleOutput)
        {
            try
            {
                ConsoleOutput.WriteResult(Result);
            }
            catch (Exception ex)
            {
                // Console failures must never discard an otherwise valid result.
                DiagnosticLogger.Write(ex, GlobalState.PathToFolder);
            }
        }
    }

    internal void SaveReportResult()
    {
        if (Result != null)
        {
            string reportFileName = Path.Combine(
                GlobalState.PathToFolder,
                $"loadtest_report_{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss}_UTC.html");

            HtmlReportGenerator.Save(Result, reportFileName);
        }
    }

    internal async Task CleanUp()
    {
        if (ProgressMonitor != null)
            await ProgressMonitor.DisposeAsync();

        if (IsConsoleOutput)
        {
            try
            {
                ConsoleOutput.EndProgressLine();
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Write(ex, GlobalState.PathToFolder);
            }
        }

        if (Collectors != null)
        {
            foreach (MetricsCollector collector in Collectors)
                collector?.Dispose();
        }

        GCPauseCollector?.Dispose();
    }
}
