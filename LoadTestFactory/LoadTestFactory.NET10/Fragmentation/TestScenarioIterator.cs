namespace LoadTestFactory.Fragmentation;

public sealed class TestScenarioIterator<T>(TestScenario<T>[] scenarios)
{
    readonly private TestScenario<T>[] _scenarios = scenarios;

    private int _index = Random.Shared.Next(scenarios.Length);

    public TestScenario<T> Next()
    {
        if (_index == _scenarios.Length)
            _index = 0;

        return _scenarios[_index++];
    }
}
