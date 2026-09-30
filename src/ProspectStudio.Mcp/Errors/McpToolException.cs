namespace ProspectStudio.Mcp.Errors;

/// <summary>
/// Thrown by tool code to return a structured error. Codes come from mcp-tools.md §Errors.
/// </summary>
public sealed class McpToolException : Exception
{
    public McpToolException(
        string code,
        string message,
        string? hint = null,
        IReadOnlyList<ToolErrorDetail>? details = null)
        : base(message)
    {
        Code = code;
        Hint = hint;
        Details = details;
    }

    public string Code { get; }

    public string? Hint { get; }

    /// <summary>
    /// Where a <c>VALIDATION_FAILED</c> went wrong, one entry per problem. Null or empty for every
    /// other code, which keeps the envelope at {code, message, hint} (mcp-tools.md §Errors).
    /// </summary>
    public IReadOnlyList<ToolErrorDetail>? Details { get; }
}
