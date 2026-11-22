using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LoadTestFactory.Measurements;

unsafe internal sealed class MetricsCollector : IDisposable
{
    public List<string> ResultTypes = new List<string>(["Success"]);

    internal const int Capacity = 1_000_000;
    internal static long InitialBufferBytes => 2L * Capacity * sizeof(Measurement);
    Measurement* _measurements = null;
    Measurement* _measurements_reserved = null;
    volatile int _MeasurementSegmentCounter = 0;
    int _index = 0;

    // One writer per collector; the monitoring timer only reads these counters.
    // Do not derive progress from _MeasurementSegmentCounter, which is updated
    // after asynchronous disk serialization.
    private long _completedExecutions;
    private long _failedExecutions;
    private long _bufferBlockingCount;
    private long _bufferBlockingTicks;
    private long _maxBufferBlockingTicks;
    readonly int _collectorNumber;
    private Task _AllocNewMemory = Task.CompletedTask;
    private bool _completed;
    private long _completedMeasurementCount;
    private int _disposed;
    private string _pathToFolder;

    public MetricsCollector(int collectorNumber, string pathToFolder)
    {
        _collectorNumber = collectorNumber;
        _pathToFolder = pathToFolder;

        _measurements = (Measurement*)NativeMemory.AllocZeroed(
            (nuint)Capacity,
            (nuint)sizeof(Measurement));

        _measurements_reserved = (Measurement*)NativeMemory.AllocZeroed(
            (nuint)Capacity,
            (nuint)sizeof(Measurement));
    }

    public void AddMeasurement(ref Measurement measurement)
    {
        if (_completed)
            throw new InvalidOperationException("Cannot add measurements after collector completion.");
        if (_index == Capacity)
        {
            HandleMemoryDepleting();
        }

        _measurements[_index++] = measurement;
    }

    public void AddMeasurementWithError(ref Measurement measurement, string error)
    {
        int errorCode = 0;
        for (int i = 1; i < ResultTypes.Count; ++i)
        {
            if (ResultTypes[i] == error)
            {
                errorCode = i;
                break;
            }
        }

        if (errorCode == 0)
        {
            ResultTypes.Add(error);
            errorCode = ResultTypes.Count - 1;
        }

        measurement.ResultType = errorCode;

        AddMeasurement(ref measurement);
    }

    private void HandleMemoryDepleting()
    {
        // Measure only waits for a background spill that has not finished yet.
        long startAt = 0;
        if (!_AllocNewMemory.IsCompleted)
            startAt = Stopwatch.GetTimestamp();

        _AllocNewMemory.Wait();

        // Switch immediately to the prepared spare buffer.
        Measurement* oldAddress = _measurements;
        _measurements = _measurements_reserved;
        _measurements_reserved = null;
        _index = 0;

        if (startAt != 0)
        {
            // An engine-side wait is not a scenario failure or latency sample.
            long elapsedTicks = Stopwatch.GetTimestamp() - startAt;
            ++_bufferBlockingCount;
            _bufferBlockingTicks += elapsedTicks;
            _maxBufferBlockingTicks = Math.Max(_maxBufferBlockingTicks, elapsedTicks);
        }

        _AllocNewMemory = Task.Factory.StartNew(() =>
        {
            string backupFileName = Path.Combine(
                                        _pathToFolder,
                                        $"load_test_{_collectorNumber}_{_MeasurementSegmentCounter}.bin");


            ReadOnlySpan<byte> bytes = new(oldAddress, checked(Capacity * sizeof(Measurement)));

            try
            {
                using var handle = File.OpenHandle(
                    backupFileName, FileMode.Create, FileAccess.Write, FileShare.None);
                RandomAccess.Write(handle, bytes, 0);
            }
            finally
            {
                // The old unmanaged buffer must be released even if disk I/O fails.
                NativeMemory.Free(oldAddress);
            }

            _measurements_reserved = (Measurement*)NativeMemory.AllocZeroed(
                                                                    (nuint)Capacity,
                                                                    (nuint)sizeof(Measurement));

            ++_MeasurementSegmentCounter;

        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void RecordScenarioCompletion(bool failed, bool withinMeasurementWindow)
    {
        if (!withinMeasurementWindow)
            return;

        Volatile.Write(ref _completedExecutions, _completedExecutions + 1);

        if (failed)
            Volatile.Write(ref _failedExecutions, _failedExecutions + 1);
    }

    public long GetCompletedExecutions() => Volatile.Read(ref _completedExecutions);

    public long GetFailedExecutions() => Volatile.Read(ref _failedExecutions);

    // Read after workers have completed; these values describe the generator,
    // not failed executions of the test scenarios.
    internal long BufferBlockingCount => _bufferBlockingCount;
    internal long BufferBlockingTicks => _bufferBlockingTicks;
    internal long MaxBufferBlockingTicks => _maxBufferBlockingTicks;

    public long GetTotalMetrics()
    {
        return _completed
            ? _completedMeasurementCount
            : _index + (long)Capacity * _MeasurementSegmentCounter;
    }

    // Explicit business operation: preserve the last partially-filled buffer.
    // Called after all workers stop, and before MetricsProcessor / Dispose.
    internal void Complete()
    {
        if (_completed)
            return;

        // Previous full segment must be fully written before the final segment
        // becomes visible to the processing stage.
        _AllocNewMemory.GetAwaiter().GetResult();

        _completedMeasurementCount = (long)Capacity * _MeasurementSegmentCounter + _index;

        if (_index > 0)
        {
            string fileName = Path.Combine(_pathToFolder,
                $"load_test_{_collectorNumber}_{_MeasurementSegmentCounter}.bin");

            using (var handle = File.OpenHandle(
                fileName, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                ReadOnlySpan<byte> bytes = new(_measurements,
                    checked(_index * sizeof(Measurement)));
                RandomAccess.Write(handle, bytes, 0);
            }

            ++_MeasurementSegmentCounter;
            _index = 0;
        }

        _completed = true;
    }

    internal string[] GetCompletedSegmentFiles()
    {
        if (!_completed)
            throw new InvalidOperationException("Complete the collector before reading segments.");

        string[] files = new string[_MeasurementSegmentCounter];
        for (int i = 0; i < files.Length; ++i)
            files[i] = Path.Combine(_pathToFolder, $"load_test_{_collectorNumber}_{i}.bin");

        return files;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            Destructor();
        GC.SuppressFinalize(this);
    }

    ~MetricsCollector()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            Destructor();
    }

    private void Destructor()
    {
        // A background spill still uses its old unmanaged buffer and can write
        // _measurements_reserved. It must finish before either pointer is freed.
        SafeExecutor.Run(() => _AllocNewMemory.Wait());

        if (_measurements != null)
        {
            SafeExecutor.Run(() => NativeMemory.Free(_measurements));
            _measurements = null;
        }

        if (_measurements_reserved != null)
        {
            SafeExecutor.Run(() => NativeMemory.Free(_measurements_reserved));
            _measurements_reserved = null;
        }

        // Task.CompletedTask may be shared by the runtime; do not dispose it.
        if (!ReferenceEquals(_AllocNewMemory, Task.CompletedTask))
            _AllocNewMemory.Dispose();
    }
}