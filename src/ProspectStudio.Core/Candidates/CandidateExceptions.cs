namespace ProspectStudio.Core.Candidates;

/// <summary>
/// A prerequisite of candidate search is missing - an Overture extract, reference data or a saved
/// search profile. The MCP layer maps this to <c>NOT_READY</c> (mcp-tools.md §Errors).
/// </summary>
public sealed class CandidatesNotReadyException(string message, string? hint = null) : Exception(message)
{
    public string? Hint { get; } = hint;
}

/// <summary>
/// The caller asked for something that cannot be searched - no categories and no keywords, say. The
/// MCP layer maps this to <c>VALIDATION_FAILED</c>.
/// </summary>
public sealed class CandidateRequestException(string message, string? hint = null) : Exception(message)
{
    public string? Hint { get; } = hint;
}
