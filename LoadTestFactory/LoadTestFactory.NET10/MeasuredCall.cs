using System.Collections.Concurrent;

namespace LoadTestFactory;

public static class MeasuredCall
{
    readonly static ConcurrentDictionary<string, int> IdResolver = new();
    internal static KeyValuePair<string, int>[] GetRegisteredCalls() => IdResolver.ToArray();

    public static async Task AsyncRun(string name, Func<Task> testCallHandler)
    {
        int callId = IdResolver.GetOrAdd(name, static _ => GlobalState.NextMeasuredCallId());

        MetricsCollector collector = MetricsCollector.Current.Value;
        if (collector == null)
        {
            await testCallHandler();
            return;
        }

        long start = Stopwatch.GetTimestamp();

        try
        {
            await testCallHandler();
        }
        catch (Exception ex)
        {
            long elapsed = Stopwatch.GetTimestamp() - start;
            Measurement error = new(callId, start, elapsed);
            collector.AddMeasurementWithError(ref error, ex.GetType().Name);

            throw;
        }

        long completionTimestamp = Stopwatch.GetTimestamp();
        long elapsedTicks = completionTimestamp - start;
        Measurement newMeasurement = new(callId, start, elapsedTicks);
        collector.AddMeasurement(ref newMeasurement);
    }

    public static async Task<T> AsyncRun<T>(string name, Func<Task<T>> testCallHandler)
    {
        int callId = IdResolver.GetOrAdd(name, static _ => GlobalState.NextMeasuredCallId());

        MetricsCollector collector = MetricsCollector.Current.Value;
        if (collector == null)
        {
            return await testCallHandler();
        }

        T result;
        long start = Stopwatch.GetTimestamp();
        
        try
        {
            result = await testCallHandler();
        }
        catch (Exception ex)
        {
            long elapsed = Stopwatch.GetTimestamp() - start;
            Measurement error = new(callId, start, elapsed);
            collector.AddMeasurementWithError(ref error, ex.GetType().Name);

            throw;
        }

        long completionTimestamp = Stopwatch.GetTimestamp();
        long elapsedTicks = completionTimestamp - start;
        Measurement newMeasurement = new(callId, start, elapsedTicks);

        collector.AddMeasurement(ref newMeasurement);

        return result;
    }
}
