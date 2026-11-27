namespace LoadTestFactory.Fragmentation;

public sealed class FullyFragmentedTestScenarioCircle<T>
{
    private readonly TestScenario<T>[] _scenarios;

    public FullyFragmentedTestScenarioCircle(TestScenario<T>[] scenarios)
    {
        TestScenario<T>[] processedScenarios = ProcessScenarios(scenarios);

        ItemFragmentator<T> fragmentator = new (scenarios);
        _scenarios = fragmentator.Fragment();
    }

    public TestScenarioIterator<T> GetIterator()
    {
        return new TestScenarioIterator<T>(_scenarios);
    }

    private TestScenario<T>[] ProcessScenarios(TestScenario<T>[] scenarios)
    {
        scenarios.Sort(
            new Comparison<TestScenario<T>>((left, right) => right.Weight - left.Weight));

        var weights = scenarios
            .Select(s => s.Weight)
            .ToArray();

        int gcd = MathEx.GetGcd(weights);

        if (gcd > 1)
        {
            for (int i = 0; i < scenarios.Length; ++i)
                scenarios[i].Weight /= gcd;
        }

        return scenarios;
    }
}
