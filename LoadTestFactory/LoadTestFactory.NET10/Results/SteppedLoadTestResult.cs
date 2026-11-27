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
}