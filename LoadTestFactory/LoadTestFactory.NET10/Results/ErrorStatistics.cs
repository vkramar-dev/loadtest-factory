using System;

namespace LoadTestFactory.Results;

/// <summary>Represents a group of errors with the same exception type name.</summary>
public sealed class ErrorStatistics
{
    /// <summary>Gets the recorded exception type name.</summary>
    public string ExceptionType { get; init; }

    /// <summary>Gets the number of occurrences of the exception type.</summary>
    public long Count { get; init; }

    /// <summary>Gets the first occurrence time relative to the beginning of the measurement phase.</summary>
    public TimeSpan FirstOccurrence { get; init; }

    /// <summary>Gets the last occurrence time relative to the beginning of the measurement phase.</summary>
    public TimeSpan LastOccurrence { get; init; }

    /// <summary>Gets an optional exception message (not recorded by the current collector).</summary>
    public string Message { get; init; }

}
