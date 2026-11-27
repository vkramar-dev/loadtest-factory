namespace LoadTestFactory.Results;

/// <summary>
/// Represents the collected results of an individual load step.
/// </summary>
public sealed class LoadTestStepResult
{
    /// <summary>Gets the one-based step number.</summary>
    public int StepNumber { get; init; }

    /// <summary>Gets the number of active workers during the step.</summary>
    public int ParallelAmount { get; init; }

    /// <summary>Gets the initial stabilization duration.</summary>
    public TimeSpan StabilizationDuration { get; init; }

    /// <summary>Gets the duration of the measurement window.</summary>
    public TimeSpan MeasurementDuration { get; init; }

    /// <summary>Gets the total step duration.</summary>
    public TimeSpan Duration =>
        StabilizationDuration + MeasurementDuration;

    /// <summary>Gets the UTC start time of the measurement window.</summary>
    public DateTimeOffset MeasurementStartedAtUtc { get; init; }

    /// <summary>Gets the combined statistics for all scenarios.</summary>
    public ExecutionStatistics OverallStatistics { get; init; } = new();

    /// <summary>Gets the completion tail statistics.</summary>
    public CompletionTailStatistics CompletionTail { get; init; } = new();

    /// <summary>Gets the individual scenario results.</summary>
    public IReadOnlyList<ScenarioResult> Scenarios { get; init; } = [];

    /// <summary>Gets the individual measured call results.</summary>
    public IReadOnlyList<MeasuredCallResult> MeasuredCalls { get; init; } = [];
}