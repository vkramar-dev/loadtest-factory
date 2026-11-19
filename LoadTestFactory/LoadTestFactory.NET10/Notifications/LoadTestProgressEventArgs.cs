namespace LoadTestFactory.Notifications;

public sealed class LoadTestProgressEventArgs : EventArgs
{
    public TimeSpan Elapsed { get; init; }
    public TimeSpan Duration { get; init; }
    public double Percentage { get; init; }

    public long Completed { get; init; }
    public long Failed { get; init; }
    public double Rps { get; init; }
}
