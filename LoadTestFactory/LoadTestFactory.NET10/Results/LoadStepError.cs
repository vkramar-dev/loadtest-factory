namespace LoadTestFactory.Results;

/// <summary>Identifies an error originating during step stabilization.</summary>
public sealed class LoadStepError
{
    public string Name { get; init; }
    public bool IsMeasuredCall { get; init; }
    /// <summary>Gets error statistics with timestamps relative to the beginning of the step.</summary>
    public ErrorStatistics Error { get; init; }
}
