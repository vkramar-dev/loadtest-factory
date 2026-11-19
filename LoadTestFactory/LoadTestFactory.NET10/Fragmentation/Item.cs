namespace LoadTestFactory.Fragmentation;

class Item
{
    public Item(TestScenario scenario)
    {
        Scenario = scenario;
        OriginalCount = scenario.Weight;
        LeftCount = scenario.Weight;
        Priority = 1M;
    }

    public TestScenario Scenario;

    public int OriginalCount;

    public int LeftCount;

    public decimal Priority;

    public TestScenario TakeOne()
    {
        Priority = --LeftCount / (decimal)OriginalCount;
        return Scenario;
    }
}
