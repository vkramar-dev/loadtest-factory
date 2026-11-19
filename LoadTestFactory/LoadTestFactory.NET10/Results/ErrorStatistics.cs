using System;

namespace LoadTestFactory.Results;

/// <summary>Represents a group of errors with the same exception type.</summary>
public sealed class ErrorStatistics
{
    /// <summary>Gets the full name of the exception type.</summary>
    public string ExceptionType { get; init; }

    /// <summary>Gets the number of occurrences of the exception type.</summary>
    public long Count { get; init; }

    /// <summary>Gets the first occurrence time relative to the beginning of the measurement phase.</summary>
    public TimeSpan FirstOccurrence { get; init; }

    /// <summary>Gets the last occurrence time relative to the beginning of the measurement phase.</summary>
    public TimeSpan LastOccurrence { get; init; }

    /// <summary>Gets the message of the first recorded exception in the group.</summary>
    public string Message { get; init; }

    /// <summary>Gets the stack trace of the first recorded exception in the group, if available.</summary>
    public string StackTrace { get; init; }
}
