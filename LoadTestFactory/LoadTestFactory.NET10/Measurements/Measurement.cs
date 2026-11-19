namespace LoadTestFactory.Measurements;

internal struct Measurement
{
    public Measurement(int scenarioId, long startAt, long elapsedTicks)
    {
        ScenarioId = scenarioId;
        StartAt = startAt;
        ElapsedTicks = elapsedTicks;
    }

    public long StartAt;

    public long ElapsedTicks;

    public int ScenarioId;

    public int ResultType;
}
