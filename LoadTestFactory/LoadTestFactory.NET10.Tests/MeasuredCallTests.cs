using LoadTestFactory.Measurements;

namespace LoadTestFactory.Tests;

public class MeasuredCallTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Success_runs_handler_once_and_preserves_result(bool measure, bool generic)
    {
        using var run = new TestRun();
        if (!measure)
            MetricsCollector.Current.Value = null;

        int calls = 0;
        object expected = new();
        if (generic)
        {
            var actual = await MeasuredCall.AsyncRun("success", () =>
            {
                calls++;
                return Task.FromResult(expected);
            });
            Assert.Same(expected, actual);
        }
        else
        {
            await MeasuredCall.AsyncRun("success", () =>
            {
                calls++;
                return Task.CompletedTask;
            });
        }

        Assert.Equal(1, calls);
        Assert.Equal(measure ? 1L : 0L, run.Collector.GetTotalMetrics());
        if (measure)
        {
            var result = await run.ReadAsync();
            var stats = Assert.Single(result.MeasuredCalls, c => c.Name == "success").Statistics;
            Assert.Equal(1L, stats.SuccessCount);
            Assert.Equal(0L, stats.FailedCount);
        }
    }

    public static TheoryData<bool, bool, bool> FailureCases
    {
        get
        {
            var cases = new TheoryData<bool, bool, bool>();
            foreach (bool measure in new[] { false, true })
                foreach (bool generic in new[] { false, true })
                    foreach (bool asyncFailure in new[] { false, true })
                        cases.Add(measure, generic, asyncFailure);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(FailureCases))]
    public async Task Failure_propagates_original_exception_and_records_only_during_measurement(
        bool measure, bool generic, bool asyncFailure)
    {
        using var run = new TestRun();
        if (!measure)
            MetricsCollector.Current.Value = null;

        var expected = new ApplicationException();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        Task<int> Handler()
        {
            calls++;
            if (!asyncFailure)
                throw expected;
            return gate.Task;
        }

        Task call = generic
            ? MeasuredCall.AsyncRun<int>("failed", Handler)
            : MeasuredCall.AsyncRun("failed", (Func<Task>)Handler);
        if (asyncFailure)
        {
            Assert.False(call.IsCompleted);
            gate.SetException(expected);
        }

        Assert.Same(expected, await Assert.ThrowsAsync<ApplicationException>(() => call));
        Assert.Equal(1, calls);
        Assert.Equal(measure ? 1L : 0L, run.Collector.GetTotalMetrics());
        if (measure)
        {
            var result = await run.ReadAsync();
            var stats = Assert.Single(result.MeasuredCalls, c => c.Name == "failed").Statistics;
            Assert.Equal(1L, stats.TotalCount);
            Assert.Equal(1L, stats.FailedCount);
            Assert.Equal(nameof(ApplicationException), Assert.Single(stats.Errors).ExceptionType);
        }
    }
}
