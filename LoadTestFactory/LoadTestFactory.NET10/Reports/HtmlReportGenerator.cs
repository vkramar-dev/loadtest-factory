using LoadTestFactory.Results;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LoadTestFactory.Reports;

/// <summary>
/// Generates a self-contained, offline HTML report from already calculated load-test results.
/// Does not read raw measurement files or alter the measured workload.
/// </summary>
public static class HtmlReportGenerator
{
    private const string ResourceName = "LoadTestFactory.ReportTemplate";
    private const string DataPlaceholder = "__LOAD_TEST_REPORT_DATA__";
    private const int MaximumChartPoints = 300;

    private static readonly Lazy<string> Template = new(LoadTemplate);

    /// <summary>Builds a self-contained HTML document without writing files.</summary>
    public static string Generate(LoadTestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // The default System.Text.Json encoder escapes '<', '>', '&' etc. This
        // protects the embedded JSON script element even for arbitrary scenario names.
        string json = JsonSerializer.Serialize(BuildReportData(result));
        return Template.Value.Replace(DataPlaceholder, json, StringComparison.Ordinal);
    }

    /// <summary>Saves a self-contained HTML file, returning its absolute path.</summary>
    public static string Save(LoadTestResult result, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        string path = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Generate(result), new UTF8Encoding(false));
        return path;
    }

    private static string LoadTemplate()
    {
        using Stream stream = typeof(HtmlReportGenerator).Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded report template: {ResourceName}");
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static object BuildReportData(LoadTestResult result)
    {
        ExecutionStatistics overall = result.OverallStatistics ?? new();
        TestEnvironment env = result.Environment;
        var scenarios = (result.Scenarios ?? []).Where(s => s != null).ToArray();
        var warnings = (result.Warnings ?? []).Where(w => w != null)
            .Select(w => new { code = w.DisplayCode, message = w.Message }).ToArray();
        GCPause[] pauses = (result.GcPauses ?? []).ToArray();
        EngineDiagnostics diagnostics = result.Engine ?? new();
        return new
        {
            generatedAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            startedAtUtc = result.MeasurementStartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            workers = result.ParallelAmount,
            warmupSeconds = result.WarmupDuration.TotalSeconds,
            durationSeconds = result.MeasurementDuration.TotalSeconds,
            elapsedSeconds = result.ActualOverallDuration.TotalSeconds,
            processingSeconds = result.MetricsProcessingDuration.TotalSeconds,
            overall = ToStatistics(overall),
            timeline = ToTimeline(overall.Timeline),
            scenarios = scenarios.Select(s => new
            {
                name = s.Name ?? "(unnamed)",
                weight = s.Weight,
                stats = ToStatistics(s.Statistics),
                timeline = ToTimeline(s.Statistics?.Timeline)
            }).ToArray(),
            errors = scenarios.SelectMany(s => (s.Statistics?.Errors ?? [])
                .Select(e => new
                {
                    scenario = s.Name ?? "(unnamed)",
                    type = e.ExceptionType ?? "Unknown",
                    count = e.Count,
                    firstSeconds = e.FirstOccurrence.TotalSeconds,
                    lastSeconds = e.LastOccurrence.TotalSeconds
                })).ToArray(),
            topFast = ToSamples(overall.Latency?.Top5Min),
            topSlow = ToSamples(overall.Latency?.Top5Max),
            excludedCompletions = result.CompletionTail?.TotalCount ?? 0,
            warnings,
            diagnostics = new
            {
                gcCount = pauses.Length,
                gcDurationMs = pauses.Sum(p => Math.Max(0L, p.EndMs - p.StartMs)),
                bufferWaits = diagnostics.BufferBlockingCount,
                totalWaitMs = diagnostics.TotalBufferBlockingTime.TotalMilliseconds,
                longestWaitMs = diagnostics.MaxBufferBlockingTime.TotalMilliseconds
            },
            environment = env == null ? null : new
            {
                os = env.OperatingSystem,
                osArchitecture = env.OSArchitecture,
                runtime = env.Runtime,
                processArchitecture = env.ProcessArchitecture,
                cpu = env.CpuModel,
                cpuFrequencyMHz = env.CpuFrequencyMHz,
                logicalProcessors = env.LogicalProcessorCount,
                gcMode = env.IsServerGC ? "Server" : "Workstation",
                totalRamBytes = env.TotalPhysicalMemoryBytes,
                availableRamBytes = env.AvailablePhysicalMemoryBytes,
                availableDiskBytes = env.AvailableStorageBytes,
                fileSystem = env.StorageFileSystem,
                volume = env.StorageVolumeType // StoragePath omitted (may contain local usernames).
            }
        };
    }

    private static object ToStatistics(ExecutionStatistics stats)
    {
        stats ??= new ExecutionStatistics();
        LatencyStatistics latency = stats.Latency ?? new();
        ThroughputStatistics throughput = stats.Throughput ?? new();
        return new
        {
            count = stats.TotalCount,
            success = stats.SuccessCount,
            failed = stats.FailedCount,
            failureRate = stats.FailureRate,
            meanRps = throughput.Mean,
            minRps = throughput.Min,
            peakRps = throughput.Peak,
            latency = new
            {
                meanNs = latency.MeanNanoseconds ?? ToNs(latency.Mean),
                p50Ns = ToNs(latency.P50),
                p90Ns = ToNs(latency.P90),
                p95Ns = ToNs(latency.P95),
                p99Ns = ToNs(latency.P99),
                p999Ns = ToNs(latency.P999)
            }
        };
    }

    private static double? ToNs(TimeSpan? latency) => latency?.Ticks * 100.0;

    private static object[] ToSamples(IReadOnlyList<LatencySample> samples) =>
        (samples ?? []).Where(x => x != null).Select(s => (object)new
        {
            scenario = s.ScenarioName ?? "(unnamed)",
            startedSeconds = s.StartedAt.TotalSeconds,
            durationNs = s.Duration.Ticks * 100.0,
            success = s.IsSuccess
        }).ToArray();

    /// <summary>
    /// Reduces long timelines to <=300 data points. Completed counts remain exact;
    /// bucket latency means are weighted by executions, not averaged from means.
    /// </summary>
    private static object[] ToTimeline(IReadOnlyList<SecondStatistics> timeline)
    {
        if (timeline == null || timeline.Count == 0)
            return [];

        int groupSize = Math.Max(1, (timeline.Count + MaximumChartPoints - 1) / MaximumChartPoints);
        List<object> output = new((timeline.Count + groupSize - 1) / groupSize);

        for (int index = 0; index < timeline.Count; index += groupSize)
        {
            int end = Math.Min(index + groupSize, timeline.Count);
            long completed = 0;
            long failures = 0;
            double totalLatencyTicks = 0;
            double? maxLatencyNs = null;

            for (int i = index; i < end; i++)
            {
                SecondStatistics second = timeline[i];
                completed += second.TotalCount;
                failures += second.FailedCount;
                totalLatencyTicks += second.TotalLatency.Ticks;
                if (second.MaxLatency is TimeSpan max)
                {
                    double ns = max.Ticks * 100.0;
                    maxLatencyNs = Math.Max(maxLatencyNs ?? ns, ns);
                }
            }

            output.Add(new
            {
                from = timeline[index].Second,
                to = timeline[end - 1].Second,
                rps = completed / (double)(end - index),
                failures,
                meanNs = completed > 0 ? (double?)(totalLatencyTicks * 100.0 / completed) : null,
                maxNs = maxLatencyNs
            });
        }
        return output.ToArray();
    }
}
