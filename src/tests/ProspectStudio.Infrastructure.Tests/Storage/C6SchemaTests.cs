using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ProspectStudio.Core.Domain;
using ProspectStudio.Infrastructure.Storage;
using ProspectStudio.Infrastructure.Tests.Infrastructure;
using Shouldly;

namespace ProspectStudio.Infrastructure.Tests.Storage;

/// <summary>
/// The two tables chunk C6 adds and the <c>leads</c> columns it fills in (technical-design §5.2),
/// asserted against the EF model rather than SQLite's catalogue so the test stays provider-neutral
/// (CLAUDE.md) and reads as the schema contract.
/// </summary>
/// <remarks>
/// C6 owns the migration for <c>research</c>, <c>signals</c> and the full <c>leads</c> column list.
/// <c>MigrationDriftGuardTests</c> catches a model with no migration behind it; this file catches the
/// opposite order - a migration that never arrived.
/// </remarks>
public class C6SchemaTests : IAsyncLifetime
{
    private readonly TempDirectory _directory = new();
    private TempDatabase? _database;

    public async Task InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _database = TempDatabase.In(_directory.Path);

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
    [InlineData(nameof(Research), "research")]
    [InlineData(nameof(Signal), "signals")]
    public async Task The_table_exists_with_the_name_section_52_gives_it(string entity, string table)
    {
        await using var context = await OpenAsync();

        var mapped = context.Model.GetEntityTypes()
            .SingleOrDefault(type => string.Equals(type.ClrType.Name, entity, StringComparison.Ordinal));

        mapped.ShouldNotBeNull(
            $"{entity} is not in the EF model. Chunk C6 adds research and signals (technical-design "
            + "§5.2) with an IEntityTypeConfiguration each in Infrastructure/Storage, plus one migration "
            + "C6_<Description>. Mapped: "
            + string.Join(", ", context.Model.GetEntityTypes().Select(type => type.ClrType.Name).Order(StringComparer.Ordinal)));

        mapped.GetTableName().ShouldBe(table);
    }

    [Fact]
    public async Task One_research_document_per_lead()
    {
        await using var context = await OpenAsync();

        var key = Entity<Research>(context).FindPrimaryKey();

        key.ShouldNotBeNull("research needs a primary key.");
        key.Properties.Select(property => property.Name).ShouldBe(
            [nameof(Research.CampaignId), nameof(Research.LeadId)],
            ignoreOrder: true,
            "technical-design §5.2 keys research on (campaign_id, lead_id). save_research replaces the "
            + "document rather than appending, so two rows for one lead would make 'the' research "
            + "ambiguous and the re-score non-deterministic. Found: "
            + string.Join(", ", key.Properties.Select(property => property.Name)));
    }

    [Theory]
    [InlineData(nameof(Research))]
    [InlineData(nameof(Signal))]
    public async Task Research_and_signals_belong_to_a_lead(string entity)
    {
        await using var context = await OpenAsync();

        var mapped = context.Model.GetEntityTypes()
            .SingleOrDefault(type => string.Equals(type.ClrType.Name, entity, StringComparison.Ordinal))
            ?? throw new Xunit.Sdk.XunitException($"{entity} is not in the EF model; chunk C6 adds it.");

        mapped.GetForeignKeys()
            .Select(key => key.PrincipalEntityType.ClrType.Name)
            .ShouldContain(
                nameof(Lead),
                $"{entity} hangs off (campaign_id, lead_id), which is leads' composite key. Without the "
                + "foreign key a research document can outlive the lead it describes, and get_lead would "
                + "join to nothing. Foreign keys found: "
                + string.Join(", ", mapped.GetForeignKeys().Select(key => key.PrincipalEntityType.ClrType.Name)));
    }

    [Fact]
    public async Task A_signals_date_stays_text_because_a_month_is_not_a_date()
    {
        // poc/schemas/research.schema.json admits YYYY-MM as well as YYYY-MM-DD, and no date type can
        // hold a month without inventing a day. This is a deliberate exception to CLAUDE.md's "let EF map
        // DateTimeOffset" rule, not a mapping to tighten later: §7.6's twelve-month window is applied in
        // the scorer at whole-month granularity, never as a SQL comparison on this column.
        await using var context = await OpenAsync();

        var date = Entity<Signal>(context).FindProperty(nameof(Signal.Date));

        date.ShouldNotBeNull();
        date.ClrType.ShouldBe(
            typeof(string),
            "turning signals.date into a DateTime forces a day onto a month-only signal, and makes "
            + "recency a lexicographic string comparison in SQL - exactly the SQLite-flavoured reasoning "
            + "the provider-neutrality rule exists to stop.");
    }

    [Theory]
    [InlineData("FeaturesJson", "features_json")]
    [InlineData("Score", "score")]
    [InlineData("Tier", "tier")]
    [InlineData("ScoreBreakdownJson", "score_breakdown_json")]
    [InlineData("ResearchStatus", "research_status")]
    [InlineData("Notes", "notes")]
    [InlineData("ContactName", "contact_name")]
    [InlineData("ContactTitle", "contact_title")]
    [InlineData("Cohort", "cohort")]
    public async Task The_leads_table_has_the_column_section_52_lists(string property, string column)
    {
        // C4 created leads with its identity and status columns and C5 added routing; C6 is the chunk
        // that completes §5.2's list. Named by string rather than by nameof so this file does not have to
        // be edited when the implementer adds the properties.
        await using var context = await OpenAsync();
        var lead = Entity<Lead>(context);

        var mapped = lead.FindProperty(property);

        mapped.ShouldNotBeNull(
            $"technical-design §5.2's leads row lists '{column}'. Properties present: "
            + string.Join(", ", lead.GetProperties().Select(each => each.Name).Order(StringComparer.Ordinal)));
    }

    [Fact]
    public async Task A_score_and_a_tier_are_null_until_the_lead_has_been_scored()
    {
        // find_candidates stores leads before score_leads has run. A non-nullable score would default
        // every unscored lead to 0, which reads as "scored, and terrible" - and save_research's delta is
        // specified as null, not 0, for exactly that lead (mcp-tools.md §save_research).
        await using var context = await OpenAsync();
        var lead = Entity<Lead>(context);

        foreach (var property in new[] { "Score", "Tier", "ScoreBreakdownJson" })
        {
            var mapped = lead.FindProperty(property)
                ?? throw new Xunit.Sdk.XunitException($"leads has no '{property}'; chunk C6 adds it.");

            mapped.IsNullable.ShouldBeTrue(
                $"'{property}' has to distinguish 'not scored yet' from a real value.");
        }
    }

    [Fact]
    public async Task The_campaign_remembers_the_weights_its_last_scoring_run_used()
    {
        // technical-design §5.2 adds campaigns.scoring_weights_json, "the effective weights of the last
        // score_leads run, so save_research re-scores on the same scale". Without the column, an override
        // run followed by one save_research leaves that lead measured differently from every other lead in
        // the list - and the list still sorts by score, so it simply appears in the wrong place.
        await using var context = await OpenAsync();
        var campaign = Entity<Campaign>(context);

        campaign.FindProperty("ScoringWeightsJson").ShouldNotBeNull(
            "technical-design §5.2's campaigns row lists scoring_weights_json. C6 owns the migration and it "
            + "is not yet committed, so extend C6_LeadsScoring rather than adding a second one. Properties "
            + "present: "
            + string.Join(", ", campaign.GetProperties().Select(each => each.Name).Order(StringComparer.Ordinal)));

        campaign.FindProperty("ScoringWeightsJson")!.IsNullable.ShouldBeTrue(
            "a campaign that has never been scored has no weights to remember, and an invented default "
            + "would claim a scale nobody chose.");
    }

    [Fact]
    public async Task Leads_can_be_sorted_and_filtered_by_score_without_a_table_scan()
    {
        // list_leads' default sort is score_desc over every lead in the campaign (mcp-tools.md
        // §list_leads), and a Houston run is thousands of rows.
        await using var context = await OpenAsync();
        var lead = Entity<Lead>(context);

        lead.GetIndexes().ShouldContain(
            index => index.Properties.Any(property => property.Name == "Score"),
            "Indexes found: "
            + string.Join(
                ", ",
                lead.GetIndexes().Select(index => $"[{string.Join(",", index.Properties.Select(property => property.Name))}]")));
    }

    [Fact]
    public async Task A_migration_named_for_chunk_C6_exists()
    {
        await using var context = await OpenAsync();

        var migrations = context.Database.GetMigrations().ToList();

        migrations.ShouldContain(
            id => id.Contains("_C6_", StringComparison.Ordinal),
            "C6 owns the migration for research, signals and the full leads column list. Add it with:\n\n"
            + "    dotnet ef migrations add C6_LeadsAndResearch --project src/ProspectStudio.Infrastructure "
            + "--startup-project src/ProspectStudio.Mcp\n\n"
            + $"Migrations present: {string.Join(", ", migrations)}");
    }

    private static IEntityType Entity<TEntity>(ProspectDbContext context)
        where TEntity : class =>
        context.Model.FindEntityType(typeof(TEntity))
            ?? throw new Xunit.Sdk.XunitException(
                $"{typeof(TEntity).Name} is not in the EF model; chunk C6 adds it (technical-design §5.2).");

    private async Task<ProspectDbContext> OpenAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var database = _database ?? throw new InvalidOperationException("InitializeAsync did not run.");
        return await database.CreateContextAsync(timeout.Token);
    }
}
