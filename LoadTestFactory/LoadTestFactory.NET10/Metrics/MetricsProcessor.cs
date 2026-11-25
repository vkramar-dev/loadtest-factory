using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LoadTestFactory.Metrics;

internal sealed class MetricsProcessingResult
{
    internal ExecutionStatistics Overall { get; init; }
    internal CompletionTailStatistics CompletionTail { get; init; }
    internal ScenarioResult[] Scenarios { get; init; }
    internal MeasuredCallResult[] MeasuredCalls { get; init; }
}

/// <summary>
/// Processes each persisted measurement exactly once. Only aggregated statistics
/// and two reusable read-ahead buffers are held in managed memory.
/// </summary>
internal static class MetricsProcessor
{
    // Exactly 262,144 measurements per buffer (6 MiB), two buffers total.
    private const int MeasurementsPerChunk = 262_144;
    private const int ChunkBytes = MeasurementsPerChunk * 24;

    internal static async Task<MetricsProcessingResult> ProcessAsync(
        MetricsCollector[] collectors,
        TestScenario[] scenarios,
        long measurementStartTimestamp,
        long measurementEndTimestamp,
        int measurementSeconds,
        CancellationToken cancellationToken = default)
    {
        scenarios = scenarios.Where(s => s.MeasureScenario).ToArray();

        var measuredCalls = MeasuredCall.GetRegisteredCalls();
        Dictionary<int, ScenarioAccumulator> byId = new(scenarios.Length + measuredCalls.Length);
        ScenarioAccumulator[] measuredCallStats = new ScenarioAccumulator[measuredCalls.Length];
        ScenarioAccumulator[] scenarioStats = new ScenarioAccumulator[scenarios.Length];

        for (int i = 0; i < scenarios.Length; ++i)
        {
            TestScenario scenario = scenarios[i];
            ScenarioAccumulator stats = new(scenario.Name, scenario.Weight, measurementSeconds, measurementStartTimestamp);
            if (!byId.TryAdd(scenario.Id, stats))
                throw new ArgumentException($"Duplicate scenario ID: {scenario.Id}.", nameof(scenarios));

            scenarioStats[i] = stats;
        }

        for (int i = 0; i < measuredCalls.Length; ++i)
        {
            var call = measuredCalls[i];
            ScenarioAccumulator stats = new(call.Key, 0, measurementSeconds, measurementStartTimestamp);
            if (!byId.TryAdd(call.Value, stats))
                throw new InvalidOperationException($"Duplicate measured call ID: {call.Value}.");

            measuredCallStats[i] = stats;
        }

        byte[][] readBuffers = [new byte[ChunkBytes], new byte[ChunkBytes]];

        foreach (MetricsCollector collector in collectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // ResultType indexes are local to the collector; never resolve them
            // through another worker's list.
            IReadOnlyList<string> errorTypes = collector.ResultTypes;

            foreach (string path in collector.GetCompletedSegmentFiles())
            {
                await ReadSegmentAsync(path, readBuffers, byId, errorTypes,
                    measurementStartTimestamp, measurementEndTimestamp,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        StatisticsAccumulator overall = new(measurementSeconds, measurementStartTimestamp, true);
        StatisticsAccumulator overallTail = new(0, measurementStartTimestamp, false);
        ScenarioResult[] results = new ScenarioResult[scenarioStats.Length];

        for (int i = 0; i < scenarioStats.Length; ++i)
        {
            ScenarioAccumulator scenario = scenarioStats[i];
            overall.MergeFrom(scenario.Normal);
            overallTail.MergeFrom(scenario.Tail);

            results[i] = new ScenarioResult
            {
                Name = scenario.Name,
                Weight = scenario.Weight,
                Statistics = scenario.Normal.BuildExecutionStatistics(measurementSeconds),
                CompletionTail = scenario.Tail.BuildCompletionTail()
            };
        }

        MeasuredCallResult[] callResults = new MeasuredCallResult[measuredCallStats.Length];
        for (int i = 0; i < measuredCallStats.Length; ++i)
        {
            ScenarioAccumulator call = measuredCallStats[i];
            callResults[i] = new MeasuredCallResult
            {
                Name = call.Name,
                Statistics = call.Normal.BuildExecutionStatistics(measurementSeconds),
                CompletionTail = call.Tail.BuildCompletionTail()
            };
        }

        return new MetricsProcessingResult
        {
            Overall = overall.BuildExecutionStatistics(measurementSeconds),
            CompletionTail = overallTail.BuildCompletionTail(),
            Scenarios = results,
            MeasuredCalls = callResults
        };
    }

    private static async Task ReadSegmentAsync(
        string path,
        byte[][] buffers,
        Dictionary<int, ScenarioAccumulator> byId,
        IReadOnlyList<string> errorTypes,
        long start,
        long end,
        CancellationToken token)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

        long remaining = stream.Length;
        int measurementSize = Marshal.SizeOf<Measurement>();
        if (measurementSize != 24 || remaining % measurementSize != 0)
            throw new InvalidDataException($"Invalid measurement segment size: {path}.");
        if (remaining == 0)
            return;

        int active = 0;
        int currentBytes = (int)Math.Min(remaining, ChunkBytes);
        ValueTask pendingRead = stream.ReadExactlyAsync(
            buffers[active].AsMemory(0, currentBytes), token);

        while (true)
        {
            // Complete the read for this buffer. The next one starts before
            // we begin CPU processing of the current chunk.
            await pendingRead.ConfigureAwait(false);
            remaining -= currentBytes;

            int next = active ^ 1;
            int nextBytes = (int)Math.Min(remaining, ChunkBytes);
            if (nextBytes > 0)
                pendingRead = stream.ReadExactlyAsync(
                    buffers[next].AsMemory(0, nextBytes), token);

            ProcessChunk(buffers[active].AsSpan(0, currentBytes), byId,
                errorTypes, start, end);

            if (nextBytes == 0)
                break;

            active = next;
            currentBytes = nextBytes;
        }
    }

    private static void ProcessChunk(
        ReadOnlySpan<byte> bytes,
        Dictionary<int, ScenarioAccumulator> byId,
        IReadOnlyList<string> errorTypes,
        long measurementStart,
        long measurementEnd)
    {
        foreach (ref readonly Measurement measurement in MemoryMarshal.Cast<byte, Measurement>(bytes))
        {
            if (measurement.ElapsedTicks < 0 || measurement.StartAt < 0 ||
                measurement.StartAt > long.MaxValue - measurement.ElapsedTicks)
                throw new InvalidDataException("Corrupted measurement timestamp.");

            long finishedAt = measurement.StartAt + measurement.ElapsedTicks;
            if (finishedAt < measurementStart)
                throw new InvalidDataException("Measurement completed before measurement phase started.");

            if ((uint)measurement.ResultType >= (uint)errorTypes.Count)
                throw new InvalidDataException("Invalid ResultType index in measurement segment.");

            string errorType = measurement.ResultType == 0 ? null : errorTypes[measurement.ResultType];
            // Compatibility with older segments that stored internal blocking
            // as a synthetic failure. This is an engine-side diagnostic only.
            if (errorType == "Performance Blocking")
                continue;

            if (!byId.TryGetValue(measurement.ScenarioId, out ScenarioAccumulator scenario))
                throw new InvalidDataException($"Unknown scenario ID {measurement.ScenarioId}.");

            StatisticsAccumulator destination = finishedAt < measurementEnd
                ? scenario.Normal : scenario.Tail;

            destination.Record(in measurement, finishedAt, scenario.Name, errorType);
        }
    }

    private sealed class ScenarioAccumulator
    {
        internal readonly string Name;
        internal readonly int Weight;
        internal readonly StatisticsAccumulator Normal;
        internal readonly StatisticsAccumulator Tail;

        internal ScenarioAccumulator(string name, int weight, int seconds, long start)
        {
            Name = name;
            Weight = weight;
            Normal = new StatisticsAccumulator(seconds, start, true);
            Tail = new StatisticsAccumulator(0, start, false);
        }
    }

    private struct SecondBucket
    {
        internal long SuccessCount;
        internal long FailedCount;
        internal double SumLatencyTicks;
        internal long MaxLatencyTicks;
    }

    private readonly record struct SampleEntry(
        string ScenarioName, long StartTimestamp, long ElapsedTicks, bool IsSuccess);

    private sealed class ErrorBucket
    {
        internal long Count;
        internal long FirstTimestamp;
        internal long LastTimestamp;

        internal ErrorBucket(long timestamp)
        {
            FirstTimestamp = timestamp;
            LastTimestamp = timestamp;
        }
    }

    private sealed class StatisticsAccumulator
    {
        private readonly long _start;
        private readonly SecondBucket[] _timeline;
        private readonly LatencyHistogram _histogram = new();
        private readonly List<SampleEntry> _fastest = new(5);
        private readonly List<SampleEntry> _slowest = new(5);
        private readonly Dictionary<string, ErrorBucket> _errors = new(StringComparer.Ordinal);

        private long _success;
        private long _failed;
        private double _sumLatencyTicks;
        private long? _firstCompletion;
        private long? _lastCompletion;

        internal StatisticsAccumulator(int durationSeconds, long start, bool timeline)
        {
            _start = start;
            if (timeline)
                _timeline = new SecondBucket[durationSeconds];
        }

        internal void Record(in Measurement m, long finishedAt, string scenarioName, string errorType)
        {
            bool succeeded = errorType == null;
            if (succeeded)
                ++_success;
            else
                ++_failed;

            _sumLatencyTicks += m.ElapsedTicks;
            _histogram.Record(m.ElapsedTicks);
            SampleEntry sample = new(scenarioName, m.StartAt, m.ElapsedTicks, succeeded);
            InsertFastest(sample);
            InsertSlowest(sample);

            if (!_firstCompletion.HasValue || finishedAt < _firstCompletion.Value)
                _firstCompletion = finishedAt;
            if (!_lastCompletion.HasValue || finishedAt > _lastCompletion.Value)
                _lastCompletion = finishedAt;

            if (_timeline != null)
            {
                int second = (int)((finishedAt - _start) / Stopwatch.Frequency);
                if ((uint)second >= (uint)_timeline.Length)
                    throw new InvalidDataException("Measurement completion is outside the measurement window.");

                ref SecondBucket bucket = ref _timeline[second];
                if (succeeded) ++bucket.SuccessCount;
                else ++bucket.FailedCount;
                bucket.SumLatencyTicks += m.ElapsedTicks;
                bucket.MaxLatencyTicks = Math.Max(bucket.MaxLatencyTicks, m.ElapsedTicks);
            }

            if (errorType != null)
            {
                if (!_errors.TryGetValue(errorType, out ErrorBucket error))
                {
                    error = new ErrorBucket(finishedAt);
                    _errors.Add(errorType, error);
                }
                ++error.Count;
                error.FirstTimestamp = Math.Min(error.FirstTimestamp, finishedAt);
                error.LastTimestamp = Math.Max(error.LastTimestamp, finishedAt);
            }
        }

        internal void MergeFrom(StatisticsAccumulator other)
        {
            _success += other._success;
            _failed += other._failed;
            _sumLatencyTicks += other._sumLatencyTicks;
            _histogram.MergeFrom(other._histogram);

            foreach (SampleEntry sample in other._fastest) InsertFastest(sample);
            foreach (SampleEntry sample in other._slowest) InsertSlowest(sample);

            if (other._firstCompletion.HasValue &&
                (!_firstCompletion.HasValue || other._firstCompletion < _firstCompletion))
                _firstCompletion = other._firstCompletion;
            if (other._lastCompletion.HasValue &&
                (!_lastCompletion.HasValue || other._lastCompletion > _lastCompletion))
                _lastCompletion = other._lastCompletion;

            if (_timeline != null && other._timeline != null)
            {
                for (int i = 0; i < _timeline.Length; ++i)
                {
                    _timeline[i].SuccessCount += other._timeline[i].SuccessCount;
                    _timeline[i].FailedCount += other._timeline[i].FailedCount;
                    _timeline[i].SumLatencyTicks += other._timeline[i].SumLatencyTicks;
                    _timeline[i].MaxLatencyTicks = Math.Max(
                        _timeline[i].MaxLatencyTicks, other._timeline[i].MaxLatencyTicks);
                }
            }

            foreach (KeyValuePair<string, ErrorBucket> item in other._errors)
            {
                if (!_errors.TryGetValue(item.Key, out ErrorBucket existing))
                {
                    existing = new ErrorBucket(item.Value.FirstTimestamp);
                    _errors.Add(item.Key, existing);
                }
                existing.Count += item.Value.Count;
                existing.FirstTimestamp = Math.Min(existing.FirstTimestamp, item.Value.FirstTimestamp);
                existing.LastTimestamp = Math.Max(existing.LastTimestamp, item.Value.LastTimestamp);
            }
        }

        internal ExecutionStatistics BuildExecutionStatistics(int measurementSeconds)
        {
            long total = _success + _failed;
            SecondStatistics[] timeline = new SecondStatistics[measurementSeconds];
            long min = long.MaxValue;
            long max = 0;

            for (int i = 0; i < measurementSeconds; ++i)
            {
                SecondBucket bucket = _timeline[i];
                long count = bucket.SuccessCount + bucket.FailedCount;
                min = Math.Min(min, count);
                max = Math.Max(max, count);
                timeline[i] = new SecondStatistics
                {
                    Second = i + 1,
                    SuccessCount = bucket.SuccessCount,
                    FailedCount = bucket.FailedCount,
                    TotalLatency = ToDuration(bucket.SumLatencyTicks),
                    MeanLatency = count == 0 ? null : ToDuration(bucket.SumLatencyTicks / count),
                    MaxLatency = count == 0 ? null : ToDuration(bucket.MaxLatencyTicks)
                };
            }

            return new ExecutionStatistics
            {
                TotalCount = total,
                SuccessCount = _success,
                FailedCount = _failed,
                FailureRate = total == 0 ? 0 : _failed * 100.0 / total,
                Throughput = new ThroughputStatistics
                {
                    Mean = total / (double)measurementSeconds,
                    Min = min == long.MaxValue ? 0 : min,
                    Peak = max
                },
                Latency = BuildLatency(),
                Timeline = timeline,
                Errors = BuildErrors()
            };
        }

        internal CompletionTailStatistics BuildCompletionTail()
        {
            long total = _success + _failed;
            return new CompletionTailStatistics
            {
                TotalCount = total,
                SuccessCount = _success,
                FailedCount = _failed,
                FailureRate = total == 0 ? 0 : _failed * 100.0 / total,
                FirstCompletionAt = _firstCompletion.HasValue ? ToDuration(_firstCompletion.Value - _start) : null,
                LastCompletionAt = _lastCompletion.HasValue ? ToDuration(_lastCompletion.Value - _start) : null,
                Latency = BuildLatency(),
                Errors = BuildErrors()
            };
        }

        private LatencyStatistics BuildLatency()
        {
            long count = _success + _failed;
            if (count == 0)
                return new LatencyStatistics();

            return new LatencyStatistics
            {
                Mean = ToDuration(_sumLatencyTicks / count),
                MeanNanoseconds = (_sumLatencyTicks / count) * (1_000_000_000.0 / Stopwatch.Frequency),
                P50 = ToDuration(_histogram.EstimatePercentile(0.5)),
                P90 = ToDuration(_histogram.EstimatePercentile(0.90)),
                P95 = ToDuration(_histogram.EstimatePercentile(0.95)),
                P99 = ToDuration(_histogram.EstimatePercentile(0.99)),
                P999 = ToDuration(_histogram.EstimatePercentile(0.999)),
                Top5Min = _fastest.Select(ToSample).ToArray(),
                Top5Max = _slowest.Select(ToSample).ToArray()
            };
        }

        private ErrorStatistics[] BuildErrors() => _errors
            .OrderByDescending(entry => entry.Value.Count)
            .Select(entry => new ErrorStatistics
            {
                ExceptionType = entry.Key,
                Count = entry.Value.Count,
                FirstOccurrence = ToDuration(entry.Value.FirstTimestamp - _start),
                LastOccurrence = ToDuration(entry.Value.LastTimestamp - _start)
            }).ToArray();

        private LatencySample ToSample(SampleEntry sample) => new()
        {
            ScenarioName = sample.ScenarioName,
            StartedAt = ToDuration(sample.StartTimestamp - _start),
            Duration = ToDuration(sample.ElapsedTicks),
            IsSuccess = sample.IsSuccess
        };

        private static TimeSpan ToDuration(double stopwatchTicks) =>
            TimeSpan.FromSeconds(stopwatchTicks / Stopwatch.Frequency);

        private void InsertFastest(SampleEntry sample)
        {
            if (_fastest.Count == 5 && sample.ElapsedTicks >= _fastest[^1].ElapsedTicks)
                return;

            int index = 0;
            while (index < _fastest.Count && _fastest[index].ElapsedTicks <= sample.ElapsedTicks)
                ++index;
            _fastest.Insert(index, sample);
            if (_fastest.Count > 5) _fastest.RemoveAt(5);
        }

        private void InsertSlowest(SampleEntry sample)
        {
            if (_slowest.Count == 5 && sample.ElapsedTicks <= _slowest[^1].ElapsedTicks)
                return;

            int index = 0;
            while (index < _slowest.Count && _slowest[index].ElapsedTicks >= sample.ElapsedTicks)
                ++index;
            _slowest.Insert(index, sample);
            if (_slowest.Count > 5) _slowest.RemoveAt(5);
        }
    }
}
