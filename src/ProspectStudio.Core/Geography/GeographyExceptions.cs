namespace ProspectStudio.Core.Geography;

/// <summary>Reference data is missing, which the tool reports as <c>NOT_READY</c>.</summary>
/// <remarks>
/// <paramref name="hint"/> overrides the tool's default "run prepare_data", because the prerequisite is
/// not always the data: a missing DuckDB extension needs a different sentence.
/// </remarks>
public sealed class GeographyNotReadyException(string message, string? hint = null) : Exception(message)
{
    public string? Hint { get; } = hint;
}

/// <summary>Nothing matched, which the tool reports as <c>NOT_FOUND</c>.</summary>
public sealed class GeographyNotFoundException(string message) : Exception(message);

/// <summary>The request made no sense, which the tool reports as <c>VALIDATION_FAILED</c>.</summary>
public sealed class GeographyRequestException(string message) : Exception(message);

/// <summary>A scope type a later chunk builds, which the tool reports as <c>UNSUPPORTED</c>.</summary>
public sealed class GeographyUnsupportedException(string message) : Exception(message);

/// <summary>An outside service failed, which the tool reports as <c>EXTERNAL_API</c>.</summary>
public sealed class GeographyExternalException(string message) : Exception(message);
