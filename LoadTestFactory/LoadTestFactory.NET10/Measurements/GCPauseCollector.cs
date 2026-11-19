
using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace LoadTestFactory.Measurements;

public readonly record struct GCPause(long StartMs, long EndMs);

public sealed class GCPauseCollector : EventListener
{
    private readonly object _sync = new();
    private readonly List<(long Start, long End)> _pauses = new();

    private volatile EventSource? _runtime;
    private volatile bool _started;
    private long? _suspendStart;

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name != "Microsoft-Windows-DotNETRuntime")
            return;

        _runtime = source;

        if (_started)
            EnableGCEvents(source);
    }

    public void Start()
    {
        _started = true;

        if (_runtime is { } source)
            EnableGCEvents(source);
    }

    private void EnableGCEvents(EventSource source)
    {
        EnableEvents(
            source,
            EventLevel.Informational,
            (EventKeywords)0x1);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        switch (e.EventId)
        {
            case 9: // GCSuspendEEBegin_V1
                {
                    if (e.Payload?.Count < 2)
                        return;

                    int reason = Convert.ToInt32(e.Payload[1]);

                    lock (_sync)
                    {
                        // GC or GC preparation
                        _suspendStart = reason is 1 or 6
                            ? e.TimeStamp.Ticks
                            : null;
                    }
                    break;
                }

            case 3: // GCRestartEEEnd_V1
                {
                    lock (_sync)
                    {
                        if (_suspendStart is long start &&
                            e.TimeStamp.Ticks >= start)
                        {
                            _pauses.Add((start, e.TimeStamp.Ticks));
                        }

                        _suspendStart = null;
                    }
                    break;
                }
        }
    }

    public GCPause[] GetPauses(
        DateTime clockAnchorUtc,
        long clockAnchorTimestamp,
        long measurementStartTimestamp,
        long measurementEndTimestamp)
    {
        // Runtime GC events can be dispatched after the event happened.
        // Therefore use e.TimeStamp (saved as UTC ticks), not the time when
        // OnEventWritten is called. Convert both clocks using one calibration.
        double stopwatchTicksPerDateTimeTick =
            Stopwatch.Frequency / (double)TimeSpan.TicksPerSecond;
        long anchorUtcTicks = clockAnchorUtc.ToUniversalTime().Ticks;

        lock (_sync)
        {
            List<GCPause> result = new(_pauses.Count);
            foreach (var pause in _pauses)
            {
                double start = clockAnchorTimestamp
                    + (pause.Start - anchorUtcTicks) * stopwatchTicksPerDateTimeTick;
                double end = clockAnchorTimestamp
                    + (pause.End - anchorUtcTicks) * stopwatchTicksPerDateTimeTick;

                // Keep only the portion that overlaps the measurement phase.
                start = Math.Max(start, measurementStartTimestamp);
                end = Math.Min(end, measurementEndTimestamp);
                if (end <= start)
                    continue;

                result.Add(new GCPause(
                    (long)Math.Floor((start - measurementStartTimestamp)
                        * 1000.0 / Stopwatch.Frequency),
                    (long)Math.Ceiling((end - measurementStartTimestamp)
                        * 1000.0 / Stopwatch.Frequency)));
            }

            return result.ToArray();
        }
    }
}
