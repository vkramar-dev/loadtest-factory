namespace LoadTestFactory.Fragmentation;

class Item<T>
{
    public Item(TestScenario<T> scenario)
    {
        Scenario = scenario;
        OriginalCount = scenario.Weight;
        LeftCount = scenario.Weight;
        Priority = 1M;
    }

    public TestScenario<T> Scenario;

    public int OriginalCount;

    public int LeftCount;

    public decimal Priority;

    public TestScenario<T> TakeOne()
    {
        Priority = --LeftCount / (decimal)OriginalCount;
        return Scenario;
    }
}
