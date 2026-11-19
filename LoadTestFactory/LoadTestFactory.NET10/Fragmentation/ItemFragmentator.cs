namespace LoadTestFactory.Fragmentation;

class ItemFragmentator
{
    private readonly Item[] _items;

    public ItemFragmentator(TestScenario[] scenarios)
    {
        _items = new Item[scenarios.Length];
        for (int i = 0; i < _items.Length; ++i)
            _items[i] = new Item(scenarios[i]);
    }

    public TestScenario[] Fragment()
    {
        int allLength = _items.Select(item => item.OriginalCount).Sum();

        TestScenario[] fragmentedScenario = new TestScenario[allLength];
        for (int i = 0; i < allLength; ++i)
            fragmentedScenario[i] = SelectNext();

        return fragmentedScenario;
    }

    private TestScenario SelectNext()
    {
        Item max = _items[0];
        for (int i = 1; i < _items.Length; ++i)
        {
            Item it = _items[i];

            if (it.Priority > max.Priority)
                max = _items[i];
        }

        return max.TakeOne();
    }
}
