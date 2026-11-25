namespace LoadTestFactory;

public sealed class TestScenario
{
    private static int Counter = 0;

    public TestScenario(string name, int weight, TestHandler methodHandler, bool measureScenario = true)
    {
        Name = name;
        Weight = weight;
        MethodHandler = methodHandler;
        Id = ++Counter;
        MeasureScenario = measureScenario;
    }

    public int Id { get; set; }

    public string Name { get; set; }

    public int Weight { get; set; }

    public TestHandler MethodHandler { get; set; }

    public bool MeasureScenario { get; set; }
}