namespace LoadTestFactory.Fragmentation;

class ItemFragmentator<T>
{
    private readonly Item<T>[] _items;

    public ItemFragmentator(TestScenario<T>[] scenarios)
    {
        _items = new Item<T>[scenarios.Length];
        for (int i = 0; i < _items.Length; ++i)
            _items[i] = new Item<T>(scenarios[i]);
    }

    public TestScenario<T>[] Fragment()
    {
        int allLength = _items.Select(item => item.OriginalCount).Sum();

        TestScenario<T>[] fragmentedScenario = new TestScenario<T>[allLength];
        for (int i = 0; i < allLength; ++i)
            fragmentedScenario[i] = SelectNext();

        return fragmentedScenario;
    }

    private TestScenario<T> SelectNext()
    {
        Item<T> max = _items[0];
        for (int i = 1; i < _items.Length; ++i)
        {
            Item<T> it = _items[i];

            if (it.Priority > max.Priority)
                max = _items[i];
        }

        return max.TakeOne();
    }
}
