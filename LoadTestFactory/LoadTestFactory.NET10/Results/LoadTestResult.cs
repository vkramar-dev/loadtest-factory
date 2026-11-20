using System;
using System.Collections.Generic;
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

    /// <summary>Returns a compact human-readable summary suitable for console output.</summary>
    public override string ToString()
    {
        StringBuilder sb = new(2048);
        ExecutionStatistics overall = OverallStatistics ?? new();
        LatencyStatistics latency = overall.Latency ?? new();
        ThroughputStatistics throughput = overall.Throughput ?? new();

        sb.AppendLine();
        sb.AppendLine(new string('-', 60));
        sb.AppendLine("LOAD TEST RESULTS");
        sb.AppendLine(new string('-', 60));
        AppendMetric(sb, "Workers:", ParallelAmount.ToString("N0"));
        AppendMetric(sb, "Scenarios:", (Scenarios?.Count ?? 0).ToString("N0"));
        AppendMetric(sb, "Warmup:", FormatDuration(WarmupDuration));
        AppendMetric(sb, "Measurement:", FormatDuration(MeasurementDuration));
        AppendMetric(sb, "Overall duration:", FormatDuration(ActualOverallDuration));
        AppendMetric(sb, "Metrics processing:", FormatDuration(MetricsProcessingDuration));
        AppendMetric(sb, "GC mode:", Environment == null
            ? "Unknown"
            : Environment.IsServerGC ? "Server" : "Workstation");

        sb.AppendLine();
        sb.AppendLine("OVERALL");
        AppendMetric(sb, "Executions:", overall.TotalCount.ToString("N0"));
        AppendMetric(sb, "Successful:", overall.SuccessCount.ToString("N0"));
        AppendMetric(sb, "Failed:", $"{overall.FailedCount:N0} ({overall.FailureRate:N2}%)");
        AppendMetric(sb, "RPS (mean):", throughput.Mean.ToString("N2"));
        AppendMetric(sb, "RPS (min / peak):", $"{throughput.Min:N2} / {throughput.Peak:N2}");
        AppendMetric(sb, "Latency mean:", FormatLatency(latency.Mean));
        AppendMetric(sb, "Latency P50:", FormatLatency(latency.P50));
        AppendMetric(sb, "Latency P90:", FormatLatency(latency.P90));
        AppendMetric(sb, "Latency P95:", FormatLatency(latency.P95));
        AppendMetric(sb, "Latency P99:", FormatLatency(latency.P99));
        AppendMetric(sb, "Latency P99.9:", FormatLatency(latency.P999));
        if (latency.Top5Min?.Count > 0)
            AppendMetric(sb, "Fastest sample:", FormatLatency(latency.Top5Min[0].Duration));
        if (latency.Top5Max?.Count > 0)
            AppendMetric(sb, "Slowest sample:", FormatLatency(latency.Top5Max[0].Duration));

        sb.AppendLine();
        sb.AppendLine("SCENARIOS (latency in ms)");
        int nameWidth = "Scenario".Length;
        if (Scenarios != null)
        {
            foreach (ScenarioResult scenario in Scenarios)
            {
                if (scenario != null)
                    nameWidth = Math.Max(nameWidth, (scenario.Name ?? "").Length);
            }
        }

        AppendScenarioRow(sb, "Scenario", "Count", "Failed", "RPS", "Mean", "P95", "P99", nameWidth);
        sb.AppendLine(new string('-', nameWidth + 1 + 12 + 1 + 9 + 1 + 11 + 1 + 10 + 1 + 10 + 1 + 10));
        if (Scenarios != null)
        {
            foreach (ScenarioResult scenario in Scenarios)
            {
                if (scenario == null)
                    continue;

                ExecutionStatistics stats = scenario.Statistics ?? new();
                LatencyStatistics scenarioLatency = stats.Latency ?? new();
                AppendScenarioRow(sb,
                    scenario.Name ?? "(unnamed)",
                    stats.TotalCount.ToString("N0"),
                    $"{stats.FailedCount:N0}",
                    (stats.Throughput?.Mean ?? 0).ToString("N2"),
                    FormatLatencyValue(scenarioLatency.Mean),
                    FormatLatencyValue(scenarioLatency.P95),
                    FormatLatencyValue(scenarioLatency.P99),
                    nameWidth);
            }
        }

        bool hasErrors = false;
        if (Scenarios != null)
        {
            foreach (ScenarioResult scenario in Scenarios)
            {
                if (scenario?.Statistics?.Errors == null)
                    continue;

                foreach (ErrorStatistics error in scenario.Statistics.Errors)
                {
                    if (!hasErrors)
                    {
                        sb.AppendLine();
                        sb.AppendLine("ERRORS BY SCENARIO");
                        hasErrors = true;
                    }
                    sb.AppendLine($"  {scenario.Name}: {error.ExceptionType} ({error.Count:N0})");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("COMPLETION TAIL (excluded from measurement statistics)");
        CompletionTailStatistics tail = CompletionTail ?? new();
        AppendMetric(sb, "Executions:", tail.TotalCount.ToString("N0"));
        AppendMetric(sb, "Successful:", tail.SuccessCount.ToString("N0"));
        AppendMetric(sb, "Failed:", $"{tail.FailedCount:N0} ({tail.FailureRate:N2}%)");

        sb.AppendLine();
        sb.AppendLine("GENERATOR DIAGNOSTICS");
        long gcDurationMs = 0;
        if (GcPauses != null)
        {
            foreach (var pause in GcPauses)
                gcDurationMs += Math.Max(0, pause.EndMs - pause.StartMs);
        }
        AppendMetric(sb, "GC pauses:", (GcPauses?.Count ?? 0).ToString("N0"));
        AppendMetric(sb, "GC pause time:", $"{gcDurationMs:N0} ms");
        EngineDiagnostics engine = Engine ?? new();
        AppendMetric(sb, "Buffer blockings:", engine.BufferBlockingCount.ToString("N0"));
        AppendMetric(sb, "Blocking total:", FormatDuration(engine.TotalBufferBlockingTime));
        AppendMetric(sb, "Blocking max:", FormatDuration(engine.MaxBufferBlockingTime));

        if (Warnings != null && Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("WARNINGS");
            foreach (LoadTestWarning warning in Warnings)
                sb.AppendLine($"  [{warning.Code}] {warning.Message}");
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendMetric(StringBuilder sb, string label, string value)
    {
        // Values begin at the same 24-column tab stop, as in the startup banner.
        int tabs = Math.Max(1, (24 - label.Length + 7) / 8);
        sb.Append(label).Append('\t', tabs).AppendLine(value);
    }

    private static void AppendScenarioRow(StringBuilder sb, string name, string count,
        string failed, string rps, string mean, string p95, string p99, int nameWidth)
    {
        sb.Append(name.PadRight(nameWidth)).Append(' ')
            .Append(count.PadLeft(12)).Append(' ')
            .Append(failed.PadLeft(9)).Append(' ')
            .Append(rps.PadLeft(11)).Append(' ')
            .Append(mean.PadLeft(10)).Append(' ')
            .Append(p95.PadLeft(10)).Append(' ')
            .AppendLine(p99.PadLeft(10));
    }

    private static string FormatDuration(TimeSpan duration) =>
        $"{(long)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}.{duration.Milliseconds:000}";

    private static string FormatLatency(TimeSpan? duration) =>
        duration is TimeSpan value ? $"{value.TotalMilliseconds:N3} ms" : "n/a";

    private static string FormatLatencyValue(TimeSpan? duration) =>
        duration is TimeSpan value ? value.TotalMilliseconds.ToString("N3") : "n/a";
}
