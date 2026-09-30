using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProspectStudio.Mcp.Errors;

namespace ProspectStudio.Mcp.Tests.Infrastructure;

/// <summary>
/// Tools that exist only in the test assembly, so the error contract can be exercised in any build
/// configuration without shipping a failing tool in the server.
/// </summary>
[McpServerToolType]
public sealed class ThrowingTools
{
    public const string UnexpectedMessage = "secret detail from an unexpected failure";

    [McpServerTool(Name = "throws_tool_exception")]
    [Description("Throws a structured tool exception.")]
    public static CallToolResult ThrowsToolException() =>
        throw new McpToolException(ToolErrorCodes.NotReady, "Reference data is missing.", "Run prepare_data for TX first.");

    [McpServerTool(Name = "throws_unexpected")]
    [Description("Throws an exception the server does not expect.")]
    public static CallToolResult ThrowsUnexpected() =>
        throw new InvalidOperationException(UnexpectedMessage);

    [McpServerTool(Name = "returns_payload")]
    [Description("Returns a successful payload.")]
    public static CallToolResult ReturnsPayload() => ToolResults.Ok(new { Ok = true });
}
