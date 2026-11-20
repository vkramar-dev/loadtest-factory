namespace LoadTestFactory.Notifications;

public enum LoadTestPhase
{
    Warmup,
    Measurement,
    Completed
}

public sealed class LoadTestProgressEventArgs : EventArgs
{
    public LoadTestPhase Phase { get; init; }
    public TimeSpan Elapsed { get; init; }
    public TimeSpan Duration { get; init; }
    public double Percentage { get; init; }

    // These counts include measurement-phase scenario executions only.
    public long Completed { get; init; }
    public long Failed { get; init; }

    // RPS for the interval since the previous measurement notification.
    public double Rps { get; init; }
}
