namespace LoadTestFactory.Results;

/// <summary>Represents the results of an individual measured call.</summary>
public sealed class MeasuredCallResult
{
    public string Name { get; init; }

    public ExecutionStatistics Statistics { get; init; } = new();

    public CompletionTailStatistics CompletionTail { get; init; } = new();
}
