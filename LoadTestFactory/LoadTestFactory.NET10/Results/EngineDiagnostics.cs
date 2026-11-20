namespace LoadTestFactory.Results;

/// <summary>Diagnostics of the load generator, separate from tested scenario failures.</summary>
public sealed class EngineDiagnostics
{
    /// <summary>Times a collector had to wait for its next memory buffer.</summary>
    public long BufferBlockingCount { get; init; }
    public TimeSpan TotalBufferBlockingTime { get; init; }
    public TimeSpan MaxBufferBlockingTime { get; init; }
}
