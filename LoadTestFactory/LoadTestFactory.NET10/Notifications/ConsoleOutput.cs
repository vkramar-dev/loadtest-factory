using System.Reflection;

namespace LoadTestFactory.Notifications;

internal static class ConsoleOutput
{
    private static readonly string[] Banner =
    [
        "#       ###    ###   ####          #####  #####   ####  #####",
        "#      #   #  #   #  #   #           #    #      #        #",
        "#      #   #  #####  #   #  #####    #    ####    ###     #",
        "#      #   #  #   #  #   #           #    #          #    #",
        "#####   ###   #   #  ####            #    #####  ####     #",
        "",
        "         #####  #   #   ####  #####  #   #  #####",
        "         #      ##  #  #        #    ##  #  #",
        "         ####   # # #  # ###    #    # # #  ####",
        "         #      #  ##  #   #    #    #  ##  #",
        "         #####  #   #   ####  #####  #   #  #####"
    ];

    internal static void WriteHeader(int workers, int warmupSeconds, int durationSeconds, int scenarios)
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
        Console.WriteLine(new string('-', 60));
        Console.WriteLine($".NET Load Testing | v{version}");
        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"Workers:      {workers:N0}");
        Console.WriteLine($"Scenarios:    {scenarios:N0}");
        Console.WriteLine($"Warmup:       {warmupSeconds} seconds");
        Console.WriteLine($"Measurement:  {durationSeconds} seconds");
        Console.WriteLine(new string('-', 60));
        Console.WriteLine();
        Console.WriteLine("Starting load test...");
    }

    internal static void WriteProgress(LoadTestProgressEventArgs progress)
    {
        string phase = progress.Phase switch
        {
            LoadTestPhase.Warmup => "Warmup",
            LoadTestPhase.Measurement => "Measurement",
            _ => "Completed"
        };

        string elapsed = FormatElapsed(progress.Elapsed);
        string duration = FormatElapsed(progress.Duration);
        string line = $"[{phase,-11}] {elapsed} / {duration} ({progress.Percentage,5:F1}%)";

        if (progress.Phase == LoadTestPhase.Warmup)
        {
            Console.WriteLine(line);
            return;
        }

        line += $" | Executions: {progress.Completed:N0} | Failures: {progress.Failed:N0}";
        if (progress.Phase == LoadTestPhase.Measurement)
            line += $" | RPS: {progress.Rps:N0}";

        Console.WriteLine(line);
    }

    private static string FormatElapsed(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}";
}
