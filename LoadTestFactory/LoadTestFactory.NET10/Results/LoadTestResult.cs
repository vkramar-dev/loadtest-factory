using System;
using System.Collections.Generic;

namespace LoadTestFactory.Results;

/// <summary>
/// Represents the configuration and collected results of a load test.
/// </summary>
public sealed class LoadTestResult
{
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
