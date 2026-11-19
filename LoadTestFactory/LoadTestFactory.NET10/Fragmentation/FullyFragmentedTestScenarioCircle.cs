namespace LoadTestFactory.Fragmentation;

public sealed class FullyFragmentedTestScenarioCircle
{
    private readonly TestScenario[] _scenarios;

    public FullyFragmentedTestScenarioCircle(TestScenario[] scenarios)
    {
        TestScenario[] processedScenarios = ProcessScenarios(scenarios);

        ItemFragmentator fragmentator = new (scenarios);
        _scenarios = fragmentator.Fragment();
    }

    public TestScenarioIterator GetIterator()
    {
        return new TestScenarioIterator(_scenarios);
    }

    private TestScenario[] ProcessScenarios(TestScenario[] scenarios)
    {
        scenarios.Sort(
            new Comparison<TestScenario>((left, right) => right.Weight - left.Weight));

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
