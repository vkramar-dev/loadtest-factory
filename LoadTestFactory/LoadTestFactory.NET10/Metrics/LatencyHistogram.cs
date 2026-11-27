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

    private const int PageBits = 8;
    private const int PageSize = 1 << PageBits;
    private readonly bool _sparse;
    private long[][] _pages;
    private long[] _counts;

    internal LatencyHistogram(bool sparse = false) => _sparse = sparse;
    private long _total;

    public long Count => _total;

    public void Record(long stopwatchTicks)
    {
        if (stopwatchTicks < 0)
            throw new InvalidDataException("Negative elapsed time in a measurement.");

        AddCount(GetIndex(stopwatchTicks), 1);
        ++_total;
    }

    public void MergeFrom(LatencyHistogram other)
    {
        if (other._total == 0)
            return;

        if (!_sparse && !other._sparse)
        {
            _counts ??= new long[BucketCount];
            for (int i = 0; i < BucketCount; ++i)
                _counts[i] += other._counts[i];
        }
        else
        {
            foreach (var (index, count) in other.GetNonzeroCounts())
                AddCount(index, count);
        }
        _total += other._total;
    }

    private void AddCount(int index, long count)
    {
        if (!_sparse)
        {
            _counts ??= new long[BucketCount];
            _counts[index] += count;
            return;
        }
        _pages ??= new long[(BucketCount + PageSize - 1) / PageSize][];
        long[] page = _pages[index >> PageBits] ??= new long[PageSize];
        page[index & (PageSize - 1)] += count;
    }

    private IEnumerable<(int Index, long Count)> GetNonzeroCounts()
    {
        if (!_sparse)
        {
            if (_counts != null)
                for (int i = 0; i < _counts.Length; ++i)
                    if (_counts[i] != 0) yield return (i, _counts[i]);
            yield break;
        }
        if (_pages == null) yield break;
        for (int p = 0; p < _pages.Length; ++p)
        {
            long[] page = _pages[p];
            if (page == null) continue;
            for (int i = 0; i < page.Length; ++i)
                if (page[i] != 0) yield return ((p << PageBits) + i, page[i]);
        }
    }

    public long EstimatePercentile(double percentile)
    {
        if (_total == 0)
            throw new InvalidOperationException("No latency samples were recorded.");
        if (percentile < 0 || percentile > 1)
            throw new ArgumentOutOfRangeException(nameof(percentile));

        long target = Math.Max(1, (long)Math.Ceiling(_total * percentile));
        long accumulated = 0;
        if (!_sparse)
        {
            for (int i = 0; i < _counts.Length; ++i)
            {
                accumulated += _counts[i];
                if (accumulated >= target) return Representative(i);
            }
        }
        else
        {
            foreach (var (index, count) in GetNonzeroCounts())
            {
                accumulated += count;
                if (accumulated >= target) return Representative(index);
            }
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
