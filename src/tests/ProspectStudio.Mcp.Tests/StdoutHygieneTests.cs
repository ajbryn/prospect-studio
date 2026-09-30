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

    private readonly string _root = Path.Combine(Path.GetTempPath(), "prospect-studio-stdout-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Every_stdout_line_is_a_json_rpc_message()
    {
        var (stdout, stderr) = await RunServerAsync();

        stdout.Count.ShouldBeGreaterThanOrEqualTo(3, $"stderr:{Environment.NewLine}{stderr}");

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
        var (_, stderr) = await RunServerAsync();

        stderr.ShouldNotBeNullOrWhiteSpace();
        stderr.ShouldContain("prospect-studio");
    }

    private async Task<(List<string> Stdout, string Stderr)> RunServerAsync()
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

        var stdout = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
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
            if (IsResponseTo(line, id: 3))
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
        return (stdout, stderr.ToString());
    }

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
