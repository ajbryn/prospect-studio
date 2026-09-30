using System.Reflection;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// The migration-drift guard lives in ProspectStudio.Infrastructure.Tests, where it can reach the
/// DbContext. Nothing there notices if the file is simply deleted, so this cross-check does: without
/// it, a later chunk can forget a migration and every chunk after it debugs schema drift instead of
/// reading one red test. CLAUDE.md §Conventions and technical-design §5.3 require the guard for every
/// chunk.
/// </summary>
public class MigrationGuardPresenceTests
{
    private static string SourceRoot { get; } = typeof(MigrationGuardPresenceTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .First(attribute => attribute.Key == "ProspectStudioSourceRoot")
        .Value!;

    private static string GuardFile { get; } = Path.Combine(
        SourceRoot,
        "tests",
        "ProspectStudio.Infrastructure.Tests",
        "Storage",
        "MigrationDriftGuardTests.cs");

    [Fact]
    public void The_migration_drift_guard_still_exists()
    {
        File.Exists(GuardFile).ShouldBeTrue(
            $"""
             The migration-drift guard is missing from '{GuardFile}'.

             Restore it: a test that asserts context.Database.HasPendingModelChanges() is false, plus
             one that asserts every migration is named C<N>_<Description>. It is the only thing that
             catches a forgotten EF migration (CLAUDE.md, technical-design §5.3).
             """);
    }

    [Fact]
    public void The_migration_drift_guard_still_checks_for_pending_model_changes()
    {
        var source = File.ReadAllText(GuardFile);

        source.Contains("HasPendingModelChanges", StringComparison.Ordinal).ShouldBeTrue(
            "the guard must still call HasPendingModelChanges(); weakening it hides a forgotten migration.");
        source.Contains("GetMigrations", StringComparison.Ordinal).ShouldBeTrue(
            "the guard must still check the C<N>_<Description> naming rule over the real migration list.");
    }
}
