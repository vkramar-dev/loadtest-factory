using System.Collections.Concurrent;

namespace LoadTestFactory;

public static class MeasuredCall
{
    internal static KeyValuePair<string, int>[] GetRegisteredCalls() => GlobalState.IdResolver.ToArray();

    public static async Task AsyncRun(string name, Func<Task> testCallHandler)
    {
        MetricsCollector collector = MetricsCollector.Current.Value;
        if (collector == null)
        {
            await testCallHandler();
            return;
        }

        int callId = GlobalState.IdResolver.GetOrAdd(name, static _ => GlobalState.NextId());

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
        int callId = GlobalState.IdResolver.GetOrAdd(name, static _ => GlobalState.NextId());

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
