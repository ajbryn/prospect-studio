using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;

namespace ProspectStudio.Mcp.Errors;

/// <summary>
/// Builds tool results. Tools return <see cref="Ok"/> with a payload serialized here, so the wire
/// shape stays exactly what mcp-tools.md specifies and nothing is duplicated into structured
/// content. <see cref="Error"/> is produced in one place by <see cref="ToolCallFilter"/>.
/// </summary>
public static class ToolResults
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static CallToolResult Ok<T>(T payload) =>
        Text(JsonSerializer.Serialize(payload, Json), isError: false);

    public static CallToolResult Error(McpToolException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var envelope = new ToolErrorEnvelope(new ToolError(exception.Code, exception.Message, exception.Hint));
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
    [property: JsonPropertyName("hint")] string? Hint);
