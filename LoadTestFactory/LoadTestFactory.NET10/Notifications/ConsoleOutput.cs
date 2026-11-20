using System.Reflection;

namespace LoadTestFactory.Notifications;

internal static class ConsoleOutput
{
    private static readonly object ProgressWriteLock = new();
    private static bool _progressLineOpen;
    private static int _progressLineWidth;

    private static readonly string[] Banner =
    [
        "#       ###    ###   ####          #####  #####   ####  #####",
        "#      #   #  #   #  #   #           #    #      #        #",
        "#      #   #  #####  #   #  #####    #    ####    ###     #",
        "#      #   #  #   #  #   #           #    #          #    #",
        "#####   ###   #   #  ####            #    #####  ####     #",
        "",
        "          #####  #   #   ####  #####  #   #  #####",
        "          #      ##  #  #        #    ##  #  #",
        "          ####   # # #  # ###    #    # # #  ####",
        "          #      #  ##  #   #    #    #  ##  #",
        "          #####  #   #   ####  #####  #   #  #####"
    ];

    internal static void WriteHeader(int workers, int warmupSeconds, int durationSeconds,
        int scenarios, TestEnvironment environment, IReadOnlyList<LoadTestWarning> warnings)
    {
        Console.WriteLine();

        bool colored = !Console.IsOutputRedirected;
        ConsoleColor previousColor = Console.ForegroundColor;
        try
        {
            if (colored)
                Console.ForegroundColor = ConsoleColor.Cyan;

            foreach (string line in Banner)
                Console.WriteLine(line);
        }
        finally
        {
            if (colored)
                Console.ForegroundColor = previousColor;
        }

        string version = typeof(LoadTestRunner).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?.Split('+')[0]
            ?? typeof(LoadTestRunner).Assembly.GetName().Version?.ToString(3)
            ?? "dev";

        Console.WriteLine();
        Separator();
        Console.WriteLine($".NET Load Testing | v{version}");
        Separator();
        WriteMetric("OS:", $"{environment.OperatingSystem} ({environment.OSArchitecture})");
        WriteMetric("Runtime:", environment.Runtime);
        WriteMetric("Process:", environment.ProcessArchitecture);
        if (!string.IsNullOrWhiteSpace(environment.CpuModel))
            WriteMetric("CPU:", environment.CpuModel);
        WriteMetric("Logical CPUs:", environment.LogicalProcessorCount.ToString("N0"));
        if (environment.CpuFrequencyMHz is double frequency)
            WriteMetric("CPU clock:", $"{frequency / 1000.0:N2} GHz (OS-reported)");
        WriteMetric("GC Mode:", environment.IsServerGC ? "Server" : "Workstation");
        WriteMetric("Memory free:", FormatAvailable(environment.AvailablePhysicalMemoryBytes,
            environment.TotalPhysicalMemoryBytes));
        WriteMetric("Storage free:", FormatAvailable(environment.AvailableStorageBytes));
        if (environment.StorageVolumeType != null)
            WriteMetric("Volume type:", environment.StorageFileSystem is null
                ? environment.StorageVolumeType
                : $"{environment.StorageVolumeType} ({environment.StorageFileSystem})");
        WriteMetric("Storage path:", environment.StoragePath);
        Separator();
        WriteMetric("Workers:", workers.ToString("N0"));
        WriteMetric("Scenarios:", scenarios.ToString("N0"));
        WriteMetric("Warmup:", $"{warmupSeconds} seconds");
        WriteMetric("Measurement:", $"{durationSeconds} seconds");
        Separator();
        WriteWarnings(warnings);
        Console.WriteLine();
        Console.WriteLine("Starting load test...");
    }

    internal static void WriteMetric(string label, string value)
    {
        // Console tab stops are every 8 columns. All header labels fit within
        // 16 columns, so one or two actual TAB characters align every value.
        string tabs = label.Length < 8 ? "\t\t" : "\t";
        Console.WriteLine($"{label}{tabs}{value}");
    }

    internal static void WriteWarnings(IEnumerable<LoadTestWarning> warnings)
    {
        foreach (LoadTestWarning warning in warnings)
        {
            bool colored = !Console.IsOutputRedirected;
            ConsoleColor previousColor = Console.ForegroundColor;
            try
            {
                if (colored)
                    Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"WARNING [{warning.DisplayCode}]: {warning.Message}");
            }
            finally
            {
                if (colored)
                    Console.ForegroundColor = previousColor;
            }
        }
    }

    internal static void WriteProgress(LoadTestProgressEventArgs progress)
    {
        string phase = progress.Phase switch
        {
            LoadTestPhase.Warmup => "Warmup",
            LoadTestPhase.Measurement => "Measurement",
            _ => "Measurement"
        };

        string elapsed = FormatElapsed(progress.Elapsed);
        string duration = FormatElapsed(progress.Duration);
        string line = $"[{phase,-11}] {elapsed} / {duration} ({progress.Percentage,5:F1}%)";

        if (progress.Phase != LoadTestPhase.Warmup)
        {
            line += $" | Executions: {progress.Completed:N0} | Failures: {progress.Failed:N0}";
            line += progress.Phase == LoadTestPhase.Completed
                ? $" | RPS (avg): {progress.Rps:N0}"
                : $" | RPS: {progress.Rps:N0}";
        }

        // A completed phase occupies one fixed line, regardless of its duration.
        bool finished = progress.Phase == LoadTestPhase.Completed ||
                        (progress.Phase == LoadTestPhase.Warmup && progress.Percentage >= 100);
        WriteProgressLine(line, finished);
    }

    internal static void WriteStageProgress(string phase, TimeSpan elapsed, bool finished)
    {
        string time = finished
            ? $"{(long)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}.{elapsed.Milliseconds:000}"
            : FormatElapsed(elapsed);
        WriteProgressLine($"[{phase,-11}] {time}", finished);
    }

    // Only one terminal line is used for each phase. During a phase we repaint
    // that line, and once finished we terminate it with a newline. Redirected
    // output gets only the final line, without carriage returns or ANSI codes.
    private static void WriteProgressLine(string line, bool finished)
    {
        lock (ProgressWriteLock)
        {
            if (Console.IsOutputRedirected)
            {
                if (finished)
                    Console.WriteLine(line);
                return;
            }

            if (_progressLineOpen)
                Console.Write('\r');

            Console.Write(line);
            if (_progressLineOpen && _progressLineWidth > line.Length)
                Console.Write(new string(' ', _progressLineWidth - line.Length));

            if (finished)
            {
                Console.WriteLine();
                _progressLineOpen = false;
                _progressLineWidth = 0;
            }
            else
            {
                _progressLineWidth = Math.Max(_progressLineWidth, line.Length);
                _progressLineOpen = true;
                Console.Out.Flush();
            }
        }
    }

    // Also close a partially written line if a test is aborted unexpectedly.
    internal static void EndProgressLine()
    {
        lock (ProgressWriteLock)
        {
            if (_progressLineOpen && !Console.IsOutputRedirected)
                Console.WriteLine();

            _progressLineOpen = false;
            _progressLineWidth = 0;
        }
    }

    // Render warnings separately so their color does not depend on detecting
    // a header inside a formatted string (or on CRLF vs LF line endings).
    // ToString() itself remains plain text for logging and other consumers.
    internal static void WriteResult(LoadTestResult result)
    {
        if (result.Warnings == null || result.Warnings.Count == 0 || Console.IsOutputRedirected)
        {
            Console.WriteLine(result);
            return;
        }

        Console.WriteLine(result.ToStringWithoutWarnings());
        Console.WriteLine();

        ConsoleColor previousColor = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("WARNINGS");
            foreach (LoadTestWarning warning in result.Warnings)
                Console.WriteLine($"  [{warning.DisplayCode}] {warning.Message}");
        }
        finally
        {
            Console.ForegroundColor = previousColor;
        }
    }

    private static string FormatAvailable(long? free, long? total = null)
    {
        if (free is not long bytes)
            return "Unknown";
        return total is long size
            ? $"{TestEnvironmentCollector.FormatBytes(bytes)} / {TestEnvironmentCollector.FormatBytes(size)} total"
            : TestEnvironmentCollector.FormatBytes(bytes);
    }

    private static void Separator() => Console.WriteLine(new string('-', 60));

    private static string FormatElapsed(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
}
