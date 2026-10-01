using System.Diagnostics;
using System.Text;

namespace ProspectStudio.Mcp.Tests.Infrastructure;

/// <summary>
/// Runs a CLI verb of the real server binary in its own process. CLI verbs are the one path allowed to
/// write to stdout, because the JSON-RPC transport is never started on it.
/// </summary>
internal static class ServerCli
{
    public static async Task<CliRun> RunAsync(
        string home,
        string data,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? extraEnvironment = null,
        CancellationToken cancellationToken = default)
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
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in TestEnvironment.For(home, data, extraEnvironment))
        {
            startInfo.Environment[name] = value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the server process.");

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new CliRun(process.ExitCode, await stdout, await stderr);
    }
}

internal sealed record CliRun(int ExitCode, string Stdout, string Stderr)
{
    public override string ToString() =>
        $"exit {ExitCode}{Environment.NewLine}--- stdout{Environment.NewLine}{Stdout}{Environment.NewLine}--- stderr{Environment.NewLine}{Stderr}";
}
