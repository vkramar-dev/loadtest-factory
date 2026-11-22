using System.Diagnostics;

namespace LoadTestFactory.Utilities;

internal static class ScenarioExecutor
{
    internal static async Task<Exception> ExecuteSafeAsync(this TestScenario scenario)
    {
        try
        {
            await scenario.MethodHandler(scenario.Name);
            return null;
        }
        catch(Exception ex)
        {
            return ex;
        }
    }

    internal static async Task ExecuteMeasuredAsync(
        this TestScenario scenario,
        MetricsCollector collector,
        long measurementEndTimestamp)
    {
        long start = Stopwatch.GetTimestamp();

        Exception error = await scenario.ExecuteSafeAsync();

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