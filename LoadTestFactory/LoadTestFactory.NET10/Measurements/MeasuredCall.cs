using System.Collections.Concurrent;

namespace LoadTestFactory.Measurements;

public static class MeasuredCall
{
    readonly static ConcurrentDictionary<string, int> IdResolver = new();
    static int IdCounter = 1024;

    public static async Task AsyncRun(string name, Func<Task> testCallHandler)
    {
        int scenarioId = IdResolver.GetOrAdd(
            name, static _ => Interlocked.Increment(ref IdCounter));

        MetricsCollector collector = MetricsCollector.Current.Value;
        if (collector == null)
        {
            await testCallHandler();
            return;
        }

        long start = Stopwatch.GetTimestamp();

        await testCallHandler();

        long completionTimestamp = Stopwatch.GetTimestamp();
        long elapsedTicks = completionTimestamp - start;
        Measurement newMeasurement = new(scenarioId, start, elapsedTicks);

        collector.AddMeasurement(ref newMeasurement);
    }

    public static async Task<T> AsyncRun<T>(string name, Func<Task<T>> testCallHandler)
    {
        MetricsCollector collector = MetricsCollector.Current.Value;
        return await testCallHandler();
    }
}
