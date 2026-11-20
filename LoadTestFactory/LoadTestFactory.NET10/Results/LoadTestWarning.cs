namespace LoadTestFactory.Results;

/// <summary>A diagnostic warning about the load generator, not a failed test scenario.</summary>
public sealed record LoadTestWarning(string Code, string Message);
