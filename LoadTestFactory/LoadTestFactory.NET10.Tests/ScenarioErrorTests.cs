using LoadTestFactory.Measurements;
using LoadTestFactory.Utilities;

namespace LoadTestFactory.Tests;

public class ScenarioErrorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_failure_before_inner_call_is_visible_only_as_scenario_error(bool measureScenario)
    {
        using var run = new TestRun();
        int callAttempts = 0;
        async Task Handler(string name)
        {
            await Task.FromException(new InvalidCastException());
            callAttempts++;
            await MeasuredCall.AsyncRun("never-reached", () => Task.CompletedTask);
        }

        var scenario = new TestScenario("direct", 1, Handler, measureScenario);
        await scenario.ExecuteMeasuredAsync(run.Collector, long.MaxValue);
        var result = await run.ReadAsync(scenario);
        Assert.Equal(0, callAttempts);
        Assert.All(result.MeasuredCalls, c => Assert.Equal(0L, c.Statistics.TotalCount));
        var error = Assert.Single(Assert.Single(result.Scenarios).Statistics.Errors);
        Assert.Equal(nameof(InvalidCastException), error.ExceptionType);
        Assert.Equal(1L, error.Count);
        Assert.Equal(measureScenario ? 1L : 0L, result.Overall.FailedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_scenario_records_inner_call_in_either_mode(bool measureScenario)
    {
        using var run = new TestRun();
        var scenario = new TestScenario("ok-outer", 1,
            _ => MeasuredCall.AsyncRun("ok-inner", () => Task.CompletedTask), measureScenario);
        await scenario.ExecuteMeasuredAsync(run.Collector, long.MaxValue);
        var result = await run.ReadAsync(scenario);
        long expected = measureScenario ? 1L : 0L;
        Assert.Equal(expected, result.Overall.TotalCount);
        Assert.Equal(expected, result.Overall.SuccessCount);
        Assert.Equal(expected, run.Collector.GetCompletedExecutions());
        Assert.Empty(Assert.Single(result.Scenarios).Statistics.Errors);
        Assert.Equal(1L, Assert.Single(result.MeasuredCalls, c => c.Name == "ok-inner").Statistics.SuccessCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Awaited_call_failure_is_attributed_to_both_call_and_scenario(bool measureScenario)
    {
        using var run = new TestRun();
        const string name = "same-name"; // Scenarios and calls have independent IDs.
        var scenario = new TestScenario(name, 1, async _ =>
        {
            await MeasuredCall.AsyncRun(name, () => throw new ApplicationException());
            throw new InvalidCastException(); // Must not run after the awaited failure.
        }, measureScenario);

        await scenario.ExecuteMeasuredAsync(run.Collector, long.MaxValue);
        var result = await run.ReadAsync(scenario);
        long measured = measureScenario ? 1L : 0L;
        Assert.Equal(measured, run.Collector.GetCompletedExecutions());
        Assert.Equal(measured, run.Collector.GetFailedExecutions());
        Assert.Equal(measured, result.Overall.TotalCount);
        Assert.Equal(measured, result.Overall.FailedCount);
        var stats = Assert.Single(result.Scenarios).Statistics;
        Assert.Equal(measured, stats.FailedCount);
        var error = Assert.Single(stats.Errors);
        Assert.Equal(nameof(ApplicationException), error.ExceptionType);
        Assert.Equal(1L, error.Count);

        var call = Assert.Single(result.MeasuredCalls, c => c.Name == name);
        Assert.Equal(1L, call.Statistics.FailedCount);
        Assert.Equal(nameof(ApplicationException), Assert.Single(call.Statistics.Errors).ExceptionType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unawaited_faulted_task_leaves_scenario_free_to_finish_or_throw(bool scenarioThrows)
    {
        using var run = new TestRun();
        Task inner = null;
        var scenario = new TestScenario("outer", 1, name =>
        {
            // The handler throws synchronously: collection finishes before AsyncRun returns.
            inner = MeasuredCall.AsyncRun("unawaited", () => throw new ApplicationException());
            if (scenarioThrows)
                throw new InvalidCastException();
            return Task.CompletedTask;
        }, measureScenario: false);

        await scenario.ExecuteMeasuredAsync(run.Collector, long.MaxValue);
        await Assert.ThrowsAsync<ApplicationException>(() => inner);
        var result = await run.ReadAsync(scenario);
        var errors = Assert.Single(result.Scenarios).Statistics.Errors;
        if (scenarioThrows)
            Assert.Equal(nameof(InvalidCastException), Assert.Single(errors).ExceptionType);
        else
            Assert.Empty(errors);

        Assert.Equal(0L, result.Overall.TotalCount);
        var call = Assert.Single(result.MeasuredCalls, c => c.Name == "unawaited");
        Assert.Equal(1L, call.Statistics.FailedCount);
        Assert.Equal(nameof(ApplicationException), Assert.Single(call.Statistics.Errors).ExceptionType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_call_then_direct_scenario_failure_stays_at_its_own_level(bool measureScenario)
    {
        using var run = new TestRun();
        var scenario = new TestScenario("outer", 1, async _ =>
        {
            await MeasuredCall.AsyncRun("ok", () => Task.CompletedTask);
            throw new InvalidCastException();
        }, measureScenario);

        await scenario.ExecuteMeasuredAsync(run.Collector, long.MaxValue);
        var result = await run.ReadAsync(scenario);
        var call = Assert.Single(result.MeasuredCalls, c => c.Name == "ok");
        Assert.Equal(1L, call.Statistics.SuccessCount);
        Assert.Empty(call.Statistics.Errors);
        var error = Assert.Single(Assert.Single(result.Scenarios).Statistics.Errors);
        Assert.Equal(nameof(InvalidCastException), error.ExceptionType);
        Assert.Equal(measureScenario ? 1L : 0L, result.Overall.FailedCount);
    }

    [Fact]
    public async Task Same_call_name_combines_success_and_failure_across_two_scenarios()
    {
        using var run = new TestRun();
        const string call = "shared-call";
        var measured = new TestScenario("successful", 1,
            _ => MeasuredCall.AsyncRun(call, () => Task.CompletedTask));
        var unmeasured = new TestScenario("failing", 1,
            _ => MeasuredCall.AsyncRun(call, () => throw new ApplicationException()), false);

        await measured.ExecuteMeasuredAsync(run.Collector, long.MaxValue);
        await unmeasured.ExecuteMeasuredAsync(run.Collector, long.MaxValue);
        var result = await run.ReadAsync(measured, unmeasured);
        var stats = Assert.Single(result.MeasuredCalls, c => c.Name == call).Statistics;
        Assert.Equal(2L, stats.TotalCount);
        Assert.Equal(1L, stats.FailedCount);
        Assert.Equal(1L, stats.SuccessCount);
        Assert.Equal(1L, result.Overall.TotalCount);
        Assert.Equal(0L, result.Overall.FailedCount);
    }

    [Fact]
    public async Task Scenario_can_handle_call_exception_and_finish_successfully()
    {
        using var run = new TestRun();
        var scenario = new TestScenario("recovered", 1, async _ =>
        {
            try
            {
                await MeasuredCall.AsyncRun("recoverable", () => throw new ApplicationException());
            }
            catch (ApplicationException) { }
        });

        await scenario.ExecuteMeasuredAsync(run.Collector, long.MaxValue);
        var result = await run.ReadAsync(scenario);
        Assert.Equal(1L, result.Overall.SuccessCount);
        Assert.Empty(result.Overall.Errors);
        Assert.Equal(1L, Assert.Single(result.MeasuredCalls, c => c.Name == "recoverable").Statistics.FailedCount);
    }
}
