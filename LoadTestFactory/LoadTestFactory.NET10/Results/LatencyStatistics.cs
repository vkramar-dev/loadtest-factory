using System;
using System.Collections.Generic;

namespace LoadTestFactory.Results;

/// <summary>
/// Represents the latency distribution of completed operations, including failures.
/// </summary>
/// <remarks>Scalar latency values are null when no operations completed.</remarks>
public sealed class LatencyStatistics
{
    /// <summary>Gets the arithmetic mean latency rounded to TimeSpan precision (100 ns).</summary>
    public TimeSpan? Mean { get; init; }

    /// <summary>
    /// Gets the arithmetic mean latency in nanoseconds, retaining fractional
    /// Stopwatch-tick precision lost when converting the mean to TimeSpan.
    /// The precision of individual measurements is still limited by Stopwatch.
    /// </summary>
    public double? MeanNanoseconds { get; init; }

    /// <summary>Gets the 50th percentile (median) latency.</summary>
    public TimeSpan? P50 { get; init; }

    /// <summary>Gets the 90th percentile latency.</summary>
    public TimeSpan? P90 { get; init; }

    /// <summary>Gets the 95th percentile latency.</summary>
    public TimeSpan? P95 { get; init; }

    /// <summary>Gets the 99th percentile latency.</summary>
    public TimeSpan? P99 { get; init; }

    /// <summary>Gets the 99.9th percentile latency.</summary>
    public TimeSpan? P999 { get; init; }

    /// <summary>Gets up to five fastest operations, ordered from fastest to slowest.</summary>
    public IReadOnlyList<LatencySample> Top5Min { get; init; } = Array.Empty<LatencySample>();

    /// <summary>Gets up to five slowest operations, ordered from slowest to fastest.</summary>
    public IReadOnlyList<LatencySample> Top5Max { get; init; } = Array.Empty<LatencySample>();
}
