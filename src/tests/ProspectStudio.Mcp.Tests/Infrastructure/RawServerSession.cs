using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ProspectStudio.Mcp.Tests.Infrastructure;

/// <summary>
/// Drives the server process with hand-written JSON-RPC lines and keeps every stdout line, so a test
/// can inspect the protocol stream itself rather than the SDK client's view of it. The SDK client
/// silently discards anything that is not a message, which is exactly what the stdout hard rule needs
/// to catch.
/// </summary>
internal static class RawServerSession
{
    /// <summary>
    /// Runs <paramref name="requests"/> (raw JSON-RPC lines, after the usual handshake) and returns
    /// every stdout line up to and including the response to the last request.
    /// </summary>
    public static async Task<RawServerResult> RunAsync(
        string home,
        string data,
        IReadOnlyList<string> requests,
        int lastRequestId,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add(TestServerBinary.Dll);
        foreach (var (name, value) in TestEnvironment.For(home, data))
        {
            startInfo.Environment[name] = value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the server process.");

        var stderr = new StringBuilder();
        var stderrReader = Task.Run(
            async () =>
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                while (line is not null)
                {
                    stderr.AppendLine(line);
                    line = await process.StandardError.ReadLineAsync(cancellationToken);
                }
            },
            CancellationToken.None);

        foreach (var request in Handshake.Concat(requests))
        {
            await process.StandardInput.WriteLineAsync(request);
            await process.StandardInput.FlushAsync(cancellationToken);
        }

        var stdout = new List<string>();
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                continue;
            }

            stdout.Add(line);
            if (IsResponseTo(line, lastRequestId))
            {
                break;
            }
        }

        process.StandardInput.Close();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
        }

        await stderrReader;
        return new RawServerResult(stdout, stderr.ToString());
    }

    private static readonly string[] Handshake =
    [
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"raw-session-test","version":"1.0.0"}}}""",
        """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
    ];

    private static bool IsResponseTo(string line, int id)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("id", out var actual)
                && actual.TryGetInt32(out var value)
                && value == id;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal sealed record RawServerResult(IReadOnlyList<string> Stdout, string Stderr);
