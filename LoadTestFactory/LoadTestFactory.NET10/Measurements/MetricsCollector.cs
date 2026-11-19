using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LoadTestFactory.Measurements;

unsafe internal sealed class MetricsCollector : IDisposable
{
    List<string> ResultTypes = new List<string>(["Success"]);
    const int Capacity = 1_000_000;
    Measurement* _measurements = null;
    Measurement* _measurements_reserved = null;
    volatile int _MeasurementSegmentCounter = 0;
    int _index = 0;
    readonly int _collectorNumber;
    Task _AllocNewMemory = Task.CompletedTask;

    public MetricsCollector(int collectorNumber)
    {
        _collectorNumber = collectorNumber;

        _measurements = (Measurement*)NativeMemory.AllocZeroed(
            (nuint)Capacity,
            (nuint)sizeof(Measurement));

        _measurements_reserved = (Measurement*)NativeMemory.AllocZeroed(
            (nuint)Capacity,
            (nuint)sizeof(Measurement));
    }

    public void AddMeasurement(ref Measurement measurement)
    {
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
        long startAt = 0; // TODO: comment
        if (!_AllocNewMemory.IsCompleted)
            startAt = Stopwatch.GetTimestamp();

        _AllocNewMemory.Wait();

        // TODO: comment
        Measurement* oldAddress = _measurements;
        _measurements = _measurements_reserved;
        _measurements_reserved = null;
        _index = 0;

        if (startAt != 0)
        {
            // TODO: comment
            Measurement blockingMeasurement = new(0, startAt, Stopwatch.GetTimestamp() - startAt);
            AddMeasurementWithError(ref blockingMeasurement, "Performance Blocking");
            Console.WriteLine("TEMP: BLOCK!");
        }

        _AllocNewMemory = Task.Run(() =>
        {
            string backupFileName = Path.Combine(
                                        LoadTestRunner.PathToFolder,
                                        $"load_test_{_collectorNumber}_{_MeasurementSegmentCounter}.bin");

            Console.WriteLine($"TEMP: MetricsCollector #{_collectorNumber}, File: {backupFileName}");

            ReadOnlySpan<byte> bytes = new(oldAddress, checked(Capacity * sizeof(Measurement)));

            using var handle = File.OpenHandle(
                backupFileName, FileMode.Create, FileAccess.Write, FileShare.None);

            RandomAccess.Write(handle, bytes, 0);

            NativeMemory.Free(oldAddress);

            _measurements_reserved = (Measurement*)NativeMemory.AllocZeroed(
                                                                    (nuint)Capacity,
                                                                    (nuint)sizeof(Measurement));

            ++_MeasurementSegmentCounter;
        });
    }

    public long GetTotalMetrics()
    {
        return _index + Capacity * _MeasurementSegmentCounter;
    }

    public void Dispose()
    {
        Destructor();
        GC.SuppressFinalize(this);
    }

    ~MetricsCollector()
    {
        Destructor();
    }

    private void Destructor()
    {
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

        _AllocNewMemory.Wait();
        _AllocNewMemory.Dispose();
    }
}