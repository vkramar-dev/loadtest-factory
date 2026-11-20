namespace LoadTestFactory.Results;

/// <summary>
/// Snapshot of the load generator's environment taken before the test starts.
/// This describes the machine running LoadTestFactory, not the tested service.
/// Values that cannot be obtained reliably are left null.
/// </summary>
public sealed class TestEnvironment
{
    public string OperatingSystem { get; init; }
    public string OSArchitecture { get; init; }
    public string ProcessArchitecture { get; init; }
    public string Runtime { get; init; }
    public bool IsServerGC { get; init; }
    public int LogicalProcessorCount { get; init; }
    public string CpuModel { get; init; }
    /// <summary>OS-reported CPU frequency, in MHz; not a performance benchmark.</summary>
    public double? CpuFrequencyMHz { get; init; }
    public long? TotalPhysicalMemoryBytes { get; init; }
    public long? AvailablePhysicalMemoryBytes { get; init; }
    public string StoragePath { get; init; }
    public string StorageFileSystem { get; init; }
    /// <summary>OS volume classification (Fixed, Network, etc.), not SSD/HDD speed.</summary>
    public string StorageVolumeType { get; init; }
    public long? AvailableStorageBytes { get; init; }
}
