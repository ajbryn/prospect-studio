using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ProspectStudio.Core.Json;

namespace ProspectStudio.Mcp.Errors;

/// <summary>
/// Builds tool results. Tools return <see cref="Ok"/> with a payload serialized here, so the wire
/// shape stays exactly what mcp-tools.md specifies and nothing is duplicated into structured
/// content. <see cref="Error"/> is produced in one place by <see cref="ToolCallFilter"/>.
/// </summary>
public static class ToolResults
{
    /// <summary>
    /// The shared configuration from <see cref="ProspectStudioJson"/>, so a job's stored
    /// <c>result_json</c> and a tool response format timestamps identically.
    /// </summary>
    public static readonly JsonSerializerOptions Json = ProspectStudioJson.Options;

    public static CallToolResult Ok<T>(T payload) =>
        Text(JsonSerializer.Serialize(payload, Json), isError: false);

    public static CallToolResult Error(McpToolException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var details = exception.Details is { Count: > 0 } reported ? reported : null;
        var envelope = new ToolErrorEnvelope(new ToolError(exception.Code, exception.Message, exception.Hint, details));
        return Text(JsonSerializer.Serialize(envelope, Json), isError: true);
    }

    public static CallToolResult Text(string text, bool isError) => new()
    {
        IsError = isError,
        Content = [new TextContentBlock { Text = text }],
    };
}

public sealed record ToolErrorEnvelope([property: JsonPropertyName("error")] ToolError Error);

public sealed record ToolError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("hint")] string? Hint,
    [property: JsonPropertyName("details"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ToolErrorDetail>? Details = null);

/// <summary>
/// One problem inside a <c>VALIDATION_FAILED</c>: an RFC 6901 pointer into the document the caller
/// submitted, and what is wrong at that spot (mcp-tools.md §save_search_profile).
/// </summary>
public sealed record ToolErrorDetail(
    [property: JsonPropertyName("pointer")] string Pointer,
    [property: JsonPropertyName("message")] string Message);
