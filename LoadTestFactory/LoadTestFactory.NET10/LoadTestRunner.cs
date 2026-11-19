using LoadTestFactory.Notifications;
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
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateParams(parallelAmount, warmupInSeconds, durationInSeconds, scenarios, workingDirectory);

            PathToFolder = GetPathToFolder(workingDirectory);

            MetricsCollector[] collectors = new MetricsCollector[parallelAmount];
            for (int i = 0; i < parallelAmount; ++i)
                collectors[i] = new MetricsCollector(i);

            FullyFragmentedTestScenarioCircle scenariosCircle = new(scenarios);

            ParallelOptions options = new()
            {
                MaxDegreeOfParallelism = parallelAmount,
            };

            Timer notificationTimer = new Timer((notUsed) => {
                Console.WriteLine("I am TIMER");
            }, null, 1000, 1000 * 10);

            GCCollector = new GCPauseCollector();
            GCCollector.Start();

            // Keep one high-resolution time axis for all workers and metrics.
            // EventWrittenEventArgs.TimeStamp uses UTC, so capture a clock
            // calibration once and convert GC event times onto this axis.
            long beforeUtc = Stopwatch.GetTimestamp();
            DateTime clockAnchorUtc = DateTime.UtcNow;
            long afterUtc = Stopwatch.GetTimestamp();
            long runStartTimestamp = beforeUtc + (afterUtc - beforeUtc) / 2;

            long measurementStartTimestamp = runStartTimestamp
                + (long)warmupInSeconds * Stopwatch.Frequency;
            long measurementEndTimestamp = measurementStartTimestamp
                + (long)durationInSeconds * Stopwatch.Frequency;

            await Parallel.ForAsync<int>(0, parallelAmount, options, async (i, ct) =>
            {
                var iterator = scenariosCircle.GetIterator();

                // Warm-up
                while (!ct.IsCancellationRequested)
                {
                    if (Stopwatch.GetTimestamp() >= measurementStartTimestamp)
                        break;

                    TestScenario scenario = iterator.Next();
                    await scenario.ExecuteSafeAsync();
                }

                MetricsCollector currentCollector = collectors[i];

                // Measurement
                while (true)
                {
                    if (Stopwatch.GetTimestamp() >= measurementEndTimestamp)
                        break;

                    TestScenario scenario = iterator.Next();
                    await scenario.ExecuteMeasuredAsync(currentCollector);
                }

                Console.WriteLine($"Parallel: #{i}, Count: {currentCollector.GetTotalMetrics()}");
            });

            GCPause[] pauses = GCCollector.GetPauses(
                clockAnchorUtc,
                runStartTimestamp,
                measurementStartTimestamp,
                measurementEndTimestamp);
            foreach (GCPause pause in pauses)
                Console.WriteLine($"Mls: {pause.EndMs - pause.StartMs}");

            Console.WriteLine($"GC counts: {pauses.Length}");

            notificationTimer.Dispose();

            for (int i = 0; i < parallelAmount; ++i)
                collectors[i].Dispose();

            GCCollector.Dispose();

            return new LoadTestResult();
        }
        catch(Exception ex)
        {
            WriteLogFile(workingDirectory, ex);
            return null;
        }
    }

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

    private static void WriteLogFile(string workingDirectory, Exception ex)
    {
        // An invalid or unwritable workingDirectory must not prevent an error log
        // from being written to the default diagnostic directory.
        string fallbackDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LoadTest_Factory");

        foreach (string directory in new[] { workingDirectory, fallbackDirectory })
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            try
            {
                Directory.CreateDirectory(directory);

                string filename = $"log_{DateTime.UtcNow:yyyyMMdd_HHmmss_fffffff}_{Guid.NewGuid():N}.txt";
                string path = Path.Combine(directory, filename);
                string content = $"Time (UTC): {DateTime.UtcNow:O}{Environment.NewLine}"
                    + $"Process ID: {Environment.ProcessId}{Environment.NewLine}"
                    + $"{Environment.NewLine}{ex}";

                File.WriteAllText(path, content);
                return;
            }
            catch (Exception)
            {
                // Diagnostics must never hide the original exception.
                // Try the fallback directory if the requested one failed.
            }
        }
    }
}
