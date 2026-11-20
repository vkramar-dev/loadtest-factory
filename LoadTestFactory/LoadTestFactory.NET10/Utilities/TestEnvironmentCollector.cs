using Microsoft.Win32;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;

namespace LoadTestFactory.Utilities;

/// <summary>Best-effort, lightweight environment inspection; no disk benchmarking.</summary>
internal static class TestEnvironmentCollector
{
    private const long GiB = 1024L * 1024 * 1024;

    internal static TestEnvironment Capture(string storageDirectory)
    {
        (long? totalMemory, long? availableMemory) = ReadMemory();
        (string cpuModel, double? cpuFrequencyMHz) = ReadCpu();

        long? diskFree = null;
        string diskFileSystem = null;
        string diskVolumeType = null;
        string fullPath = Path.GetFullPath(storageDirectory);

        try
        {
            // The actual measurement directory is used, not the process drive.
            // Prefer the longest matching mount point: on Unix the measurement
            // directory may be on a different filesystem than the root (/).
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            DriveInfo drive = DriveInfo.GetDrives()
                .Where(drive => fullPath.Equals(drive.Name.TrimEnd(Path.DirectorySeparatorChar), comparison)
                    || fullPath.StartsWith(Path.EndsInDirectorySeparator(drive.Name)
                        ? drive.Name : drive.Name + Path.DirectorySeparatorChar, comparison))
                .OrderByDescending(drive => drive.Name.Length)
                .FirstOrDefault() ?? new DriveInfo(Path.GetPathRoot(fullPath)!);
            if (drive.IsReady)
            {
                diskFree = drive.AvailableFreeSpace;
                diskFileSystem = drive.DriveFormat;
                diskVolumeType = drive.DriveType.ToString();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unknown capacity is represented as null, never as zero.
        }

        return new TestEnvironment
        {
            OperatingSystem = RuntimeInformation.OSDescription.Trim(),
            OSArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = RuntimeInformation.FrameworkDescription,
            IsServerGC = GCSettings.IsServerGC,
            LogicalProcessorCount = Environment.ProcessorCount,
            CpuModel = cpuModel,
            CpuFrequencyMHz = cpuFrequencyMHz,
            TotalPhysicalMemoryBytes = totalMemory,
            AvailablePhysicalMemoryBytes = availableMemory,
            StoragePath = fullPath,
            StorageFileSystem = diskFileSystem,
            StorageVolumeType = diskVolumeType,
            AvailableStorageBytes = diskFree
        };
    }

    internal static LoadTestWarning[] GetStartupWarnings(TestEnvironment environment, long requiredBufferBytes)
    {
        List<LoadTestWarning> warnings = new();

        if (environment.AvailablePhysicalMemoryBytes is long available)
        {
            if (requiredBufferBytes >= available)
            {
                warnings.Add(new LoadTestWarning("MEMORY_INSUFFICIENT",
                    $"Initial measurement buffers require {FormatBytes(requiredBufferBytes)}; " +
                    $"only {FormatBytes(available)} RAM is available."));
            }
            else if (requiredBufferBytes > available / 2 || available - requiredBufferBytes < GiB)
            {
                warnings.Add(new LoadTestWarning("MEMORY_LOW",
                    $"Measurement buffers need {FormatBytes(requiredBufferBytes)} of " +
                    $"{FormatBytes(available)} available RAM; other process memory is not included."));
            }
        }

        if (environment.StorageVolumeType == "Network")
        {
            warnings.Add(new LoadTestWarning("NETWORK_STORAGE",
                "Measurement files are stored on a network volume; I/O latency may affect load generation."));
        }

        if (environment.AvailableStorageBytes is long free)
        {
            if (free < GiB)
            {
                warnings.Add(new LoadTestWarning("DISK_CRITICAL",
                    $"Measurement storage has only {FormatBytes(free)} free. The test may run out of disk space."));
            }
            else if (free < 5 * GiB)
            {
                warnings.Add(new LoadTestWarning("DISK_LOW",
                    $"Measurement storage has only {FormatBytes(free)} free. Large tests may exhaust it."));
            }
        }

        return warnings.ToArray();
    }

    internal static string FormatBytes(long value) => value >= GiB
        ? $"{value / (double)GiB:N2} GiB"
        : value >= 1024 * 1024
            ? $"{value / (1024d * 1024):N0} MiB"
            : $"{value / 1024d:N0} KiB";

    private static (long? total, long? available) ReadMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            MemoryStatus status = new() { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref status))
                return ((long)Math.Min(status.TotalPhysical, (ulong)long.MaxValue),
                    (long)Math.Min(status.AvailablePhysical, (ulong)long.MaxValue));
        }

        if (OperatingSystem.IsLinux())
        {
            long? total = null;
            long? available = null;
            try
            {
                foreach (string line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                        total = ParseMemInfoBytes(line);
                    else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                        available = ParseMemInfoBytes(line);
                    if (total.HasValue && available.HasValue)
                        break;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            // Prefer the effective cgroup memory limit when running in a container.
            try
            {
                string maxText = File.ReadAllText("/sys/fs/cgroup/memory.max").Trim();
                if (long.TryParse(maxText, CultureInfo.InvariantCulture, out long limit) && limit > 0)
                {
                    long usage = long.Parse(File.ReadAllText("/sys/fs/cgroup/memory.current").Trim(),
                        CultureInfo.InvariantCulture);
                    long remaining = Math.Max(0, limit - usage);
                    total = Math.Min(total ?? long.MaxValue, limit);
                    available = Math.Min(available ?? long.MaxValue, remaining);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (FormatException) { }
            catch (OverflowException) { }

            return (total, available);
        }

        return (null, null);
    }

    private static long? ParseMemInfoBytes(string line)
    {
        ReadOnlySpan<char> value = line.AsSpan((line.IndexOf(':') + 1)..).Trim();
        int index = value.IndexOf(' ');
        if (index < 0)
            return null;

        if (long.TryParse(value[..index], CultureInfo.InvariantCulture, out long kilobytes))
            return kilobytes * 1024;
        return null;
    }

    private static (string model, double? mhz) ReadCpu()
    {
        if (OperatingSystem.IsWindows())
        {
            const string cpuKey = @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0";
            try
            {
                string model = Registry.GetValue(cpuKey, "ProcessorNameString", null) as string;
                object clock = Registry.GetValue(cpuKey, "~MHz", null);
                double? mhz = clock is int value && value > 0 ? value : null;
                return (model?.Trim(), mhz);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return (null, null);
            }
        }

        if (OperatingSystem.IsLinux())
        {
            string model = null;
            double? mhz = null;
            try
            {
                foreach (string line in File.ReadLines("/proc/cpuinfo"))
                {
                    int colon = line.IndexOf(':');
                    if (colon < 0) continue;
                    string key = line[..colon].Trim();
                    string value = line[(colon + 1)..].Trim();
                    if (key == "model name" || (key == "Hardware" && model == null))
                        model = value;
                    else if (key == "cpu MHz" &&
                        double.TryParse(value, CultureInfo.InvariantCulture, out double frequency) && frequency > 0)
                        mhz = frequency;

                    if (model != null && mhz.HasValue)
                        break;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return (model, mhz);
        }

        // A missing model/frequency is preferable to an unverified guess.
        return (null, null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
