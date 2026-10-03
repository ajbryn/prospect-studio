using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProspectStudio.Core.Campaigns;
using ProspectStudio.Core.Candidates;
using ProspectStudio.Core.Domain;
using ProspectStudio.Infrastructure.Storage;
using ProspectStudio.Infrastructure.Tests.Infrastructure;
using Shouldly;
using Xunit.Sdk;

namespace ProspectStudio.Infrastructure.Tests.Places;

/// <summary>
/// Storing candidates: the <c>companies</c>, <c>sites</c>, <c>source_records</c> and <c>leads</c>
/// tables (technical-design §5.2), written in batches (§5.3). Real SQLite in a temp file through the
/// production registration - never the EF InMemory provider (CLAUDE.md).
/// </summary>
public class CandidateStoreTests
{
    private const string CampaignId = "cmp_TEST01";

    /// <summary>The figure technical-design §5.3 names for the bulk-write target.</summary>
    private const int Candidates = 5000;

    /// <summary>Rows <see cref="Candidates"/> candidates produce: one per candidate in four tables.</summary>
    private const int Rows = Candidates * 4;

    /// <summary>
    /// The ceiling on <c>SaveChanges</c> calls for <see cref="Rows"/> rows. Batches of about 500 give
    /// roughly forty; a smaller internal batch would give more. Two hundred leaves room for either
    /// while staying a hundred times below the one-per-row figure, which is the only distinction that
    /// matters.
    /// </summary>
    private const int MaxSaveChanges = 200;

    [Fact]
    public async Task Storing_a_group_creates_a_company_a_site_a_source_record_and_a_lead()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var store = await PrepareAsync(database, timeout.Token);

        var result = await store.StoreAsync(CampaignId, [Group("fx_0001", 0.95)], timeout.Token);

        result.Companies.ShouldBe(1);
        result.Sites.ShouldBe(1);
        result.SourceRecords.ShouldBe(1, "provenance is per site (§5.2), not per campaign.");
        result.Leads.ShouldBe(1);
        result.NewLeads.ShouldBe(1);
        result.Duplicates.ShouldBe(0);

        var leads = await store.ListLeadsAsync(CampaignId, timeout.Token);
        var lead = leads.ShouldHaveSingleItem();
        lead.LeadId.ShouldBe("L0001", "leads are numbered per campaign (CLAUDE.md §Conventions).");
        lead.Status.ShouldBe(LeadStatuses.Candidate);
        lead.OvertureId.ShouldBe("fx_0001");
    }

    [Fact]
    public async Task Lead_ids_run_in_order_from_L0001()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var store = await PrepareAsync(database, timeout.Token);

        await store.StoreAsync(
            CampaignId,
            [Group("fx_0001", 0.95), Group("fx_0002", 0.90), Group("fx_0003", 0.92)],
            timeout.Token);

        var leads = await store.ListLeadsAsync(CampaignId, timeout.Token);

        leads.Select(lead => lead.LeadId).Order(StringComparer.Ordinal).ToList()
            .ShouldBe(["L0001", "L0002", "L0003"]);
    }

    [Fact]
    public async Task A_duplicate_is_stored_as_its_own_site_and_lead_marked_duplicate()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var store = await PrepareAsync(database, timeout.Token);

        var group = new CandidateGroup(
            Site("fx_0007", 0.90),
            [Site("fx_0014", 0.69)],
            DedupeRules.Fuzzy);

        var result = await store.StoreAsync(CampaignId, [group], timeout.Token);

        result.Companies.ShouldBe(1, "a duplicate is the same company, which is the point of merging.");
        result.Sites.ShouldBe(2, "both places are real places and both are kept.");
        result.SourceRecords.ShouldBe(
            2,
            "§7.2: 'Mark the others duplicate and keep their source records.'");
        result.Duplicates.ShouldBe(1);

        var leads = await store.ListLeadsAsync(CampaignId, timeout.Token);
        leads.Count.ShouldBe(2);
        leads.Single(lead => lead.OvertureId == "fx_0007").Status.ShouldBe(LeadStatuses.Candidate);
        leads.Single(lead => lead.OvertureId == "fx_0014").Status.ShouldBe(
            LeadStatuses.Duplicate,
            "the absorbed record stays visible as a duplicate rather than disappearing.");
    }

    [Fact]
    public async Task Storing_the_same_groups_again_adds_nothing()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var store = await PrepareAsync(database, timeout.Token);

        var groups = new[] { Group("fx_0001", 0.95), Group("fx_0002", 0.90), Group("fx_0003", 0.92) };

        var first = await store.StoreAsync(CampaignId, groups, timeout.Token);
        var second = await store.StoreAsync(CampaignId, groups, timeout.Token);

        second.NewLeads.ShouldBe(
            0,
            "NFR-3: re-running find_candidates with the same inputs creates no new rows. The Overture id "
            + "is the identity, so a second pass must recognise it.");
        second.Leads.ShouldBe(first.Leads, "the campaign still has the same leads, not duplicates of them.");

        // Companies, Sites and SourceRecords count rows WRITTEN, not rows seen, so a re-run reports
        // zero. Reporting the input size would make an idempotent re-run read exactly like the first
        // pass - the summary would say it stored three companies having stored none.
        second.Companies.ShouldBe(0, $"nothing was written the second time: {second}");
        second.Sites.ShouldBe(0, $"nothing was written the second time: {second}");
        second.SourceRecords.ShouldBe(0, $"nothing was written the second time: {second}");

        await using var context = await database.CreateContextAsync(timeout.Token);
        (await CountAsync<Company>(context, "companies", timeout.Token)).ShouldBe(3);
        (await CountAsync<Site>(context, "sites", timeout.Token)).ShouldBe(3);
        (await CountAsync<SourceRecord>(context, "source_records", timeout.Token)).ShouldBe(3);
        (await CountAsync<Lead>(context, "leads", timeout.Token)).ShouldBe(3);
    }

    [Fact]
    public async Task Re_opening_the_database_keeps_the_candidates()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var directory = new TempDirectory();

        await using (var database = TempDatabase.In(directory.Path))
        {
            var store = await PrepareAsync(database, timeout.Token);
            await store.StoreAsync(CampaignId, [Group("fx_0001", 0.95)], timeout.Token);
        }

        await using var reopened = TempDatabase.In(directory.Path);
        (await reopened.MigrateAsync(timeout.Token)).Dispose();

        var leads = await Store(reopened).ListLeadsAsync(CampaignId, timeout.Token);
        leads.ShouldHaveSingleItem().OvertureId.ShouldBe("fx_0001");
    }

    [Fact]
    public async Task Clearing_candidates_without_research_empties_the_campaign()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var store = await PrepareAsync(database, timeout.Token);

        await store.StoreAsync(CampaignId, [Group("fx_0001", 0.95), Group("fx_0002", 0.90)], timeout.Token);

        var cleared = await store.ClearCandidatesWithoutResearchAsync(CampaignId, timeout.Token);

        cleared.ShouldBe(2, "mcp-tools.md §find_candidates: 'replace: true clears candidates without research first'.");
        (await store.ListLeadsAsync(CampaignId, timeout.Token)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Five_thousand_candidates_are_written_in_batches_not_one_row_at_a_time()
    {
        // technical-design §5.3 "Bulk writes": batches of about 500 through SaveChangesAsync with
        // change tracking off and a fresh context per batch. What that rule exists to prevent is a
        // SaveChangesAsync per row.
        //
        // The guard counts SaveChanges calls, which is the thing §5.3 actually prescribes: 20,000 rows
        // at 500 a batch is on the order of forty calls, while one per row is 20,000. Two instruments
        // were tried first and both rejections are worth keeping written down.
        //
        // A wall-clock threshold of ten seconds was the original. It never measured what it guarded - a
        // per-row write of 20,000 rows takes minutes, not eleven seconds - and it moved with machine
        // load, passing at 7-9 s and failing at 10-11.5 s on the same build under a parallel dotnet
        // test run.
        //
        // Command count cannot work either, which is less obvious. EF Core's SQLite provider does not
        // batch inserts into multi-statement commands: a probe over one four-column table with
        // AddRange plus SaveChangesAsync every 500 rows sent 5,000 single-row INSERTs for 5,000 rows,
        // and a SaveChangesAsync per row sends the same number - measured at 20,001 commands for
        // 20,000 rows in both shapes. The only shape reaching a few dozen commands is hand-written
        // multi-row INSERT ... VALUES (...),(...), measured at 439 ms against EF's 488 ms per 5,000
        // rows: a ten percent gain for provider-specific SQL on the highest-volume write path, which
        // is not a trade CLAUDE.md's portability rules would take.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var directory = new TempDirectory();

        var commands = new CommandCounter();
        var saves = new SaveChangesCounter();
        await using var database = TempDatabase.In(directory.Path, commands, saves);
        var store = await PrepareAsync(database, timeout.Token);

        var groups = Enumerable.Range(1, Candidates)
            .Select(index => Group($"syn_{index:D5}", 0.6 + (index % 40 / 100.0), $"Synthetic Company {index}"))
            .ToList();

        var commandsBefore = commands.Commands;
        var savesBefore = saves.Calls;

        commandsBefore.ShouldBeGreaterThan(
            0,
            "the interceptors saw nothing even while migrating, so they are not attached and this test "
            + "would pass whatever the store did. TempDatabase appends them to the production "
            + "registration through IDbContextOptionsConfiguration.");

        var stopwatch = Stopwatch.StartNew();
        var result = await store.StoreAsync(CampaignId, groups, timeout.Token);
        stopwatch.Stop();

        result.NewLeads.ShouldBe(Candidates);

        var written = commands.Commands - commandsBefore;
        var saveCalls = saves.Calls - savesBefore;

        saveCalls.ShouldBeInRange(
            1,
            MaxSaveChanges,
            $"""
             Writing {Candidates:N0} candidates - {Rows:N0} rows across companies, sites, source_records
             and leads - took {saveCalls:N0} SaveChanges calls and {written:N0} commands.

             technical-design §5.3 asks for batches of about 500 rows per SaveChangesAsync, which is
             around forty calls. A SaveChangesAsync per row would be {Rows:N0}.

             First commands sent:
             {commands.Sample(4)}
             """);

        written.ShouldBeGreaterThanOrEqualTo(
            Rows,
            $"{Rows:N0} rows have to reach the database, and this provider sends one INSERT each; "
            + $"{written:N0} commands is too few for the rows the store reported writing.");

        written.ShouldBeLessThanOrEqualTo(
            Rows * 3 / 2,
            $"""
             {written:N0} commands for {Rows:N0} rows means something is querying per row on top of the
             inserts - an existence check inside the loop rather than one lookup up front, most likely.
             The store already reads the campaign's existing sites once, which is the right shape.

             First commands sent:
             {commands.Sample(4)}
             """);

        // A smoke bound only, deliberately far above anything a correct implementation needs, so a
        // pathological regression still trips something without this becoming a timing test again.
        stopwatch.Elapsed.ShouldBeLessThan(
            TimeSpan.FromSeconds(60),
            $"took {stopwatch.Elapsed.TotalSeconds:F1} s for {Candidates:N0} candidates in {saveCalls:N0} "
            + "SaveChanges calls, which is slow enough to be worth looking at even though the batching "
            + "is right.");
    }

    private static ICandidateStore Store(TempDatabase database) =>
        database.Services.GetService<ICandidateStore>()
        ?? throw new XunitException(
            "AddProspectStudioStorage does not register an ICandidateStore. Chunk C4 adds the "
            + "companies, sites, source_records and leads tables (one migration, C4_<Description>) and "
            + "the EF implementation of ProspectStudio.Core.Candidates.ICandidateStore.");

    /// <summary>Migrates, creates the campaign the leads hang off, and hands back the store.</summary>
    private static async Task<ICandidateStore> PrepareAsync(TempDatabase database, CancellationToken cancellationToken)
    {
        await using var context = await database.MigrateAsync(cancellationToken);

        var campaigns = database.Services.GetService<ICampaignStore>()
            ?? throw new XunitException("AddProspectStudioStorage must still register an ICampaignStore (C1).");

        var now = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        await campaigns.AddAsync(
            new Campaign
            {
                Id = CampaignId,
                Name = "Houston test",
                Slug = "houston test",
                FolderPath = Path.Combine(Path.GetTempPath(), "houston-test"),
                Status = CampaignStatuses.Draft,
                CreatedAt = now,
                UpdatedAt = now,
            },
            cancellationToken);

        return Store(database);
    }

    private static CandidateGroup Group(string overtureId, double confidence, string? name = null) =>
        new(Site(overtureId, confidence, name), [], DedupeRules.None);

    private static CandidateSite Site(string overtureId, double confidence, string? name = null) =>
        new(
            OvertureId: overtureId,
            Name: name ?? $"Company {overtureId}",
            NameNorm: (name ?? $"company {overtureId}").ToLowerInvariant(),
            Address: "1 Test Rd",
            City: "Houston",
            State: "TX",
            Zip: "77001",
            CountyFips: "48201",
            Lat: 29.76,
            Lon: -95.37,
            Phone: "7135550100",
            Website: $"https://{overtureId}.example",
            TaxonomyPrimary: "warehouse",
            TaxonomyPath: "services_and_business|b2b_service|warehouse",
            BasicCategory: "b2b_transportation_and_storage_service",
            Confidence: confidence,
            Release: "2026-09-23.1",
            PayloadJson: $"{{\"id\":\"{overtureId}\"}}");

    /// <summary>
    /// A row count through the EF model rather than raw SQL, so the assertion stays provider-neutral
    /// (CLAUDE.md) and still names the table technical-design §5.2 asks for.
    /// </summary>
    private static async Task<int> CountAsync<TEntity>(
        ProspectDbContext context,
        string table,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var entity = context.Model.FindEntityType(typeof(TEntity))
            ?? throw new XunitException(
                $"{typeof(TEntity).Name} is not in the EF model, so table '{table}' cannot exist. "
                + "Chunk C4 adds it (technical-design §5.2). Mapped tables: "
                + string.Join(", ", context.Model.GetEntityTypes().Select(type => type.GetTableName()).Order(StringComparer.Ordinal)));

        entity.GetTableName().ShouldBe(table, "technical-design §5.2 names the tables in snake_case.");

        return await context.Set<TEntity>().CountAsync(cancellationToken);
    }
}
