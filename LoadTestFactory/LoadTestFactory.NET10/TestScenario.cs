namespace LoadTestFactory;

public sealed class TestScenario<T>
{
    public TestScenario(string name, int weight, TestHandler<T> methodHandler, bool measureScenario = true)
    {
        Name = name;
        Weight = weight;
        MethodHandler = methodHandler;
        Id = ScenarioIdGenerator.Next();
        MeasureScenario = measureScenario;
    }

    public int Id { get; set; }

    public string Name { get; set; }

    public int Weight { get; set; }

    public TestHandler<T> MethodHandler { get; set; }

    public bool MeasureScenario { get; set; }
}

internal static class ScenarioIdGenerator
{
    private static int Counter;

    internal static int Next() => Interlocked.Increment(ref Counter);
}
