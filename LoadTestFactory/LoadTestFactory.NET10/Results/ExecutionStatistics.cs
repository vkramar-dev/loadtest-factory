using System;
using System.Collections.Generic;

namespace LoadTestFactory.Results;

/// <summary>Represents execution statistics for a scenario or an entire load test.</summary>
public sealed class ExecutionStatistics
{
    /// <summary>Gets the number of completed operations, including successful and failed operations.</summary>
    public long TotalCount { get; init; }

    /// <summary>Gets the number of successful operations.</summary>
    public long SuccessCount { get; init; }

    /// <summary>Gets the number of failed operations.</summary>
    public long FailedCount { get; init; }

    /// <summary>Gets the percentage of completed operations that failed, from 0 to 100.</summary>
    /// <remarks>The value is zero when no operations completed.</remarks>
    public double FailureRate { get; init; }

    /// <summary>Gets the throughput statistics.</summary>
    public ThroughputStatistics Throughput { get; init; } = new();

    /// <summary>Gets the latency statistics for completed operations.</summary>
    public LatencyStatistics Latency { get; init; } = new();

    /// <summary>Gets the per-second statistics, including seconds without completed operations.</summary>
    public IReadOnlyList<SecondStatistics> Timeline { get; init; } = Array.Empty<SecondStatistics>();

    /// <summary>Gets the grouped errors recorded during the measurement phase.</summary>
    public IReadOnlyList<ErrorStatistics> Errors { get; init; } = Array.Empty<ErrorStatistics>();
}
