namespace LoadTestFactory.Fragmentation;

public sealed class TestScenarioIterator(TestScenario[] scenarios)
{
    readonly private TestScenario[] _scenarios = scenarios;

    private int _index = Random.Shared.Next(scenarios.Length);

    public TestScenario Next()
    {
        if (_index == _scenarios.Length)
            _index = 0;

        return _scenarios[_index++];
    }
}
