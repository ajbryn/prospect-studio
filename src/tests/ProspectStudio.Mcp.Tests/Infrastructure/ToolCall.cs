using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace ProspectStudio.Mcp.Tests.Infrastructure;

/// <summary>
/// Calls a tool over the real MCP transport and unwraps the documented payload, so contract tests
/// never reach past the wire into an internal exception type. Errors are read from the
/// <c>{"error":{"code","message","hint"}}</c> envelope in mcp-tools.md §Errors.
/// </summary>
internal static class ToolCall
{
    public static async Task<JsonElement> OkAsync(
        McpClient client,
        string tool,
        Dictionary<string, object?>? arguments = null,
        string? diagnostics = null)
    {
        var result = await CallAsync(client, tool, arguments, diagnostics);
        var text = Text(result);

        if (result.IsError is true)
        {
            await FailAsync(client, tool, $"'{tool}' failed: {text}", diagnostics);
        }

        return Parse(text);
    }

    /// <summary>
    /// Calls a tool that is expected to fail and returns the structured error. The envelope must
    /// carry <c>code</c>, <c>message</c> and <c>hint</c>; <c>details[]</c> may join them (see
    /// <see cref="ToolErrorPayload.Details"/>).
    /// </summary>
    public static async Task<ToolErrorPayload> ErrorAsync(
        McpClient client,
        string tool,
        Dictionary<string, object?>? arguments = null,
        string? diagnostics = null)
    {
        var result = await CallAsync(client, tool, arguments, diagnostics);
        var text = Text(result);

        result.IsError.ShouldBe(true, $"'{tool}' was expected to fail but returned: {text}{Diagnostics(diagnostics)}");

        var payload = Parse(text);
        if (payload.TryGetProperty("error", out var probe)
            && probe.TryGetProperty("code", out var code)
            && code.GetString() == "INTERNAL")
        {
            // A tool that is not registered reaches the central filter as the SDK's "Unknown tool"
            // exception, which it maps to INTERNAL. Say which it was.
            await FailAsync(client, tool, $"'{tool}' returned INTERNAL: {text}", diagnostics);
        }

        payload.TryGetProperty("error", out var error).ShouldBeTrue(
            $"every tool error is the {{\"error\":{{code,message,hint}}}} envelope (mcp-tools.md §Errors), got: {text}");

        var names = error.EnumerateObject().Select(property => property.Name).ToList();
        foreach (var required in new[] { "code", "message", "hint" })
        {
            names.ShouldContain(required, $"the error envelope is missing '{required}': {text}");
        }

        var details = error.TryGetProperty("details", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(ValidationDetail.From).ToList()
            : [];

        return new ToolErrorPayload(
            error.GetProperty("code").GetString(),
            error.GetProperty("message").GetString(),
            error.GetProperty("hint").GetString(),
            details,
            text);
    }

    private static async Task<CallToolResult> CallAsync(
        McpClient client,
        string tool,
        Dictionary<string, object?>? arguments,
        string? diagnostics)
    {
        using var timeout = TestTimeout.Start();

        try
        {
            return await client.CallToolAsync(tool, arguments ?? [], cancellationToken: timeout.Token);
        }
        catch (McpException exception)
        {
            await FailAsync(client, tool, $"'{tool}' could not be called: {exception.Message}", diagnostics);
            throw;
        }
    }

    /// <summary>
    /// Fails the test, saying up front whether the tool is even registered. Without this, a tool C1 has
    /// not built yet shows up only as the filter's generic <c>INTERNAL</c> envelope.
    /// </summary>
    private static async Task FailAsync(McpClient client, string tool, string message, string? diagnostics)
    {
        using var timeout = TestTimeout.Start();
        var names = (await client.ListToolsAsync(cancellationToken: timeout.Token))
            .Select(listed => listed.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        var registration = names.Contains(tool)
            ? string.Empty
            : $"{Environment.NewLine}tools/list does not advertise '{tool}' at all (mcp-tools.md lists it for chunk C1). "
              + $"It advertises: {string.Join(", ", names)}";

        throw new Xunit.Sdk.XunitException($"{message}{registration}{Diagnostics(diagnostics)}");
    }

    private static string Text(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().Select(block => block.Text).ShouldHaveSingleItem();

    private static JsonElement Parse(string text)
    {
        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new Xunit.Sdk.XunitException($"The tool returned text that is not JSON: {text} ({exception.Message})");
        }
    }

    private static string Diagnostics(string? diagnostics) =>
        string.IsNullOrWhiteSpace(diagnostics) ? string.Empty : Environment.NewLine + diagnostics;
}

internal sealed record ToolErrorPayload(
    string? Code,
    string? Message,
    string? Hint,
    IReadOnlyList<ValidationDetail> Details,
    string RawJson);

/// <summary>
/// One entry of <c>VALIDATION_FAILED</c>'s <c>details[]</c>: a JSON pointer into the submitted
/// document plus a human message (mcp-tools.md §save_search_profile).
/// </summary>
internal sealed record ValidationDetail(string Pointer, string Message)
{
    public static ValidationDetail From(JsonElement element)
    {
        element.ValueKind.ShouldBe(JsonValueKind.Object, $"each validation detail is an object, got: {element}");

        var names = element.EnumerateObject().Select(property => property.Name).ToList();
        names.ShouldContain(
            "pointer",
            $"each detail needs a JSON pointer in a 'pointer' field so Claude can point at the offending value, got: {element}");
        names.ShouldContain("message", $"each detail needs a 'message', got: {element}");

        var pointer = element.GetProperty("pointer").GetString() ?? string.Empty;
        (pointer.Length == 0 || pointer.StartsWith('/')).ShouldBeTrue(
            $"'{pointer}' is not a JSON pointer (RFC 6901): it must be empty or start with '/'.");

        return new ValidationDetail(pointer, element.GetProperty("message").GetString() ?? string.Empty);
    }

    /// <summary>True when either half of the detail names <paramref name="member"/>.</summary>
    public bool Mentions(string member) =>
        Pointer.Contains(member, StringComparison.OrdinalIgnoreCase)
        || Message.Contains(member, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Pointer} :: {Message}";
}
