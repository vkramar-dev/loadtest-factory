using LoadTestFactory.Measurements;

namespace LoadTestFactory.Tests;

public class RunnerTests
{
    [Fact]
    public async Task Public_runner_reports_unmeasured_scenario_errors_and_awaited_call_failures()
    {
        const string callName = "runner.call";
        var scenario = new TestScenario("runner.scenario", 1, _ =>
            MeasuredCall.AsyncRun(callName, async () =>
            {
                await Task.Delay(5);
                throw new ApplicationException();
            }), measureScenario: false);
        DirectoryInfo directory = Directory.CreateTempSubdirectory("lte-runner-");
        try
        {
            var report = await LoadTestRunner.RunAsync(1, 0, 1, [scenario],
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
}
