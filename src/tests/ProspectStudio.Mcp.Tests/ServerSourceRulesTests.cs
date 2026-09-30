using System.Reflection;
using System.Text.RegularExpressions;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// Guards the hard rule at source level across every production project. Console.SetOut redirects
/// the console writer, but it does not cover Console.OpenStandardOutput, a using-static import or a
/// child process that inherits the stdout handle, and a later chunk could delete the redirect
/// altogether. Only the CLI verbs, which never start the transport, may write to stdout.
/// </summary>
public partial class ServerSourceRulesTests
{
    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"Console\s*\.\s*(?:Write|Out\b|OpenStandardOutput)")]
    private static partial Regex ConsoleStdoutUse();

    [GeneratedRegex(@"using\s+static\s+(?:global::)?System\s*\.\s*Console\s*;")]
    private static partial Regex ConsoleStaticImport();

    [GeneratedRegex(@"using\s+\w+\s*=\s*(?:global::)?System\s*\.\s*Console\s*;")]
    private static partial Regex ConsoleAliasImport();

    [GeneratedRegex(@"Console\s*\.\s*SetOut\s*\(\s*Console\s*\.\s*Error\s*\)")]
    private static partial Regex ConsoleRedirect();

    [GeneratedRegex(@"WithProspectStudioToolFilter\s*\(\s*\)")]
    private static partial Regex ToolFilterWiring();

    [Fact]
    public void No_production_source_outside_the_cli_writes_to_stdout()
    {
        var files = ProductionSourceFiles();
        files.Count.ShouldBeGreaterThan(5, "the scan found suspiciously few files, so the paths are probably wrong");

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var code = StripComments(File.ReadAllText(file));
            var name = Path.GetRelativePath(SourceRoot, file);

            if (ConsoleStdoutUse().IsMatch(code))
            {
                offenders.Add($"{name}: writes to Console stdout");
            }

            if (ConsoleStaticImport().IsMatch(code))
            {
                offenders.Add($"{name}: imports System.Console statically");
            }

            if (ConsoleAliasImport().IsMatch(code))
            {
                offenders.Add($"{name}: aliases System.Console");
            }
        }

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public void Every_production_project_is_scanned()
    {
        var projects = ProductionProjectDirectories();

        projects.Select(Path.GetFileName).ShouldBe(
            ["ProspectStudio.Core", "ProspectStudio.Infrastructure", "ProspectStudio.Mcp"],
            ignoreOrder: true);
    }

    [Fact]
    public void Program_still_redirects_the_console_writer_to_stderr()
    {
        var program = Path.Combine(SourceRoot, "ProspectStudio.Mcp", "Program.cs");
        File.Exists(program).ShouldBeTrue($"Program.cs not found at '{program}'.");

        ConsoleRedirect().IsMatch(StripComments(File.ReadAllText(program))).ShouldBeTrue(
            "Program.cs must keep Console.SetOut(Console.Error) so a stray console write cannot reach the protocol stream.");
    }

    // The in-process error-contract tests build their own server, so only this source check
    // notices if Program.cs stops wiring the filter. Without it, Release has no coverage at all:
    // the debug_* tools that prove the wiring end to end are compiled out.
    [Fact]
    public void Program_still_wires_the_tool_error_filter()
    {
        var program = Path.Combine(SourceRoot, "ProspectStudio.Mcp", "Program.cs");
        File.Exists(program).ShouldBeTrue($"Program.cs not found at '{program}'.");

        ToolFilterWiring().IsMatch(StripComments(File.ReadAllText(program))).ShouldBeTrue(
            "Program.cs must keep WithProspectStudioToolFilter() so tool errors reach the client as "
            + "{\"error\":{code,message,hint}} instead of the SDK's bare \"An error occurred.\".");
    }

    private static string SourceRoot { get; } = typeof(ServerSourceRulesTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .First(attribute => attribute.Key == "ProspectStudioSourceRoot")
        .Value!;

    private static List<string> ProductionProjectDirectories()
    {
        Directory.Exists(SourceRoot).ShouldBeTrue($"Source root not found at '{SourceRoot}'.");
        return [.. Directory.EnumerateDirectories(SourceRoot, "ProspectStudio.*").Order()];
    }

    private static List<string> ProductionSourceFiles()
    {
        var cli = Path.Combine(SourceRoot, "ProspectStudio.Mcp", "Cli") + Path.DirectorySeparatorChar;

        return
        [
            .. ProductionProjectDirectories()
                .SelectMany(project => Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
                .Where(file => !file.StartsWith(cli, StringComparison.OrdinalIgnoreCase))
                .Where(file => !IsBuildArtifact(file)),
        ];
    }

    private static bool IsBuildArtifact(string file)
    {
        var separator = Path.DirectorySeparatorChar;
        return file.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase)
            || file.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripComments(string text)
    {
        var withoutBlocks = BlockComment().Replace(text, " ");
        var kept = withoutBlocks
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));
        return string.Join('\n', kept);
    }
}
