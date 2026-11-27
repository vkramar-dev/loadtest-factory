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

    internal static async Task<MetricsProcessingResult> ProcessAsync<T>(
        MetricsCollector[] collectors,
        TestScenario<T>[] scenarios,
        long measurementStartTimestamp,
        long measurementEndTimestamp,
        int measurementSeconds,
        CancellationToken cancellationToken = default)
    {
        var measuredCalls = MeasuredCall.GetRegisteredCalls();
        Dictionary<int, ScenarioAccumulator> byId = new(scenarios.Length + measuredCalls.Length);
        ScenarioAccumulator[] measuredCallStats = new ScenarioAccumulator[measuredCalls.Length];
        ScenarioAccumulator[] scenarioStats = new ScenarioAccumulator[scenarios.Length];

        for (int i = 0; i < scenarios.Length; ++i)
        {
            TestScenario<T> scenario = scenarios[i];
            ScenarioAccumulator stats = new(scenario.Name, scenario.Weight, measurementSeconds,
                measurementStartTimestamp, scenario.MeasureScenario);
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
            if (scenario.MeasureExecutions)
            {
                overall.MergeFrom(scenario.Normal);
                overallTail.MergeFrom(scenario.Tail);
            }

            results[i] = new ScenarioResult
            {
                Name = scenario.Name,
                Weight = scenario.Weight,
                MeasureScenario = scenario.MeasureExecutions,
                Statistics = scenario.MeasureExecutions
                    ? scenario.Normal.BuildExecutionStatistics(measurementSeconds)
                    : new ExecutionStatistics { Errors = scenario.Normal.BuildErrors() },
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

    internal static async Task<LoadTestStepResult[]> ProcessSteppedAsync<T>(
        MetricsCollector[] collectors, TestScenario<T>[] scenarios,
        long measurementStart, long measurementEnd, int durationSeconds,
        int stepAmount, int parallelAmount, DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        int stepSeconds = durationSeconds / stepAmount;
        int stepWorkers = parallelAmount / stepAmount;
        var calls = MeasuredCall.GetRegisteredCalls();
        var definitions = scenarios.Select(x => (x.Id, x.Name, x.Weight, x.MeasureScenario)).ToArray();
        StepBucket[] steps = new StepBucket[stepAmount];
        for (int i = 0; i < steps.Length; ++i)
        {
            int offset = i * stepSeconds;
            int duration = i == steps.Length - 1 ? durationSeconds - offset : stepSeconds;
            int workers = i == steps.Length - 1 ? parallelAmount : (i + 1) * stepWorkers;
            steps[i] = new StepBucket(i + 1, workers, duration, offset,
                measurementStart, startedAtUtc, definitions, calls);
        }

        byte[][] buffers = [new byte[ChunkBytes], new byte[ChunkBytes]];
        foreach (MetricsCollector collector in collectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string path in collector.GetCompletedSegmentFiles())
                await ReadSegmentAsync(path, buffers, null, collector.ResultTypes,
                    measurementStart, measurementEnd, cancellationToken, steps, stepSeconds)
                    .ConfigureAwait(false);
        }

        LoadTestStepResult[] results = new LoadTestStepResult[stepAmount];
        for (int i = 0; i < steps.Length; ++i)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results[i] = steps[i].Build();
            steps[i] = null;
        }
        return results;
    }

    private sealed class StepBucket
    {
        private readonly int _number, _workers, _seconds;
        private readonly double _stabilizationSeconds, _measurementSeconds;
        private readonly long _stepStart;
        private readonly long _start, _end;
        private readonly DateTimeOffset _utc;
        private readonly Dictionary<int, ScenarioAccumulator> _byId = new();
        private readonly ScenarioAccumulator[] _scenarios, _calls;
        private readonly Dictionary<int, StatisticsAccumulator> _stabilizationErrors = new();
        private long _excludedScenarios, _excludedCalls;

        internal StepBucket(int number, int workers, int duration, int offset,
            long origin, DateTimeOffset utc,
            (int Id, string Name, int Weight, bool MeasureScenario)[] scenarios,
            KeyValuePair<string, int>[] calls)
        {
            _number = number;
            _workers = workers;
            _stepStart = origin + (long)offset * Stopwatch.Frequency;
            long stabilizationTicks = (long)duration * Stopwatch.Frequency / 5;
            _start = _stepStart + stabilizationTicks;
            _end = _stepStart + (long)duration * Stopwatch.Frequency;
            _stabilizationSeconds = stabilizationTicks / (double)Stopwatch.Frequency;
            _measurementSeconds = (_end - _start) / (double)Stopwatch.Frequency;
            _seconds = (int)((_end - _start + Stopwatch.Frequency - 1) / Stopwatch.Frequency);
            _utc = utc.AddSeconds(offset + _stabilizationSeconds);
            _scenarios = new ScenarioAccumulator[scenarios.Length];
            _calls = new ScenarioAccumulator[calls.Length];
            for (int i = 0; i < scenarios.Length; ++i)
            {
                var item = scenarios[i];
                var accumulator = new ScenarioAccumulator(item.Name, item.Weight,
                    _seconds, _start, item.MeasureScenario, sparseHistogram: true);
                _byId.Add(item.Id, accumulator);
                _scenarios[i] = accumulator;
            }
            for (int i = 0; i < calls.Length; ++i)
            {
                var item = calls[i];
                var accumulator = new ScenarioAccumulator(item.Key, 0, _seconds, _start, sparseHistogram: true, isMeasuredCall: true);
                _byId.Add(item.Value, accumulator);
                _calls[i] = accumulator;
            }
        }

        internal void Record(in Measurement m, long finishedAt, string errorType)
        {
            if (!_byId.TryGetValue(m.ScenarioId, out ScenarioAccumulator item))
                throw new InvalidDataException($"Unknown scenario ID {m.ScenarioId}.");

            if (m.StartAt < _start)
            {
                if (item.MeasureExecutions)
                {
                    if (item.IsMeasuredCall) ++_excludedCalls;
                    else ++_excludedScenarios;
                }
                // Excluded failures remain visible, without affecting stable statistics.
                if (errorType != null)
                {
                    if (!_stabilizationErrors.TryGetValue(m.ScenarioId, out var errors))
                    {
                        errors = new StatisticsAccumulator(0, _stepStart, false);
                        _stabilizationErrors.Add(m.ScenarioId, errors);
                    }
                    errors.RecordError(errorType, finishedAt);
                }
                return;
            }

            StatisticsAccumulator destination = finishedAt < _end ? item.Normal : item.Tail;
            if (item.MeasureExecutions)
                destination.Record(in m, finishedAt, item.Name, errorType);
            else if (errorType != null)
                destination.RecordError(errorType, finishedAt);
        }

        internal LoadTestStepResult Build()
        {
            StatisticsAccumulator overall = new(_seconds, _start, true, sparseHistogram: true);
            StatisticsAccumulator tail = new(0, _start, false, sparseHistogram: true);
            ScenarioResult[] scenarios = new ScenarioResult[_scenarios.Length];
            for (int i = 0; i < scenarios.Length; ++i)
            {
                var item = _scenarios[i];
                if (item.MeasureExecutions)
                {
                    overall.MergeFrom(item.Normal);
                    tail.MergeFrom(item.Tail);
                }
                scenarios[i] = new ScenarioResult
                {
                    Name = item.Name, Weight = item.Weight, MeasureScenario = item.MeasureExecutions,
                    Statistics = item.MeasureExecutions ? item.Normal.BuildExecutionStatistics(_seconds, _measurementSeconds)
                        : new ExecutionStatistics { Errors = item.Normal.BuildErrors() },
                    CompletionTail = item.Tail.BuildCompletionTail()
                };
            }
            MeasuredCallResult[] calls = _calls.Select(item => new MeasuredCallResult
            {
                Name = item.Name,
                Statistics = item.Normal.BuildExecutionStatistics(_seconds, _measurementSeconds),
                CompletionTail = item.Tail.BuildCompletionTail()
            }).ToArray();
            return new LoadTestStepResult
            {
                StepNumber = _number, ParallelAmount = _workers,
                StabilizationDuration = TimeSpan.FromSeconds(_stabilizationSeconds),
                MeasurementDuration = TimeSpan.FromSeconds(_measurementSeconds),
                MeasurementStartedAtUtc = _utc,
                OverallStatistics = overall.BuildExecutionStatistics(_seconds, _measurementSeconds),
                CompletionTail = tail.BuildCompletionTail(),
                Scenarios = scenarios, MeasuredCalls = calls,
                StabilizationScenarioCount = _excludedScenarios,
                StabilizationMeasuredCallCount = _excludedCalls,
                StabilizationErrors = _stabilizationErrors.SelectMany(pair =>
                    pair.Value.BuildErrors().Select(error => new LoadStepError
                    {
                        Name = _byId[pair.Key].Name,
                        IsMeasuredCall = _byId[pair.Key].IsMeasuredCall,
                        Error = error
                    })).ToArray()
            };
        }
    }

    private static async Task ReadSegmentAsync(
        string path,
        byte[][] buffers,
        Dictionary<int, ScenarioAccumulator> byId,
        IReadOnlyList<string> errorTypes,
        long start,
        long end,
        CancellationToken token,
        StepBucket[] steps = null, int stepSeconds = 0)
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
                errorTypes, start, end, steps, stepSeconds);

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
        long measurementEnd,
        StepBucket[] steps = null, int stepSeconds = 0)
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

            if (steps != null)
            {
                int index = (int)Math.Min(steps.Length - 1,
                    Math.Max(0, (measurement.StartAt - measurementStart) / Stopwatch.Frequency / stepSeconds));
                steps[index].Record(in measurement, finishedAt, errorType);
                continue;
            }

            if (!byId.TryGetValue(measurement.ScenarioId, out ScenarioAccumulator scenario))
                throw new InvalidDataException($"Unknown scenario ID {measurement.ScenarioId}.");

            StatisticsAccumulator destination = finishedAt < measurementEnd
                ? scenario.Normal : scenario.Tail;

            if (!scenario.MeasureExecutions)
            {
                if (errorType != null)
                    destination.RecordError(errorType, finishedAt);
                continue;
            }

            destination.Record(in measurement, finishedAt, scenario.Name, errorType);
        }
    }

    private sealed class ScenarioAccumulator
    {
        internal readonly string Name;
        internal readonly int Weight;
        internal readonly bool MeasureExecutions;
        internal readonly bool IsMeasuredCall;
        internal readonly StatisticsAccumulator Normal;
        internal readonly StatisticsAccumulator Tail;

        internal ScenarioAccumulator(string name, int weight, int seconds, long start, bool measureExecutions = true,
            bool sparseHistogram = false, bool isMeasuredCall = false)
        {
            Name = name;
            Weight = weight;
            MeasureExecutions = measureExecutions;
            IsMeasuredCall = isMeasuredCall;
            Normal = new StatisticsAccumulator(seconds, start, measureExecutions, sparseHistogram);
            Tail = new StatisticsAccumulator(0, start, false, sparseHistogram);
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
        private readonly LatencyHistogram _histogram;
        private readonly List<SampleEntry> _fastest = new(5);
        private readonly List<SampleEntry> _slowest = new(5);
        private readonly Dictionary<string, ErrorBucket> _errors = new(StringComparer.Ordinal);

        private long _success;
        private long _failed;
        private double _sumLatencyTicks;
        private long? _firstCompletion;
        private long? _lastCompletion;

        internal StatisticsAccumulator(int durationSeconds, long start, bool timeline, bool sparseHistogram = false)
        {
            _start = start;
            _histogram = new LatencyHistogram(sparseHistogram);
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
                RecordError(errorType, finishedAt);
        }

        internal void RecordError(string errorType, long finishedAt)
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

        internal ExecutionStatistics BuildExecutionStatistics(int measurementSeconds, double? actualMeasurementSeconds = null)
        {
            double duration = actualMeasurementSeconds ?? measurementSeconds;
            long total = _success + _failed;
            SecondStatistics[] timeline = new SecondStatistics[measurementSeconds];
            double min = double.PositiveInfinity;
            double max = 0;

            for (int i = 0; i < measurementSeconds; ++i)
            {
                SecondBucket bucket = _timeline[i];
                long count = bucket.SuccessCount + bucket.FailedCount;
                double rate = count / Math.Min(1.0, duration - i);
                min = Math.Min(min, rate);
                max = Math.Max(max, rate);
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
                    Mean = total / duration,
                    Min = double.IsPositiveInfinity(min) ? 0 : min,
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

        internal ErrorStatistics[] BuildErrors() => _errors
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
