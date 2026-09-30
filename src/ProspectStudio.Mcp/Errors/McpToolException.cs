namespace ProspectStudio.Mcp.Errors;

/// <summary>
/// Thrown by tool code to return a structured error. Codes come from mcp-tools.md §Errors.
/// </summary>
public sealed class McpToolException : Exception
{
    public McpToolException(string code, string message, string? hint = null)
        : base(message)
    {
        Code = code;
        Hint = hint;
    }

    public string Code { get; }

    public string? Hint { get; }
}
