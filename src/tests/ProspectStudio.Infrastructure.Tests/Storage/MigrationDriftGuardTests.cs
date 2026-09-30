// ─────────────────────────────────────────────────────────────────────────────────────────────────
//  DO NOT DELETE OR WEAKEN THIS FILE.
//
//  It is the only thing that catches a forgotten EF migration. Without it, a later chunk can add or
//  change an entity, the model and the database drift apart, and every chunk after it debugs
//  mysterious "no such column" failures instead of seeing one red test with the fix in its message.
//  CLAUDE.md (Conventions) and technical-design §5.3 require it for every chunk; a cross-check in
//  ProspectStudio.Mcp.Tests/MigrationGuardPresenceTests.cs fails if this file goes missing.
//
//  If a test here is red, the fix is to add the migration - never to change the assertion.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ProspectStudio.Infrastructure.Tests.Infrastructure;
using Shouldly;

namespace ProspectStudio.Infrastructure.Tests.Storage;

public partial class MigrationDriftGuardTests
{
    private const string AddMigrationCommand =
        "dotnet ef migrations add C<N>_<Description> "
        + "--project src/ProspectStudio.Infrastructure --startup-project src/ProspectStudio.Mcp";

    [GeneratedRegex(@"^\d{14}_C\d+[a-z]?_[A-Za-z0-9]+$")]
    private static partial Regex MigrationId();

    [Fact]
    public async Task The_model_has_no_pending_changes_so_no_migration_is_missing()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        await using var context = await database.CreateContextAsync(timeout.Token);

        context.Database.HasPendingModelChanges().ShouldBeFalse(
            $"""
             The EF model no longer matches the last migration's snapshot, so the database schema is
             out of date and every later chunk will fail against it.

             Fix it by adding the missing migration - do not change or delete this test:

                 {AddMigrationCommand}

             Review the generated Up/Down before committing, and never edit a migration that is
             already committed (CLAUDE.md, technical-design §5.3).
             """);
    }

    [Fact]
    public async Task Every_migration_is_named_C_number_Description()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        await using var context = await database.CreateContextAsync(timeout.Token);

        var migrations = context.Database.GetMigrations().ToList();

        migrations.ShouldNotBeEmpty(
            $"""
             There are no migrations at all, so the tables from technical-design §5.2 cannot exist.
             Create the first one:

                 {AddMigrationCommand}
             """);

        var offenders = migrations.Where(id => !MigrationId().IsMatch(id)).ToList();
        offenders.ShouldBeEmpty(
            "every migration must be named C<N>_<Description> (CLAUDE.md), e.g. C1_Campaigns, so the "
            + "migration history reads as the chunk order.");
    }
}
