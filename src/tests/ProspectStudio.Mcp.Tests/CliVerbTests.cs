using System.Diagnostics;
using System.Text;
using ProspectStudio.Core.Configuration;
using ProspectStudio.Mcp.Tests.Infrastructure;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// publish-mcp.ps1 gates the publish on doctor's exit code, and doctor is the only path allowed to
/// write to stdout, so both are worth a guard.
/// </summary>
public class CliVerbTests : IDisposable
{
    private const string SecretValue = "census-key-that-must-not-be-printed";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "prospect-studio-cli-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Doctor_exits_zero_and_prints_key_presence_but_never_a_key_value()
    {
        var (exitCode, stdout, stderr) = await RunAsync("doctor", withCensusKey: true);

        exitCode.ShouldBe(0, stderr);
        stdout.ShouldNotContain(SecretValue);
        stdout.ShouldContain("census");
        stdout.ShouldContain(Path.GetFullPath(Path.Combine(_root, "data")));
    }

    [Fact]
    public async Task An_unknown_verb_fails_and_explains_itself()
    {
        var (exitCode, _, stderr) = await RunAsync("wat", withCensusKey: false);

        exitCode.ShouldNotBe(0);
        stderr.ShouldContain("wat");
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string verb, bool withCensusKey)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add(TestServerBinary.Dll);
        startInfo.ArgumentList.Add(verb);

        foreach (var (name, value) in TestEnvironment.For(Path.Combine(_root, "home"), Path.Combine(_root, "data")))
        {
            startInfo.Environment[name] = value;
        }

        if (withCensusKey)
        {
            startInfo.Environment[PsOptionsFactory.CensusKeyVariable] = SecretValue;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the server process.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = TestTimeout.Start();
        await process.WaitForExitAsync(timeout.Token);

        return (process.ExitCode, await stdout, await stderr);
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
