namespace LoadTestFactory.Results;

/// <summary>Represents the results of an individual test scenario.</summary>
public sealed class ScenarioResult
{
    /// <summary>Gets the name of the scenario.</summary>
    public string Name { get; init; }

    /// <summary>Gets the configured weight of the scenario.</summary>
    public int Weight { get; init; }

    /// <summary>Gets the execution statistics for the scenario.</summary>
    public ExecutionStatistics Statistics { get; init; } = new();
}
