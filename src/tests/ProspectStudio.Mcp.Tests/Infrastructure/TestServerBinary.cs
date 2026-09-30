using System.Reflection;

namespace ProspectStudio.Mcp.Tests.Infrastructure;

internal static class TestServerBinary
{
    private const string MetadataKey = "ProspectStudioServerDll";

    public static string Dll { get; } = Resolve();

    private static string Resolve()
    {
        var path = typeof(TestServerBinary).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == MetadataKey)
            ?.Value;

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"Assembly metadata '{MetadataKey}' is missing from the test project.");
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"The server was not built at '{path}'.");
        }

        return path;
    }
}
