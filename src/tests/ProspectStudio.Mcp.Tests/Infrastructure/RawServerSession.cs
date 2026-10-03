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
    /// every stdout line, reading until both the handshake and
    /// <paramref name="lastRequestId"/> have been answered.
    /// </summary>
    /// <param name="lastRequestId">
    /// The id of the last request in <paramref name="requests"/>. Its response is not assumed to come
    /// last on the wire - the server answers concurrently and may reorder - so it is one of the ids
    /// waited for rather than the signal to stop.
    /// </param>
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

        // Read until both the handshake and the caller's last request have been answered, in whatever
        // order they come back. The SDK dispatches requests concurrently and JSON-RPC does not promise
        // response order - measured directly, two of 25 handshakes answered tools/list before
        // initialize. Breaking on one id alone would stop with earlier lines still in the pipe, and
        // those unread lines are exactly what a stdout-purity check exists to inspect.
        var pending = new HashSet<int> { HandshakeRequestId, lastRequestId };
        var stdout = new List<string>();

        while (pending.Count > 0)
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

            if (ResponseId(line) is { } answered)
            {
                pending.Remove(answered);
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

    /// <summary>The id of the <c>initialize</c> request in <see cref="Handshake"/>.</summary>
    private const int HandshakeRequestId = 1;

    private static readonly string[] Handshake =
    [
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"raw-session-test","version":"1.0.0"}}}""",
        """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
    ];

    /// <summary>The id this line is a response to, or null when it is not a response at all.</summary>
    private static int? ResponseId(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("id", out var actual)
                && actual.TryGetInt32(out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            // Not JSON at all, which is the violation the callers are looking for. It is kept in the
            // returned lines for them to assert on; it simply does not count as an answer.
            return null;
        }
    }
}

internal sealed record RawServerResult(IReadOnlyList<string> Stdout, string Stderr);
