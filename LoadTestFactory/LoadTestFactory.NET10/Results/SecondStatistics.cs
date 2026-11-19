using System;

namespace LoadTestFactory.Results;

/// <summary>Represents operations completed within a single measurement time bucket.</summary>
public sealed class SecondStatistics
{
    /// <summary>Gets the one-based second number relative to the beginning of the measurement phase.</summary>
    public int Second { get; init; }

    /// <summary>Gets the number of successful operations completed during this second.</summary>
    public long SuccessCount { get; init; }

    /// <summary>Gets the number of failed operations completed during this second.</summary>
    public long FailedCount { get; init; }

    /// <summary>Gets the total number of completed operations during this second.</summary>
    public long TotalCount => SuccessCount + FailedCount;

    /// <summary>Gets the mean latency of completed operations, or null if none completed.</summary>
    public TimeSpan? MeanLatency { get; init; }

    /// <summary>Gets the maximum latency of completed operations, or null if none completed.</summary>
    public TimeSpan? MaxLatency { get; init; }
}
