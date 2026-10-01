using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProspectStudio.Infrastructure.Storage;
using ProspectStudio.Infrastructure.Workspace;
using ProspectStudio.Mcp.Tests.Infrastructure;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// The <c>setup</c> CLI verb (implementation-plan C2, POC-2). It is also where two things deferred from
/// C1 land: running migrations from <c>setup</c>, and the <c>--seed-fixtures</c> flag beside
/// <c>PS_SEED_FIXTURES=1</c>. Reference data is pre-installed in every test that exercises the steps,
/// because a step that does not skip would download from census.gov - which no test may do.
/// </summary>
public class SetupVerbTests
{
    [Fact]
    public async Task Setup_applies_migrations_and_creates_the_workspace_folders()
    {
        using var timeout = TestTimeout.Start(180);
        using var workspace = new TempWorkspace();
        ReferenceDataFixture.Install(EnsureData(workspace));

        var run = await ServerCli.RunAsync(workspace.Home, workspace.Data, ["setup"], cancellationToken: timeout.Token);

        run.ExitCode.ShouldBe(0, run.ToString());

        foreach (var folder in WorkspaceBootstrapper.FolderNames)
        {
            Directory.Exists(workspace.WorkspaceFolder(folder)).ShouldBeTrue(
                $"POC-3 requires '{folder}' under PROSPECT_STUDIO_HOME; {run}");
        }

        var databasePath = Path.Combine(workspace.Data, ServerJobs.DatabaseFileName);
        File.Exists(databasePath).ShouldBeTrue(
            $"implementation-plan C1 defers running migrations from 'setup' to C2; {run}");

        await using var services = new ServiceCollection()
            .AddProspectStudioStorage(databasePath)
            .BuildServiceProvider();

        await using (var context = await services
            .GetRequiredService<IDbContextFactory<ProspectDbContext>>()
            .CreateDbContextAsync(timeout.Token))
        {
            (await context.Database.GetPendingMigrationsAsync(timeout.Token)).ShouldBeEmpty(
                "'setup' runs Database.MigrateAsync (technical-design §5.3).");
        }

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Setup_skips_reference_data_that_is_already_present()
    {
        using var timeout = TestTimeout.Start(180);
        using var workspace = new TempWorkspace();
        ReferenceDataFixture.Install(EnsureData(workspace));

        var before = ReferenceDataFixture.Fingerprint(workspace.Data);

        var run = await ServerCli.RunAsync(
            workspace.Home,
            workspace.Data,
            ["setup", "--states", "TX"],
            cancellationToken: timeout.Token);

        run.ExitCode.ShouldBe(0, run.ToString());

        ReferenceDataFixture.Fingerprint(workspace.Data).ShouldBe(
            before,
            $"a second setup must skip the steps it has already done (POC-2); {run}");

        run.Stdout.ShouldContain(
            "skip",
            Case.Insensitive,
            $"the operator running 'setup' needs to see which steps were skipped; {run}");
    }

    [Fact]
    public async Task Setup_with_seed_fixtures_copies_the_brand_kit_into_an_empty_workspace()
    {
        using var timeout = TestTimeout.Start(180);
        using var workspace = new TempWorkspace();
        ReferenceDataFixture.Install(EnsureData(workspace));

        var run = await ServerCli.RunAsync(
            workspace.Home,
            workspace.Data,
            ["setup", "--seed-fixtures"],
            cancellationToken: timeout.Token);

        run.ExitCode.ShouldBe(0, run.ToString());

        File.Exists(workspace.WorkspaceFolder(WorkspaceBootstrapper.BrandKitFolder, "brand.json")).ShouldBeTrue(
            "--seed-fixtures is the flag implementation-plan C1 deferred until the setup verb existed; "
            + run);
    }

    [Fact]
    public async Task Setup_without_the_flag_leaves_the_brand_kit_empty()
    {
        using var timeout = TestTimeout.Start(180);
        using var workspace = new TempWorkspace();
        ReferenceDataFixture.Install(EnsureData(workspace));

        var run = await ServerCli.RunAsync(workspace.Home, workspace.Data, ["setup"], cancellationToken: timeout.Token);

        run.ExitCode.ShouldBe(0, run.ToString());

        Directory.EnumerateFileSystemEntries(workspace.WorkspaceFolder(WorkspaceBootstrapper.BrandKitFolder))
            .ShouldBeEmpty($"seeding is opt-in, so the user's brand kit is never written to by default; {run}");
    }

    [Fact]
    public async Task Setup_does_not_extract_Overture_yet()
    {
        using var timeout = TestTimeout.Start(180);
        using var workspace = new TempWorkspace();
        ReferenceDataFixture.Install(EnsureData(workspace));

        var run = await ServerCli.RunAsync(
            workspace.Home,
            workspace.Data,
            ["setup", "--states", "TX"],
            cancellationToken: timeout.Token);

        run.ExitCode.ShouldBe(0, run.ToString());

        var overture = Path.Combine(workspace.Data, "overture");
        if (Directory.Exists(overture))
        {
            Directory.EnumerateFileSystemEntries(overture).ShouldBeEmpty(
                $"the Overture extract is chunk C4; C2's setup covers reference data only; {run}");
        }
    }

    private static string EnsureData(TempWorkspace workspace)
    {
        Directory.CreateDirectory(workspace.Data);
        return workspace.Data;
    }
}
