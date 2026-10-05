using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProspectStudio.Core.Reference;
using ProspectStudio.Infrastructure.Storage;
using ProspectStudio.Infrastructure.Workspace;
using ProspectStudio.Mcp.Tests.Infrastructure;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// The <c>setup</c> CLI verb (implementation-plan C2, POC-2). It is also where two things deferred from
/// C1 land: running migrations from <c>setup</c>, and the <c>--seed-fixtures</c> flag beside
/// <c>PS_SEED_FIXTURES=1</c>. Reference data <em>and</em> the Overture extract are pre-installed in
/// every test that exercises the steps, because a step that does not skip would download from
/// census.gov or pull the 205 MB Overture extract from S3 - which no test may do (CLAUDE.md).
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
    public async Task Setup_skips_an_Overture_extract_that_is_already_there()
    {
        using var timeout = TestTimeout.Start(180);
        using var workspace = new TempWorkspace();
        ReferenceDataFixture.Install(EnsureData(workspace));

        var before = PlacesDataFixture.Fingerprint(workspace.Data);
        before.ShouldNotBeEmpty();

        var run = await ServerCli.RunAsync(
            workspace.Home,
            workspace.Data,
            ["setup", "--states", "TX"],
            cancellationToken: timeout.Token);

        run.ExitCode.ShouldBe(0, run.ToString());

        PlacesDataFixture.Fingerprint(workspace.Data).ShouldBe(
            before,
            "chunk C4 adds the Overture step (technical-design §6.1); a changed file means setup went "
            + $"to S3, which a test may not do; {run}");

        run.Stdout.ShouldContain(
            OvertureSteps.Places,
            Case.Insensitive,
            "the operator needs to see the Overture step reported - skipped here - rather than wonder "
            + $"whether it ran at all; {run}");
    }

    /// <summary>
    /// A data folder with every step's output already there, so <c>setup</c> skips all of them. From C4
    /// that includes the Overture extract, whose source is an S3 download.
    /// </summary>
    private static string EnsureData(TempWorkspace workspace)
    {
        Directory.CreateDirectory(workspace.Data);
        PlacesDataFixture.Install(workspace.Data);
        return workspace.Data;
    }
}
