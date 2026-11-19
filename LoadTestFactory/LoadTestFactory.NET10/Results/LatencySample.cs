using System;

namespace LoadTestFactory.Results;

/// <summary>Represents an individual latency measurement.</summary>
public sealed class LatencySample
{
    /// <summary>Gets the name of the scenario that produced the measurement.</summary>
    public string ScenarioName { get; init; }

    /// <summary>Gets the operation start time relative to the beginning of the measurement phase.</summary>
    public TimeSpan StartedAt { get; init; }

    /// <summary>Gets the elapsed duration of the operation.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Gets a value indicating whether the operation completed successfully.</summary>
    public bool IsSuccess { get; init; }
}
