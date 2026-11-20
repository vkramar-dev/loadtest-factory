namespace LoadTestFactory.Results;

/// <summary>A diagnostic warning about the load generator, not a failed test scenario.</summary>
public sealed record LoadTestWarning(string Code, string Message)
{
    // Keep a machine-friendly identifier in Code while using a readable console label.
    public string DisplayCode => Code == "DISK_WRITE_BOTTLENECK" ? "DISK WRITE BOTTLENECK" : Code;
}
