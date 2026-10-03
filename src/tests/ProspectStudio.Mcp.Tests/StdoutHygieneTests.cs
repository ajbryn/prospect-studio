using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ProspectStudio.Mcp.Tests.Infrastructure;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// Guards the project's hard rule: stdout carries JSON-RPC and nothing else. Drives the server
/// process directly rather than through the SDK client so every stdout line can be inspected.
/// </summary>
public class StdoutHygieneTests : IDisposable
{
    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"stdout-hygiene-test","version":"1.0.0"}}}""";

    private const string Initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    private const string ListTools = """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""";

    private const string CallGetStatus =
        """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_status","arguments":{}}}""";

    /// <summary>The request ids sent below. <c>notifications/initialized</c> has none and gets no reply.</summary>
    private static readonly int[] ExpectedIds = [1, 2, 3];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "prospect-studio-stdout-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Every_stdout_line_is_a_json_rpc_message()
    {
        var (stdout, stderr, exitCode) = await RunServerAsync();

        // Every request must be answered, in any order - and the ids are sorted before comparing
        // because that order is precisely what must not matter here. An observed run answered
        // [1, 3, 2]; comparing them as they arrived would re-introduce, one layer up, the very
        // assumption the reader below was fixed for. When something is missing the useful question is
        // always "what did the server do instead", so the message carries the lines it did send, how
        // it exited, and its log.
        var answered = stdout.Select(ResponseId).OfType<int>().Order().ToList();

        answered.ShouldBe(
            [.. ExpectedIds],
            $"""
             The server answered {answered.Count} of {ExpectedIds.Length} requests (initialize,
             tools/list and tools/call get_status; notifications/initialized gets no reply).
             Exit code: {(exitCode is null ? "still running" : exitCode.ToString())}.

             stdout:
             {(stdout.Count == 0 ? "(nothing)" : string.Join(Environment.NewLine, stdout))}

             stderr:
             {stderr}
             """);

        foreach (var line in stdout)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException exception)
            {
                throw new ShouldAssertException(
                    $"stdout carried a line that is not JSON: '{line}'{Environment.NewLine}{exception.Message}");
            }

            using (document)
            {
                document.RootElement.ValueKind.ShouldBe(JsonValueKind.Object, $"stdout line: '{line}'");
                document.RootElement.TryGetProperty("jsonrpc", out var version).ShouldBeTrue($"stdout line: '{line}'");
                version.GetString().ShouldBe("2.0");
            }
        }
    }

    [Fact]
    public async Task Logs_go_to_stderr()
    {
        var (_, stderr, _) = await RunServerAsync();

        stderr.ShouldNotBeNullOrWhiteSpace();
        stderr.ShouldContain("prospect-studio");
    }

    private async Task<(List<string> Stdout, string Stderr, int? ExitCode)> RunServerAsync()
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
        foreach (var (name, value) in TestEnvironment.For(Path.Combine(_root, "home"), Path.Combine(_root, "data")))
        {
            startInfo.Environment[name] = value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the server process.");

        var stderr = new StringBuilder();
        var stderrReader = Task.Run(async () =>
        {
            var line = await process.StandardError.ReadLineAsync();
            while (line is not null)
            {
                stderr.AppendLine(line);
                line = await process.StandardError.ReadLineAsync();
            }
        });

        foreach (var message in new[] { Initialize, Initialized, ListTools, CallGetStatus })
        {
            await process.StandardInput.WriteLineAsync(message);
            await process.StandardInput.FlushAsync();
        }

        // Read until every request has been answered, not until one particular answer shows up.
        //
        // JSON-RPC does not promise responses in request order, and this server does not deliver them
        // in request order: the SDK dispatches requests concurrently, so a cheap handler finishes ahead
        // of an expensive one. Both reorderings have been observed on this machine - [2,1,3] in a
        // 25-run probe under CPU load, and [1,3,2] in a full Release run, which is get_status beating
        // tools/list. The old loop broke the moment it saw id 3, so on [1,3,2] it stopped with two
        // lines collected and the third still in the pipe, and the count assertion failed about twelve
        // seconds in. Not a short write and not a dead server - the run that was finally captured
        // exited 0 with every line valid JSON-RPC - just a reader that assumed an ordering nobody
        // guarantees.
        //
        // Worth knowing why C4 made it bite: tools/list now serialises two more tools with large input
        // schemas, so it got slower and get_status overtakes it more often than it used to.
        var pending = new HashSet<int>(ExpectedIds);
        var stdout = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        while (pending.Count > 0)
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
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
        return (stdout, stderr.ToString(), process.HasExited ? process.ExitCode : null);
    }

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
            // Not JSON at all, which is the violation this test exists to catch. The assertion reports
            // it; the reader just does not count it as an answer.
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp folder is harmless.
        }

        GC.SuppressFinalize(this);
    }
}
