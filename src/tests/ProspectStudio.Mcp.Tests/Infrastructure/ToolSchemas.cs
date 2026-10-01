using System.Text.Json;
using ModelContextProtocol.Client;
using Shouldly;

namespace ProspectStudio.Mcp.Tests.Infrastructure;

/// <summary>
/// Reads a tool's advertised input schema, so the contract tests can check that a tool takes the
/// parameters mcp-tools.md documents (and that they keep their <c>camelCase</c> names).
/// </summary>
internal static class ToolSchemas
{
    public static async Task<McpClientTool> FindAsync(McpClient client, string name)
    {
        using var timeout = TestTimeout.Start();
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);

        var matches = tools.Where(tool => tool.Name == name).ToList();
        matches.Count.ShouldBe(
            1,
            $"tools/list must advertise exactly one '{name}' (mcp-tools.md §Summary). "
            + $"It listed: {string.Join(", ", tools.Select(tool => tool.Name).Order(StringComparer.Ordinal))}");

        return matches[0];
    }

    public static List<string> Properties(McpClientTool tool)
    {
        var schema = tool.ProtocolTool.InputSchema;
        return schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            ? [.. properties.EnumerateObject().Select(property => property.Name)]
            : [];
    }

    public static List<string> Required(McpClientTool tool)
    {
        var schema = tool.ProtocolTool.InputSchema;
        return schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
            ? [.. required.EnumerateArray().Select(value => value.GetString() ?? string.Empty)]
            : [];
    }

    public static string Describe(McpClientTool tool) => tool.ProtocolTool.InputSchema.ToString();
}
