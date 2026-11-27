using System.Text;

namespace LoadTestFactory.Results;

/// <summary>
/// Represents the configuration and collected results
/// of a stepped load test.
/// </summary>
public sealed class SteppedLoadTestResult
{
    /// <summary>Gets the load generator environment snapshot.</summary>
    public TestEnvironment Environment { get; init; }

    /// <summary>Gets warnings affecting test reliability.</summary>
    public IReadOnlyList<LoadTestWarning> Warnings { get; init; } = [];

    /// <summary>Gets load generator diagnostics.</summary>
    public EngineDiagnostics Engine { get; init; } = new();

    /// <summary>Gets recorded GC pauses.</summary>
    public IReadOnlyList<GCPause> GcPauses { get; init; } = [];

    /// <summary>Gets the maximum number of concurrent workers.</summary>
    public int ParallelAmount { get; init; }

    /// <summary>Gets the configured number of load steps.</summary>
    public int StepAmount { get; init; }

    /// <summary>Gets the initial warm-up duration.</summary>
    public TimeSpan WarmupDuration { get; init; }

    /// <summary>
    /// Gets the total duration of all load steps,
    /// including stabilization intervals.
    /// </summary>
    public TimeSpan MeasurementDuration { get; init; }

    /// <summary>Gets the entire test execution duration.</summary>
    public TimeSpan ActualOverallDuration { get; init; }

    /// <summary>Gets the metrics processing duration.</summary>
    public TimeSpan MetricsProcessingDuration { get; init; }

    /// <summary>Gets the UTC start time of the stepped load phase.</summary>
    public DateTimeOffset MeasurementStartedAtUtc { get; init; }

    /// <summary>Gets the individual load step results.</summary>
    public IReadOnlyList<LoadTestStepResult> Steps { get; init; } = [];

    /// <summary>Returns the comparative stepped load report.</summary>
    public override string ToString() => FormatSummary(true);

    internal string ToStringWithoutWarnings() => FormatSummary(false);

    private string FormatSummary(bool includeWarnings)
    {
        StringBuilder sb = new();
        sb.AppendLine();
        sb.AppendLine(new string('-', 60));
        sb.AppendLine("STEPPED LOAD TEST RESULTS:");
        sb.AppendLine(new string('-', 60));
        sb.AppendLine($"Maximum workers: {ParallelAmount:N0} | Steps: {StepAmount:N0} | Duration: {MeasurementDuration.TotalSeconds:N0} sec");
        sb.AppendLine("Stabilization: first 20% of each step.");
        sb.AppendLine("Statistics include only operations starting and finishing inside the same stable window.");

        AppendTable(sb, "LOAD STEPS", ["Step", "Workers", "Duration(s)", "Stabilize(s)", "Measure(s)"],
            Steps.Select(step => new[] { step.StepNumber.ToString(), step.ParallelAmount.ToString("N0"),
                step.Duration.TotalSeconds.ToString("N0"), step.StabilizationDuration.TotalSeconds.ToString("0.###"),
                step.MeasurementDuration.TotalSeconds.ToString("0.###") }));

        if (Steps.Any(step => step.Scenarios.Any(s => s.MeasureScenario)))
            AppendStatistics(sb, "OVERALL (latency in ms)",
                Steps.Select(step => (step.ParallelAmount, step.OverallStatistics)), true);

        // Every step has the same scenario definitions in the same order.
        // Use the index rather than the name, so duplicate names do not merge.
        if (Steps.Count > 0)
        {
            for (int i = 0; i < Steps[0].Scenarios.Count; ++i)
            {
                var scenario = Steps[0].Scenarios[i];
                if (!scenario.MeasureScenario) continue;
                int index = i;
                AppendStatistics(sb, $"SCENARIO: {scenario.Name} (weight: {scenario.Weight}, latency in ms)",
                    Steps.Select(step => (step.ParallelAmount, step.Scenarios[index].Statistics)), false);
            }
        }

        foreach (string name in Steps.SelectMany(step => step.MeasuredCalls)
            .Where(c => c.Statistics.TotalCount > 0 || c.CompletionTail.TotalCount > 0)
            .Select(c => c.Name).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
        {
            AppendStatistics(sb, $"MEASURED CALL: {name} (latency in ms)",
                Steps.Select(step => (step.ParallelAmount, step.MeasuredCalls.FirstOrDefault(c => c.Name == name)?.Statistics)), false);
        }

        if (Steps.Any(step => step.CompletionTail.TotalCount > 0 || step.MeasuredCalls.Any(c => c.CompletionTail.TotalCount > 0)))
            AppendTable(sb, "COMPLETION TAIL (stable-window operations crossing the step end; final tail included)",
                ["Workers", "Scenarios", "Measured calls"], Steps.Select(step => new[] {
                    step.ParallelAmount.ToString("N0"), step.CompletionTail.TotalCount.ToString("N0"),
                    step.MeasuredCalls.Sum(c => c.CompletionTail.TotalCount).ToString("N0") }));

        if (Steps.Any(step => step.StabilizationScenarioCount > 0 || step.StabilizationMeasuredCallCount > 0))
            AppendTable(sb, "STABILIZATION (excluded operations)", ["Workers", "Scenarios", "Measured calls"],
                Steps.Select(step => new[] { step.ParallelAmount.ToString("N0"),
                    step.StabilizationScenarioCount.ToString("N0"), step.StabilizationMeasuredCallCount.ToString("N0") }));

        bool hasErrors = false;
        foreach (var step in Steps)
        {
            void AppendError(string kind, string name, string phase, ErrorStatistics error)
            {
                if (!hasErrors)
                {
                    sb.AppendLine();
                    sb.AppendLine("ERRORS BY STEP");
                    hasErrors = true;
                }
                sb.AppendLine($"  {step.ParallelAmount:N0} workers | {kind}: {name}{phase}: {error.ExceptionType} ({error.Count:N0})");
            }
            foreach (var scenario in step.Scenarios)
            {
                foreach (var error in scenario.Statistics.Errors) AppendError("Scenario", scenario.Name, "", error);
                foreach (var error in scenario.CompletionTail.Errors) AppendError("Scenario", scenario.Name, " (completion tail)", error);
            }
            foreach (var call in step.MeasuredCalls)
            {
                foreach (var error in call.Statistics.Errors) AppendError("Call", call.Name, "", error);
                foreach (var error in call.CompletionTail.Errors) AppendError("Call", call.Name, " (completion tail)", error);
            }
            foreach (var item in step.StabilizationErrors)
                AppendError(item.IsMeasuredCall ? "Call" : "Scenario", item.Name, " (stabilization)", item.Error);
        }

        if (GcPauses.Count > 0 || Engine.BufferBlockingCount > 0)
        {
            sb.AppendLine();
            sb.AppendLine("GENERATOR DIAGNOSTICS:");
            if (GcPauses.Count > 0)
            {
                sb.AppendLine($"GC pauses: {GcPauses.Count:N0}");
                sb.AppendLine($"GC pause time: {GcPauses.Sum(p => Math.Max(0, p.EndMs - p.StartMs)):N0} ms");
            }
            if (Engine.BufferBlockingCount > 0)
            {
                sb.AppendLine($"Storage buffer waits: {Engine.BufferBlockingCount:N0}");
                sb.AppendLine($"Total wait time: {Engine.TotalBufferBlockingTime:c}");
                sb.AppendLine($"Longest wait: {Engine.MaxBufferBlockingTime:c}");
            }
        }
        if (includeWarnings && Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("WARNINGS:");
            foreach (var warning in Warnings)
                sb.AppendLine($"  [{warning.DisplayCode}] {warning.Message}");
        }
        return sb.ToString().TrimEnd();
    }

    private static void AppendStatistics(StringBuilder sb, string title,
        IEnumerable<(int Workers, ExecutionStatistics Statistics)> items, bool overall)
    {
        string[] headers = overall
            ? ["Workers", "Count", "Failed", "RPS", "Mean", "P50", "P95", "P99", "P99.9"]
            : ["Workers", "Count", "Failed", "RPS", "Mean", "P95", "P99"];
        AppendTable(sb, title, headers, items.Select(item =>
        {
            ExecutionStatistics stats = item.Statistics ?? new();
            LatencyStatistics latency = stats.Latency ?? new();
            string mean = latency.MeanNanoseconds is double ns
                ? FormatMilliseconds(ns / 1_000_000.0) : FormatLatency(latency.Mean);
            string[] row = [item.Workers.ToString("N0"), stats.TotalCount.ToString("N0"),
                stats.FailedCount.ToString("N0"), stats.Throughput.Mean.ToString("N2"), mean,
                .. (overall ? new[] { FormatLatency(latency.P50) } : Array.Empty<string>()),
                FormatLatency(latency.P95), FormatLatency(latency.P99),
                .. (overall ? new[] { FormatLatency(latency.P999) } : Array.Empty<string>())];
            return row;
        }));
    }

    private static string FormatLatency(TimeSpan? value) => value.HasValue
        ? FormatMilliseconds(value.Value.TotalMilliseconds) : "n/a";

    private static string FormatMilliseconds(double value)
    {
        string formatted = value.ToString("N3");
        return value > 0 && formatted == 0d.ToString("N3") ? "< 0.001" : formatted;
    }

    private static void AppendTable(StringBuilder sb, string title, string[] headers,
        IEnumerable<string[]> source)
    {
        string[][] rows = source.ToArray();
        int[] widths = headers.Select(h => h.Length).ToArray();
        foreach (var row in rows)
            for (int i = 0; i < widths.Length; ++i)
                widths[i] = Math.Max(widths[i], row[i].Length);
        sb.AppendLine();
        sb.AppendLine(title);
        sb.AppendLine(string.Join("  ", headers.Select((h, i) => h.PadLeft(widths[i]))));
        sb.AppendLine(new string('-', widths.Sum() + 2 * (widths.Length - 1)));
        foreach (var row in rows)
            sb.AppendLine(string.Join("  ", row.Select((v, i) => v.PadLeft(widths[i]))));
    }
}
