namespace LoadTestFactory.Utilities;

internal static class SafeExecutor
{
    internal static void Run(Action action)
    {
        try
        {
            action();
        }
        catch
        {
        }
    }
}
