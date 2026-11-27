using System.Text;

namespace LoadTestFactory.Results;

/// <summary>
/// Represents the configuration and collected results of a load test.
/// </summary>
public sealed class LoadTestResult
{
    /// <summary>Gets the load generator environment snapshot captured at the start.</summary>
    public TestEnvironment Environment { get; init; }

    /// <summary>Gets warnings about conditions affecting the reliability of this test.</summary>
    public IReadOnlyList<LoadTestWarning> Warnings { get; init; } = Array.Empty<LoadTestWarning>();

    /// <summary>Gets load generator diagnostics (separate from scenario failures).</summary>
    public EngineDiagnostics Engine { get; init; } = new();

    /// <summary>Gets the recorded GC pauses during the measurement phase.</summary>
    public IReadOnlyList<GCPause> GcPauses { get; init; } = Array.Empty<GCPause>();

    /// <summary>Gets the configured number of concurrent workers.</summary>
    public int ParallelAmount { get; init; }

    /// <summary>Gets the configured duration of the warm-up phase.</summary>
    public TimeSpan WarmupDuration { get; init; }

    /// <summary>Gets the configured duration of the measurement phase.</summary>
    public TimeSpan MeasurementDuration { get; init; }

    /// <summary>Gets the entire RunAsync duration, including worker completion and metric processing.</summary>
    public TimeSpan ActualOverallDuration { get; init; }

    /// <summary>Gets the time spent reading completed segments and calculating metrics.</summary>
    public TimeSpan MetricsProcessingDuration { get; init; }

    /// <summary>Gets the UTC time at which the measurement phase began.</summary>
    public DateTimeOffset MeasurementStartedAtUtc { get; init; }

    /// <summary>Gets the combined statistics for all test scenarios.</summary>
    public ExecutionStatistics OverallStatistics { get; init; } = new();

    /// <summary>Gets all operations completed after the measurement window ended.</summary>
    public CompletionTailStatistics CompletionTail { get; init; } = new();

    /// <summary>Gets the individual results of the test scenarios.</summary>
    public IReadOnlyList<ScenarioResult> Scenarios { get; init; } = Array.Empty<ScenarioResult>();

    /// <summary>Gets the individual results of measured calls.</summary>
    public IReadOnlyList<MeasuredCallResult> MeasuredCalls { get; init; } = Array.Empty<MeasuredCallResult>();

    /// <summary>Returns a compact human-readable summary suitable for console output.</summary>
    public override string ToString() => FormatSummary(includeWarnings: true);

    // The interactive console prints the warning section separately in yellow.
    // Keep ToString() independent of console colors and redirection settings.
    internal string ToStringWithoutWarnings() => FormatSummary(includeWarnings: false);

    private string FormatSummary(bool includeWarnings)
    {
        StringBuilder sb = new(2048);
        ExecutionStatistics overall = OverallStatistics ?? new();
        LatencyStatistics latency = overall.Latency ?? new();
        ThroughputStatistics throughput = overall.Throughput ?? new();

        sb.AppendLine();
        sb.AppendLine(new string('-', 60));
        sb.AppendLine("LOAD TEST RESULTS:");
        sb.AppendLine(new string('-', 60));

        bool hasScenarioMeasurements = overall.TotalCount > 0;
        bool hasMeasuredCalls = MeasuredCalls?.Any(call => call?.Statistics?.TotalCount > 0) == true;

        if (!hasScenarioMeasurements && !hasMeasuredCalls)
        {
            sb.AppendLine();
            sb.AppendLine("No measurements completed within the measurement window.");
        }

        if (hasScenarioMeasurements)
        {
            sb.AppendLine();
            sb.AppendLine("OVERALL:");
            AppendMetric(sb, "Executions:", overall.TotalCount.ToString("N0"));
            AppendMetric(sb, "Successful:", overall.SuccessCount.ToString("N0"));
            AppendMetric(sb, "Failed:", $"{overall.FailedCount:N0} ({overall.FailureRate:N2}%)");
            AppendMetric(sb, "RPS (mean):", throughput.Mean.ToString("N2"));
            AppendMetric(sb, "RPS (min / peak):", $"{throughput.Min:N2} / {throughput.Peak:N2}");
            AppendMetric(sb, "Latency mean:", FormatMeanLatency(latency));
            AppendMetric(sb, "Latency P50:", FormatLatency(latency.P50));
            AppendMetric(sb, "Latency P90:", FormatLatency(latency.P90));
            AppendMetric(sb, "Latency P95:", FormatLatency(latency.P95));
            AppendMetric(sb, "Latency P99:", FormatLatency(latency.P99));
            AppendMetric(sb, "Latency P99.9:", FormatLatency(latency.P999));
            if (latency.Top5Min?.Count > 0)
                AppendMetric(sb, "Fastest sample:", FormatLatency(latency.Top5Min[0].Duration));
            if (latency.Top5Max?.Count > 0)
                AppendMetric(sb, "Slowest sample:", FormatLatency(latency.Top5Max[0].Duration));
        }

        bool showScenarios = hasScenarioMeasurements && Scenarios?.Count > 0;
        string[] scenarioHeaders = ["Scenario", "Weight", "Count", "Failed", "RPS", "Mean", "P95", "P99"];
        string[] callHeaders = showScenarios
            ? ["Call", "", "Count", "Failed", "RPS", "Mean", "P95", "P99"]
            : ["Call", "Count", "Failed", "RPS", "Mean", "P95", "P99"];
        int[] widths = (showScenarios ? scenarioHeaders : callHeaders)
            .Select(header => header.Length).ToArray();
        widths[0] = Math.Max(16, widths[0]);

        List<string[]> scenarioRows = new();
        if (showScenarios)
        {
            foreach (ScenarioResult scenario in Scenarios)
            {
                if (scenario?.MeasureScenario != true)
                    continue;

                ExecutionStatistics stats = scenario.Statistics ?? new();
                LatencyStatistics scenarioLatency = stats.Latency ?? new();
                string[] row =
                [
                    scenario.Name ?? "(unnamed)",
                    scenario.Weight.ToString("N0"),
                    stats.TotalCount.ToString("N0"),
                    stats.FailedCount.ToString("N0"),
                    (stats.Throughput?.Mean ?? 0).ToString("N2"),
                    FormatMeanLatencyValue(scenarioLatency),
                    FormatLatencyValue(scenarioLatency.P95),
                    FormatLatencyValue(scenarioLatency.P99)
                ];
                UpdateColumnWidths(widths, row);
                scenarioRows.Add(row);
            }
        }

        List<string[]> callRows = new();
        if (hasMeasuredCalls)
        {
            foreach (MeasuredCallResult call in MeasuredCalls)
            {
                if (call?.Statistics == null || call.Statistics.TotalCount == 0)
                    continue;

                ExecutionStatistics stats = call.Statistics;
                LatencyStatistics callLatency = stats.Latency ?? new();
                string[] row =
                [
                    call.Name ?? "(unnamed)",
                    .. (showScenarios ? new[] { string.Empty } : Array.Empty<string>()),
                    stats.TotalCount.ToString("N0"),
                    stats.FailedCount.ToString("N0"),
                    (stats.Throughput?.Mean ?? 0).ToString("N2"),
                    FormatMeanLatencyValue(callLatency),
                    FormatLatencyValue(callLatency.P95),
                    FormatLatencyValue(callLatency.P99)
                ];
                UpdateColumnWidths(widths, row);
                callRows.Add(row);
            }
        }

        if (showScenarios)
            AppendTable(sb, "SCENARIOS (latency in ms)", scenarioHeaders, scenarioRows, widths);

        if (hasMeasuredCalls)
            AppendTable(sb, "MEASURED CALLS (latency in ms)", callHeaders, callRows, widths);

        long scenarioTailCount = CompletionTail?.TotalCount ?? 0;
        long measuredCallTailCount = MeasuredCalls?.Sum(call => call?.CompletionTail?.TotalCount ?? 0) ?? 0;

        if (scenarioTailCount > 0 || measuredCallTailCount > 0)
        {
            sb.AppendLine();
            sb.AppendLine("COMPLETION TAIL");
            sb.AppendLine(new string('-', 60));
            sb.AppendLine($"{"Scenarios completed after measurement window:",-52}{scenarioTailCount,8:N0}");
            sb.AppendLine($"{"Measured calls completed after measurement window:",-52}{measuredCallTailCount,8:N0}");
        }

        bool hasErrors = false;
        foreach (var (name, error, isTail) in GetScenarioErrors())
        {
            if (!hasErrors)
            {
                sb.AppendLine();
                sb.AppendLine("ERRORS BY SCENARIO");
                hasErrors = true;
            }
            string phase = isTail ? " (completion tail)" : string.Empty;
            sb.AppendLine($"  {name}{phase}: {error.ExceptionType} ({error.Count:N0})");
        }

        long gcDurationMs = 0;
        if (GcPauses != null)
        {
            foreach (var pause in GcPauses)
                gcDurationMs += Math.Max(0, pause.EndMs - pause.StartMs);
        }
        long gcPauseCount = GcPauses?.Count ?? 0;
        EngineDiagnostics engine = Engine ?? new();
        bool showDiagnostics = gcPauseCount > 0 || gcDurationMs > 0 ||
            engine.BufferBlockingCount > 0 ||
            engine.TotalBufferBlockingTime > TimeSpan.Zero ||
            engine.MaxBufferBlockingTime > TimeSpan.Zero;

        if (showDiagnostics)
        {
            sb.AppendLine();
            sb.AppendLine("GENERATOR DIAGNOSTICS:");
            if (gcPauseCount > 0)
                AppendMetric(sb, "GC pauses:", gcPauseCount.ToString("N0"));
            if (gcDurationMs > 0)
                AppendMetric(sb, "GC pause time:", $"{gcDurationMs:N0} ms");
            if (engine.BufferBlockingCount > 0)
                AppendMetric(sb, "Storage buffer waits:", engine.BufferBlockingCount.ToString("N0"));
            if (engine.TotalBufferBlockingTime > TimeSpan.Zero)
                AppendMetric(sb, "Total wait time:", FormatDuration(engine.TotalBufferBlockingTime));
            if (engine.MaxBufferBlockingTime > TimeSpan.Zero)
                AppendMetric(sb, "Longest wait:", FormatDuration(engine.MaxBufferBlockingTime));
        }

        if (includeWarnings && Warnings != null && Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("WARNINGS:");
            foreach (LoadTestWarning warning in Warnings)
                sb.AppendLine($"  [{warning.DisplayCode}] {warning.Message}");
        }

        return sb.ToString().TrimEnd();
    }

    internal IEnumerable<(string Name, ErrorStatistics Error, bool IsTail)> GetScenarioErrors()
    {
        foreach (ScenarioResult scenario in Scenarios ?? [])
        {
            if (scenario == null)
                continue;

            foreach (ErrorStatistics error in scenario.Statistics?.Errors ?? [])
                yield return (scenario.Name, error, false);

            foreach (ErrorStatistics error in scenario.CompletionTail?.Errors ?? [])
                yield return (scenario.Name, error, true);
        }
    }

    private static void AppendMetric(StringBuilder sb, string label, string value)
    {
        // Values begin at the same 24-column tab stop, as in the startup banner.
        int tabs = Math.Max(1, (24 - label.Length + 7) / 8);
        sb.Append(label).Append('\t', tabs).AppendLine(value);
    }

    private static void UpdateColumnWidths(int[] widths, string[] row)
    {
        for (int i = 0; i < widths.Length; ++i)
            widths[i] = Math.Max(widths[i], row[i].Length);
    }

    private static void AppendTable(StringBuilder sb, string title, string[] headers,
        List<string[]> rows, int[] widths)
    {
        sb.AppendLine();
        sb.AppendLine(title);
        AppendScenarioRow(sb, headers, widths);
        sb.AppendLine(new string('-', widths.Sum() + widths.Length - 1));
        foreach (string[] row in rows)
            AppendScenarioRow(sb, row, widths);
    }

    private static void AppendScenarioRow(StringBuilder sb, string[] columns, int[] widths)
    {
        for (int i = 0; i < columns.Length; ++i)
        {
            if (i > 0)
                sb.Append(' ');

            // Scenario names are left-aligned, all numerical columns right-aligned.
            sb.Append(i == 0
                ? columns[i].PadRight(widths[i])
                : columns[i].PadLeft(widths[i]));
        }
        sb.AppendLine();
    }

    private static string FormatDuration(TimeSpan duration) =>
        $"{(long)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}.{duration.Milliseconds:000}";

    private static string FormatLatency(TimeSpan? duration) =>
        duration.HasValue ? $"{FormatLatencyValue(duration)} ms" : "n/a";

    private static string FormatMeanLatency(LatencyStatistics latency)
    {
        string value = FormatMeanLatencyValue(latency);
        return value == "n/a" ? value : $"{value} ms";
    }

    private static string FormatMeanLatencyValue(LatencyStatistics latency) =>
        latency.MeanNanoseconds is double meanNs
            ? FormatLatencyMilliseconds(meanNs / 1_000_000.0)
            : FormatLatencyValue(latency.Mean);

    private static string FormatLatencyValue(TimeSpan? duration)
    {
        if (duration is not TimeSpan value)
            return "n/a";

        return FormatLatencyMilliseconds(value.TotalMilliseconds);
    }

    private static string FormatLatencyMilliseconds(double milliseconds)
    {
        string formatted = milliseconds.ToString("N3");
        // Preserve exact zero; distinguish positive measurements that round to 0.000 ms.
        return milliseconds > 0 && formatted == 0d.ToString("N3")
            ? "< 0.001"
            : formatted;
    }
}
