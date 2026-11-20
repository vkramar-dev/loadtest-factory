using System;
using System.Collections.Generic;

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

    /// <summary>Gets the actual duration of the measurement phase, including worker completion.</summary>
    public TimeSpan ActualMeasurementDuration { get; init; }

    /// <summary>Gets the UTC time at which the measurement phase began.</summary>
    public DateTimeOffset MeasurementStartedAtUtc { get; init; }

    /// <summary>Gets the combined statistics for all test scenarios.</summary>
    public ExecutionStatistics OverallStatistics { get; init; } = new();

    /// <summary>Gets the individual results of the test scenarios.</summary>
    public IReadOnlyList<ScenarioResult> Scenarios { get; init; } = Array.Empty<ScenarioResult>();
}
