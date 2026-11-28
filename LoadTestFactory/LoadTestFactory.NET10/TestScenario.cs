namespace LoadTestFactory;

public sealed class TestScenario<T>
{
    public TestScenario(string name, int weight, TestHandler<T> methodHandler, bool measureScenario = true)
    {
        Name = name;
        OriginalWeight = weight;
        Weight = weight;
        MethodHandler = methodHandler;
        Id = GlobalState.NextScenarioId();
        MeasureScenario = measureScenario;

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual<int>(weight, 0, nameof(weight));
        ArgumentOutOfRangeException.ThrowIfGreaterThan<int>(weight, 100, nameof(weight));
    }

    public int Id { get; init; }

    public string Name { get; init; }

    public readonly int OriginalWeight;

    public int Weight { get; private set; }

    public TestHandler<T> MethodHandler { get; init; }

    public bool MeasureScenario { get; init; }

    public void ReduceWeight(int divisor)
    {
        Weight /= divisor;
    }
}
