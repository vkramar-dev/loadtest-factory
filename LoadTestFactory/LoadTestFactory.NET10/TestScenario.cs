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

    public int Id { get; init; }

    public string Name { get; init; }

    public int Weight { get; private set; }

    public TestHandler<T> MethodHandler { get; init; }

    public bool MeasureScenario { get; init; }

    public void ReduceWeight(int divisor)
    {
        Weight /= divisor;
    }
}

internal static class ScenarioIdGenerator
{
    private static int Counter;

    internal static int Next() => Interlocked.Increment(ref Counter);
}
