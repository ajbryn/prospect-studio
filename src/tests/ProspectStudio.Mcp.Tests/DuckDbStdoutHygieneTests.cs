using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProspectStudio.Mcp.Tests.Infrastructure;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// The residual stdout risk C0 recorded for C2 <strong>and C4</strong> to close: <c>Console.SetOut</c>
/// and the source rules cannot stop a native library writing to the process's stdout handle through the
/// C runtime, nor a child process that inherits that handle. C2 is the first chunk to load a native
/// library (DuckDB); C4 is the one that gives it real work - a full Parquet scan, a spatial join and
/// <c>to_json</c> - so both tool calls are driven explicitly rather than one standing in for the other.
/// </summary>
public partial class DuckDbStdoutHygieneTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "prospect-studio-duckdb-stdout-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Resolve_geography_writes_nothing_but_json_rpc_to_stdout()
    {
        using var timeout = TestTimeout.Start(120);
        var data = PrepareReferenceData();

        await AssertOnlyJsonRpcAsync(
            data,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"resolve_geography","arguments":{"query":"Houston metro"}}}""",
            "resolve_geography",
            timeout.Token);
    }

    [Fact]
    public async Task Find_candidates_writes_nothing_but_json_rpc_to_stdout()
    {
        // The larger native path by far: find_candidates scans the whole Parquet extract, runs a
        // spatial join across two CRS and serializes rows with to_json. Covering it only indirectly
        // through the SDK-client contract tests is weaker than this, because that client silently
        // discards any stdout line that is not a message - which is exactly what has to be caught.
        using var timeout = TestTimeout.Start(180);
        var data = PrepareReferenceData();
        PlacesDataFixture.Install(data);

        const string CampaignId = "cmp_STDOUT";
        await ServerCampaigns.AddWithProfileAsync(data, CampaignId, timeout.Token);

        var request =
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"find_candidates","arguments":{"campaignId":"CAMPAIGN"}}}"""
                .Replace("CAMPAIGN", CampaignId, StringComparison.Ordinal);

        await AssertOnlyJsonRpcAsync(data, request, "find_candidates", timeout.Token);
    }

    private string PrepareReferenceData()
    {
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        ReferenceDataFixture.Install(data);
        return data;
    }

    /// <summary>
    /// Runs one raw <c>tools/call</c> and fails if any stdout line is not JSON-RPC, or if the call did
    /// not actually succeed - a tool that errored out early may never have reached DuckDB, which would
    /// make the guard vacuous.
    /// </summary>
    private async Task AssertOnlyJsonRpcAsync(
        string data,
        string request,
        string tool,
        CancellationToken cancellationToken)
    {
        var result = await RawServerSession.RunAsync(
            Path.Combine(_root, "home"),
            data,
            [request],
            lastRequestId: 2,
            cancellationToken);

        foreach (var line in result.Stdout)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException exception)
            {
                throw new ShouldAssertException(
                    $"stdout carried a line that is not JSON-RPC: '{line}'. DuckDB loads a native library "
                    + $"and reads a Parquet file during '{tool}', and neither may print."
                    + $"{Environment.NewLine}{exception.Message}{Environment.NewLine}{result.Stderr}");
            }

            using (document)
            {
                document.RootElement.TryGetProperty("jsonrpc", out var version).ShouldBeTrue($"stdout line: '{line}'");
                version.GetString().ShouldBe("2.0");
            }
        }

        var response = result.Stdout
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .LastOrDefault(element =>
                element.TryGetProperty("id", out var id) && id.TryGetInt32(out var value) && value == 2);

        response.ValueKind.ShouldBe(
            JsonValueKind.Object,
            $"no response to the {tool} call arrived.{Environment.NewLine}{result.Stderr}");

        response.TryGetProperty("result", out var toolResult).ShouldBeTrue(
            $"{tool} must answer with a tool result: {response}{Environment.NewLine}{DuckDbExtensionCache.Advice}");

        (toolResult.TryGetProperty("isError", out var isError) && isError.GetBoolean()).ShouldBeFalse(
            $"the call has to actually reach DuckDB for this guard to mean anything: {toolResult}"
            + $"{Environment.NewLine}{DuckDbExtensionCache.Advice}{Environment.NewLine}{result.Stderr}");
    }

    [GeneratedRegex(@"ProcessStartInfo|Process\s*\.\s*Start")]
    private static partial Regex StartsAChildProcess();

    [GeneratedRegex(@"RedirectStandardOutput\s*=\s*true")]
    private static partial Regex RedirectsChildStdout();

    [Fact]
    public void No_production_source_starts_a_child_process_that_inherits_stdout()
    {
        // A child process started without RedirectStandardOutput = true inherits our stdout handle, so
        // anything it prints lands in the middle of the JSON-RPC stream. Console.SetOut cannot help.
        var files = ProductionSourceFiles();
        files.Count.ShouldBeGreaterThan(5, "the scan found suspiciously few files, so the paths are probably wrong.");

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var code = File.ReadAllText(file);
            if (StartsAChildProcess().IsMatch(code) && !RedirectsChildStdout().IsMatch(code))
            {
                offenders.Add(Path.GetRelativePath(SourceRoot, file));
            }
        }

        offenders.ShouldBeEmpty(
            "every production file that starts a child process must set RedirectStandardOutput = true, "
            + "or the child writes straight onto the protocol stream.");
    }

    private static string SourceRoot { get; } = typeof(DuckDbStdoutHygieneTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .First(attribute => attribute.Key == "ProspectStudioSourceRoot")
        .Value!;

    private static List<string> ProductionSourceFiles() =>
    [
        .. Directory.EnumerateDirectories(SourceRoot, "ProspectStudio.*")
            .SelectMany(project => Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)),
    ];

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
