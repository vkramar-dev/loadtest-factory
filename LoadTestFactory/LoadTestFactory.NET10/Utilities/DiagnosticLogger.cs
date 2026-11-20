namespace LoadTestFactory.Utilities;

// Best-effort diagnostics for internal errors; never hides the original failure.
internal static class DiagnosticLogger
{
    internal static void Write(Exception ex, string workingDirectory = null)
    {
        string fallbackDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LoadTest_Factory");

        foreach (string directory in new[] { workingDirectory, fallbackDirectory })
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            try
            {
                Directory.CreateDirectory(directory);

                string filename = $"log_{DateTime.UtcNow:yyyyMMdd_HHmmss_fffffff}_{Guid.NewGuid():N}.txt";
                string path = Path.Combine(directory, filename);
                string content = $"Time (UTC): {DateTime.UtcNow:O}{Environment.NewLine}"
                    + $"Process ID: {Environment.ProcessId}{Environment.NewLine}"
                    + $"{Environment.NewLine}{ex}";

                File.WriteAllText(path, content);
                return;
            }
            catch (Exception)
            {
                // Try the fallback directory if the requested directory is unavailable.
            }
        }
    }
}
