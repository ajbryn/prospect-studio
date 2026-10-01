namespace ProspectStudio.Core.Jobs;

/// <summary>
/// There is no job with that id. The MCP layer turns it into <c>NOT_FOUND</c> (mcp-tools.md §Errors);
/// Core stays free of any MCP type.
/// </summary>
public sealed class JobNotFoundException(string jobId)
    : InvalidOperationException($"There is no job with id '{jobId}'.")
{
    public string JobId { get; } = jobId;
}
