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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch(Exception ex)
        {
            return ex;
        }
    }

    internal static async Task ExecuteMeasuredAsync(
        this TestScenario scenario,
        MetricsCollector collector)
    {
        long start = Stopwatch.GetTimestamp();

        Exception error = await scenario.ExecuteSafeAsync();

        long elapsedTicks = Stopwatch.GetTimestamp() - start;

        Measurement newMeasurement = new(scenario.Id, start, elapsedTicks);

        if (error == null)
            collector.AddMeasurement(ref newMeasurement);
        else
            collector.AddMeasurementWithError(ref newMeasurement, error.GetType().Name);
    }
}