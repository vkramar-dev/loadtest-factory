namespace LoadTestFactory.Results;

/// <summary>Represents throughput statistics in completed operations per second.</summary>
public sealed class ThroughputStatistics
{
    /// <summary>Gets the mean throughput over the configured measurement window (excluding completion tail).</summary>
    public double Mean { get; init; }

    /// <summary>Gets the minimum throughput observed in the per-second timeline.</summary>
    public double Min { get; init; }

    /// <summary>Gets the maximum throughput observed in the per-second timeline.</summary>
    public double Peak { get; init; }
}
