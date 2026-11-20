using System;
using System.Collections.Generic;

namespace LoadTestFactory.Results;

/// <summary>
/// Operations started during measurement but finished at or after its end.
/// Excluded from normal throughput, latency distributions and failure rates.
/// </summary>
public sealed class CompletionTailStatistics
{
    public long TotalCount { get; init; }
    public long SuccessCount { get; init; }
    public long FailedCount { get; init; }
    public double FailureRate { get; init; }
    public TimeSpan? FirstCompletionAt { get; init; }
    public TimeSpan? LastCompletionAt { get; init; }
    public LatencyStatistics Latency { get; init; } = new();
    public IReadOnlyList<ErrorStatistics> Errors { get; init; } = Array.Empty<ErrorStatistics>();
}
