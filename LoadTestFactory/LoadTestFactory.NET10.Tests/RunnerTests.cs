using LoadTestFactory.Measurements;

namespace LoadTestFactory.Tests;

public class RunnerTests
{
    [Fact]
    public async Task Public_runner_reports_unmeasured_scenario_errors_and_awaited_call_failures()
    {
        const string callName = "runner.call";
        var scenario = new TestScenario<int>("runner.scenario", 1, _ =>
            MeasuredCall.AsyncRun(callName, async () =>
            {
                await Task.Delay(5);
                throw new ApplicationException();
            }), measureScenario: false);
        DirectoryInfo directory = Directory.CreateTempSubdirectory("lte-runner-");
        try
        {
            var report = await LoadTestRunner.RunAsync(new[] { 0 }, 0, 1, [scenario],
                workingDirectory: directory.FullName, consoleOutput: false);
            var outer = Assert.Single(report.Scenarios);
            var inner = Assert.Single(report.MeasuredCalls, c => c.Name == callName);
            Assert.Equal(0L, report.OverallStatistics.TotalCount);
            Assert.Equal(0L, outer.Statistics.TotalCount);
            // The nested call and its parent may finish on opposite sides of the deadline.
            var errors = outer.Statistics.Errors.Concat(outer.CompletionTail.Errors).ToArray();
            Assert.NotEmpty(errors);
            Assert.All(errors, e => Assert.Equal(nameof(ApplicationException), e.ExceptionType));
            Assert.Equal(errors.Sum(e => e.Count), inner.Statistics.FailedCount + inner.CompletionTail.FailedCount);
            Assert.Equal(0L, inner.Statistics.SuccessCount + inner.CompletionTail.SuccessCount);
            Assert.Contains("ERRORS BY SCENARIO", report.ToString());
            Assert.Single(directory.GetFiles("*.html"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
    private sealed class WorkerContext
    {
        public int WarmupA;
        public int WarmupB;
        public int MeasurementA;
        public int MeasurementB;
    }

    [Fact]
    public async Task Each_worker_keeps_its_context_across_scenarios_and_phases()
    {
        WorkerContext[] contexts = [new(), new()];
        TestScenario<WorkerContext>[] scenarios =
        [
            new("context-a", 1, async context =>
            {
                if (MetricsCollector.Current.Value == null)
                    context.WarmupA++;
                else
                    context.MeasurementA++;
                await Task.Delay(10);
            }),
            new("context-b", 1, async context =>
            {
                if (MetricsCollector.Current.Value == null)
                    context.WarmupB++;
                else
                    context.MeasurementB++;
                await Task.Delay(10);
            })
        ];

        DirectoryInfo directory = Directory.CreateTempSubdirectory("lte-contexts-");
        try
        {
            var report = await LoadTestRunner.RunAsync(contexts, 1, 1, scenarios,
                workingDirectory: directory.FullName, consoleOutput: false);
            Assert.Equal(contexts.Length, report.ParallelAmount);
            Assert.All(contexts, context =>
            {
                Assert.True(context.WarmupA > 0);
                Assert.True(context.WarmupB > 0);
                Assert.True(context.MeasurementA > 0);
                Assert.True(context.MeasurementB > 0);
            });
            Assert.Equal(0L, report.OverallStatistics.FailedCount);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Single_handler_receives_value_type_contexts()
    {
        int[] contexts = [10, 20];
        var received = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();
        DirectoryInfo directory = Directory.CreateTempSubdirectory("lte-values-");
        try
        {
            var report = await LoadTestRunner.RunAsync<int>(contexts, 0, 1, async context =>
            {
                received.AddOrUpdate(context, 1, (_, count) => count + 1);
                await Task.Delay(10);
            }, workingDirectory: directory.FullName, consoleOutput: false);
            Assert.Equal(contexts, received.Keys.OrderBy(value => value).ToArray());
            Assert.Equal(contexts.Length, report.ParallelAmount);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Null_and_empty_context_arrays_are_rejected()
    {
        TestHandler<int> handler = _ => Task.CompletedTask;
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            LoadTestRunner.RunAsync<int>(null, 0, 1, handler, consoleOutput: false));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            LoadTestRunner.RunAsync(Array.Empty<int>(), 0, 1, handler, consoleOutput: false));
    }

    [Fact]
    public void Global_run_guard_and_scenario_ids_are_shared_between_context_types()
    {
        var first = new TestScenario<int>("int", 1, _ => Task.CompletedTask);
        var second = new TestScenario<string>("string", 1, _ => Task.CompletedTask);
        Assert.NotEqual(first.Id, second.Id);

        GlobalState.CaptureState();
        try
        {
            Assert.Throws<InvalidOperationException>(() => GlobalState.CaptureState());
        }
        finally
        {
            GlobalState.ReleaseState();
        }
    }
}
