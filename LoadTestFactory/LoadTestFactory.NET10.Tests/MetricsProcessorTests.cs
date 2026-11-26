using System.Diagnostics;
using System.Text.Json;
using LoadTestFactory.Measurements;
using LoadTestFactory.Reports;
using LoadTestFactory.Results;

namespace LoadTestFactory.Tests;

public class MetricsProcessorTests
{
    [Fact]
    public async Task Unmeasured_errors_are_grouped_by_time_window_and_excluded_from_performance()
    {
        using var run = new TestRun();
        var measured = new TestScenario("measured", 1, _ => Task.CompletedTask);
        var hidden = new TestScenario("errors-only", 1, _ => Task.CompletedTask, measureScenario: false);
        long second = Stopwatch.Frequency;
        var ok = new Measurement(measured.Id, run.Start, second / 2);
        run.Collector.AddMeasurement(ref ok);
        void FailAt(long offset)
        {
            var error = new Measurement(hidden.Id, run.Start + offset, 0);
            run.Collector.AddMeasurementWithError(ref error, nameof(InvalidCastException));
        }

        FailAt(second / 4);
        FailAt(3 * second / 4);
        FailAt(second); // The end itself belongs to the completion tail.
        FailAt(2 * second);

        var metrics = await run.ReadWindowAsync(1, measured, hidden);
        var overall = metrics.Overall;
        Assert.Equal(1L, overall.TotalCount);
        Assert.Equal(1L, overall.SuccessCount);
        Assert.Equal(0L, overall.FailedCount);
        Assert.Equal(1d, overall.Throughput.Mean);
        Assert.Equal(1L, Assert.Single(overall.Timeline).TotalCount);
        Assert.Equal(TimeSpan.FromMilliseconds(500), overall.Latency.Mean);
        Assert.Equal(measured.Name, Assert.Single(overall.Latency.Top5Min).ScenarioName);
        Assert.Equal(measured.Name, Assert.Single(overall.Latency.Top5Max).ScenarioName);
        Assert.Empty(overall.Errors);
        Assert.Equal(0L, metrics.CompletionTail.TotalCount);

        var scenario = Assert.Single(metrics.Scenarios, s => s.Name == hidden.Name);
        Assert.False(scenario.MeasureScenario);
        Assert.Equal(0L, scenario.Statistics.TotalCount);
        Assert.Empty(scenario.Statistics.Timeline);
        var error = Assert.Single(scenario.Statistics.Errors);
        Assert.Equal(nameof(InvalidCastException), error.ExceptionType);
        Assert.Equal(2L, error.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(250), error.FirstOccurrence);
        Assert.Equal(TimeSpan.FromMilliseconds(750), error.LastOccurrence);
        var tail = Assert.Single(scenario.CompletionTail.Errors);
        Assert.Equal(2L, tail.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), tail.FirstOccurrence);
        Assert.Equal(TimeSpan.FromSeconds(2), tail.LastOccurrence);

        var report = new LoadTestResult
        {
            OverallStatistics = overall, CompletionTail = metrics.CompletionTail,
            Scenarios = metrics.Scenarios, MeasurementDuration = TimeSpan.FromSeconds(1)
        };
        string console = report.ToString();
        int errorsHeading = console.IndexOf("ERRORS BY SCENARIO", StringComparison.Ordinal);
        Assert.True(errorsHeading >= 0);
        Assert.DoesNotContain(hidden.Name, console[..errorsHeading]);
        Assert.Contains("errors-only: InvalidCastException (2)", console[errorsHeading..]);
        Assert.Contains("errors-only (completion tail): InvalidCastException (2)", console[errorsHeading..]);

        string html = HtmlReportGenerator.Generate(report);
        const string marker = "<script type=\"application/json\" id=\"loadtest-data\">";
        int markerIndex = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0);
        int from = markerIndex + marker.Length;
        using var data = JsonDocument.Parse(html[from..html.IndexOf("</script>", from, StringComparison.Ordinal)]);
        var json = data.RootElement;
        Assert.Equal(measured.Name, Assert.Single(json.GetProperty("scenarios").EnumerateArray()).GetProperty("name").GetString());
        var rows = json.GetProperty("errors").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row =>
        {
            Assert.Equal(nameof(InvalidCastException), row.GetProperty("type").GetString());
            Assert.Equal(2L, row.GetProperty("count").GetInt64());
        });
        Assert.Contains(rows, r => r.GetProperty("scenario").GetString() == hidden.Name);
        Assert.Contains(rows, r => r.GetProperty("scenario").GetString() == hidden.Name + " (completion tail)");
    }

    [Fact]
    public async Task Zero_tick_latency_is_valid_for_a_measured_execution()
    {
        using var run = new TestRun();
        var scenario = new TestScenario("fast", 1, _ => Task.CompletedTask);
        var sample = new Measurement(scenario.Id, run.Start, 0);
        run.Collector.AddMeasurement(ref sample);
        var metrics = await run.ReadWindowAsync(1, scenario);
        Assert.Equal(1L, metrics.Overall.SuccessCount);
        Assert.Equal(TimeSpan.Zero, Assert.Single(metrics.Overall.Latency.Top5Min).Duration);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Corrupt_timestamp_and_unknown_id_still_fail_validation(bool invalidTime)
    {
        using var run = new TestRun();
        var scenario = new TestScenario("valid", 1, _ => Task.CompletedTask);
        var sample = new Measurement(invalidTime ? scenario.Id : int.MaxValue, invalidTime ? 0 : run.Start, 0);
        run.Collector.AddMeasurement(ref sample);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => run.ReadWindowAsync(1, scenario));
        Assert.Contains(invalidTime ? "before measurement phase" : "Unknown scenario ID", ex.Message);
    }
}
