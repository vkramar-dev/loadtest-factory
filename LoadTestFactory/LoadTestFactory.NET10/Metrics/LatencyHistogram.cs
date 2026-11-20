using System.Numerics;

namespace LoadTestFactory.Metrics;

/// <summary>
/// Logarithmic latency histogram with 2048 subdivisions per power-of-two range.
/// Resolution is better than 0.05% at large latencies; values below 2048
/// Stopwatch ticks are represented exactly. No per-measurement allocation.
/// </summary>
internal sealed class LatencyHistogram
{
    private const int SubdivisionBits = 11;
    private const int Subdivisions = 1 << SubdivisionBits;
    // Positive Int64 values have exponents 0..62; index 0 is zero latency.
    private const int BucketCount = 1 + 63 * Subdivisions;

    private long[] _counts;
    private long _total;

    public long Count => _total;

    public void Record(long stopwatchTicks)
    {
        if (stopwatchTicks < 0)
            throw new InvalidDataException("Negative elapsed time in a measurement.");

        _counts ??= new long[BucketCount];
        ++_counts[GetIndex(stopwatchTicks)];
        ++_total;
    }

    public void MergeFrom(LatencyHistogram other)
    {
        if (other._counts == null)
            return;

        _counts ??= new long[BucketCount];
        for (int i = 0; i < BucketCount; ++i)
            _counts[i] += other._counts[i];

        _total += other._total;
    }

    public long EstimatePercentile(double percentile)
    {
        if (_total == 0)
            throw new InvalidOperationException("No latency samples were recorded.");
        if (percentile < 0 || percentile > 1)
            throw new ArgumentOutOfRangeException(nameof(percentile));

        long target = Math.Max(1, (long)Math.Ceiling(_total * percentile));
        long accumulated = 0;
        for (int i = 0; i < _counts.Length; ++i)
        {
            accumulated += _counts[i];
            if (accumulated >= target)
                return Representative(i);
        }

        throw new InvalidDataException("Latency histogram count mismatch.");
    }

    private static int GetIndex(long ticks)
    {
        if (ticks == 0)
            return 0;

        int exponent = BitOperations.Log2((ulong)ticks);
        ulong offset = (ulong)ticks - (1UL << exponent);
        int subdivision = exponent >= SubdivisionBits
            ? (int)(offset >> (exponent - SubdivisionBits))
            : (int)(offset << (SubdivisionBits - exponent));

        return 1 + exponent * Subdivisions + subdivision;
    }

    private static long Representative(int index)
    {
        if (index == 0)
            return 0;

        int adjusted = index - 1;
        int exponent = adjusted / Subdivisions;
        int subdivision = adjusted % Subdivisions;
        ulong lower = 1UL << exponent;

        if (exponent >= SubdivisionBits)
        {
            ulong step = 1UL << (exponent - SubdivisionBits);
            return (long)(lower + (ulong)subdivision * step + (step - 1) / 2);
        }

        // For values below 2048, each populated bucket represents one tick.
        return (long)(lower + ((ulong)subdivision >> (SubdivisionBits - exponent)));
    }
}
