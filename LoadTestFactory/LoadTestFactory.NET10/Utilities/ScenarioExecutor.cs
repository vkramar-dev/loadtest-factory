using System.Diagnostics;

namespace LoadTestFactory.Utilities;

internal static class ScenarioExecutor
{
    internal static async Task<Exception> ExecuteSafeAsync<T>(this TestScenario<T> scenario, T context)
    {
        try
        {
            await scenario.MethodHandler(context);
            return null;
        }
        catch(Exception ex)
        {
            return ex;
        }
    }

    internal static async Task ExecuteMeasuredAsync<T>(
        this TestScenario<T> scenario,
        T context,
        MetricsCollector collector,
        long measurementEndTimestamp)
    {
        if (!scenario.MeasureScenario)
        {
            Exception ex = await scenario.ExecuteSafeAsync(context);
            if (ex != null)
            {
                Measurement errorMeasure = new(scenario.Id, Stopwatch.GetTimestamp(), 0);
                collector.AddMeasurementWithError(ref errorMeasure, ex.GetType().Name);
            }
            return;
        }

        long start = Stopwatch.GetTimestamp();

        Exception error = await scenario.ExecuteSafeAsync(context);

        long completionTimestamp = Stopwatch.GetTimestamp();
        long elapsedTicks = completionTimestamp - start;

        Measurement newMeasurement = new(scenario.Id, start, elapsedTicks);

        if (error == null)
            collector.AddMeasurement(ref newMeasurement);
        else
            collector.AddMeasurementWithError(ref newMeasurement, error.GetType().Name);

        // Count finished user executions only, not internal Performance Blocking samples.
        collector.RecordScenarioCompletion(error != null, completionTimestamp < measurementEndTimestamp);
    }
}