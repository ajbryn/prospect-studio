using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ProspectStudio.Core.Domain;
using ProspectStudio.Infrastructure.Storage;
using ProspectStudio.Infrastructure.Tests.Infrastructure;
using Shouldly;

namespace ProspectStudio.Infrastructure.Tests.Storage;

/// <summary>
/// The four tables chunk C4 adds (technical-design §5.2), asserted against the EF model rather than
/// against SQLite's catalogue, so the test stays provider-neutral (CLAUDE.md) and reads as the schema
/// contract rather than as a SQLite feature test.
/// </summary>
/// <remarks>
/// C4 owns the only migration of its wave. The existing drift guard in
/// <c>MigrationDriftGuardTests</c> catches a model with no migration behind it; this file catches the
/// opposite order - a migration that never arrived at all.
/// </remarks>
public class C4SchemaTests : IAsyncLifetime
{
    private readonly TempDirectory _directory = new();
    private TempDatabase? _database;

    public async Task InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _database = TempDatabase.In(_directory.Path);

        // Migrate once for the whole class: every assertion below reads model metadata, but applying
        // the migrations first means a broken migration shows up here rather than as odd metadata.
        await using var context = await _database.MigrateAsync(timeout.Token);
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        _directory.Dispose();
    }

    [Theory]
    [InlineData(nameof(Company), "companies")]
    [InlineData(nameof(Site), "sites")]
    [InlineData(nameof(SourceRecord), "source_records")]
    [InlineData(nameof(Lead), "leads")]
    public async Task The_table_exists_with_the_name_section_52_gives_it(string entity, string table)
    {
        await using var context = await OpenAsync();

        var mapped = context.Model.GetEntityTypes()
            .SingleOrDefault(type => string.Equals(type.ClrType.Name, entity, StringComparison.Ordinal));

        mapped.ShouldNotBeNull(
            $"{entity} is not in the EF model. Chunk C4 adds companies, sites, source_records and "
            + "leads (technical-design §5.2) with an IEntityTypeConfiguration each in "
            + "Infrastructure/Storage, plus one migration C4_<Description>. Mapped: "
            + string.Join(", ", context.Model.GetEntityTypes().Select(type => type.ClrType.Name).Order(StringComparer.Ordinal)));

        mapped.GetTableName().ShouldBe(table);
    }

    [Fact]
    public async Task The_overture_id_is_unique_so_a_re_run_cannot_duplicate_a_site()
    {
        await using var context = await OpenAsync();
        var site = Entity<Site>(context);

        var unique = site.GetIndexes().Where(index => index.IsUnique).ToList();

        unique.ShouldContain(
            index => index.Properties.Count == 1 && index.Properties[0].Name == nameof(Site.OvertureId),
            "technical-design §5.2 marks sites.overture_id unique. It is what makes find_candidates "
            + "idempotent (NFR-3): without it a re-run inserts every site again and every count "
            + "doubles. Indexes found: "
            + string.Join(", ", site.GetIndexes().Select(index => $"[{string.Join(",", index.Properties.Select(property => property.Name))}] unique={index.IsUnique}")));
    }

    [Fact]
    public async Task A_lead_is_keyed_by_campaign_and_lead_id()
    {
        await using var context = await OpenAsync();

        Entity<Lead>(context).FindPrimaryKey().ShouldNotBeNull()
            .Properties.Select(property => property.Name).ToList()
            .ShouldBe(
                [nameof(Lead.CampaignId), nameof(Lead.Id)],
                "technical-design §5.3: 'Lead has a composite key (CampaignId, Id)'. Lead ids are "
                + "L0001 per campaign, so Id alone is not unique across campaigns.");
    }

    [Fact]
    public async Task A_lead_belongs_to_a_campaign_and_a_site()
    {
        await using var context = await OpenAsync();
        var lead = Entity<Lead>(context);

        var targets = lead.GetForeignKeys()
            .Select(key => key.PrincipalEntityType.ClrType.Name)
            .ToList();

        targets.ShouldContain(nameof(Campaign), $"leads.campaign_id references campaigns. Found: {string.Join(", ", targets)}");
        targets.ShouldContain(nameof(Site), $"leads.site_id references sites. Found: {string.Join(", ", targets)}");
    }

    [Fact]
    public async Task A_site_belongs_to_a_company_and_a_source_record_to_a_site()
    {
        await using var context = await OpenAsync();

        Entity<Site>(context).GetForeignKeys()
            .Select(key => key.PrincipalEntityType.ClrType.Name)
            .ShouldContain(nameof(Company));

        Entity<SourceRecord>(context).GetForeignKeys()
            .Select(key => key.PrincipalEntityType.ClrType.Name)
            .ShouldContain(nameof(Site));
    }

    [Fact]
    public async Task Names_are_matched_on_normalized_columns_not_on_raw_text()
    {
        await using var context = await OpenAsync();

        Entity<Company>(context).GetProperties()
            .Select(property => property.Name)
            .ShouldContain(
                nameof(Company.NameNorm),
                "CLAUDE.md: 'don't depend on SQLite's case-sensitive text comparison (match on the "
                + "normalized columns such as name_norm)'. Dedupe, suppression and matchback all "
                + "compare this column.");
    }

    [Fact]
    public async Task A_migration_named_for_chunk_C4_exists()
    {
        await using var context = await OpenAsync();

        var migrations = context.Database.GetMigrations().ToList();

        migrations.ShouldContain(
            id => id.Contains("_C4_", StringComparison.Ordinal),
            "C4 owns the only migration of its wave, so exactly one must appear here. Add it with:\n\n"
            + "    dotnet ef migrations add C4_SitesAndLeads --project src/ProspectStudio.Infrastructure "
            + "--startup-project src/ProspectStudio.Mcp\n\n"
            + $"Migrations present: {string.Join(", ", migrations)}");
    }

    [Fact]
    public async Task Json_documents_stay_opaque_text()
    {
        // CLAUDE.md: "JSON documents stay opaque TEXT (never filtered inside)". source_records.payload_json
        // is the raw Overture row; a provider-specific JSON column type would not survive the move to
        // SQL Server or PostgreSQL.
        await using var context = await OpenAsync();

        var payload = Entity<SourceRecord>(context).GetProperties()
            .SingleOrDefault(property => property.Name == nameof(SourceRecord.PayloadJson))
            .ShouldNotBeNull();

        payload.ClrType.ShouldBe(typeof(string));
    }

    private static IEntityType Entity<TEntity>(ProspectDbContext context)
        where TEntity : class =>
        context.Model.FindEntityType(typeof(TEntity))
            ?? throw new Xunit.Sdk.XunitException(
                $"{typeof(TEntity).Name} is not in the EF model; chunk C4 adds it (technical-design §5.2).");

    private async Task<ProspectDbContext> OpenAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var database = _database ?? throw new InvalidOperationException("InitializeAsync did not run.");
        return await database.CreateContextAsync(timeout.Token);
    }
}
