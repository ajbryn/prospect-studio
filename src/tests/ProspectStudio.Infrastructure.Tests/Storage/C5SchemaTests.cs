using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ProspectStudio.Core.Domain;
using ProspectStudio.Infrastructure.Storage;
using ProspectStudio.Infrastructure.Tests.Infrastructure;
using Shouldly;

namespace ProspectStudio.Infrastructure.Tests.Storage;

/// <summary>
/// The four tables chunk C5 adds (technical-design §5.2), asserted against the EF model rather than
/// SQLite's catalogue so the test stays provider-neutral (CLAUDE.md) and reads as the schema contract.
/// </summary>
/// <remarks>
/// C5 owns the only migration of its wave. <c>MigrationDriftGuardTests</c> catches a model with no
/// migration behind it; this file catches the opposite order - a migration that never arrived.
/// </remarks>
public class C5SchemaTests : IAsyncLifetime
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
    [InlineData(nameof(Dealer), "dealers")]
    [InlineData(nameof(DealerBranch), "dealer_branches")]
    [InlineData(nameof(Territory), "territories")]
    [InlineData(nameof(SuppressionRow), "suppression")]
    public async Task The_table_exists_with_the_name_section_52_gives_it(string entity, string table)
    {
        await using var context = await OpenAsync();

        var mapped = context.Model.GetEntityTypes()
            .SingleOrDefault(type => string.Equals(type.ClrType.Name, entity, StringComparison.Ordinal));

        mapped.ShouldNotBeNull(
            $"{entity} is not in the EF model. Chunk C5 adds dealers, dealer_branches, territories and "
            + "suppression (technical-design §5.2) with an IEntityTypeConfiguration each in "
            + "Infrastructure/Storage, plus one migration C5_<Description>. Mapped: "
            + string.Join(", ", context.Model.GetEntityTypes().Select(type => type.ClrType.Name).Order(StringComparer.Ordinal)));

        mapped.GetTableName().ShouldBe(table);
    }

    [Fact]
    public async Task A_branch_and_a_territory_belong_to_a_dealer()
    {
        await using var context = await OpenAsync();

        Entity<DealerBranch>(context).GetForeignKeys()
            .Select(key => key.PrincipalEntityType.ClrType.Name)
            .ShouldContain(
                nameof(Dealer),
                "dealer_branches.dealer_id references dealers; an orphan branch would route leads to a "
                + "dealer who does not exist.");

        Entity<Territory>(context).GetForeignKeys()
            .Select(key => key.PrincipalEntityType.ClrType.Name)
            .ShouldContain(
                nameof(Dealer),
                "territories.dealer_id references dealers. It is also what makes import_list's "
                + "'Unknown dealerId' error a rule rather than a courtesy.");
    }

    [Fact]
    public async Task Suppression_matching_happens_on_normalized_columns()
    {
        await using var context = await OpenAsync();

        var properties = Entity<SuppressionRow>(context).GetProperties()
            .Select(property => property.Name)
            .ToList();

        properties.ShouldContain(
            nameof(SuppressionRow.NameNorm),
            "technical-design §5.2 gives suppression a name_norm column, and CLAUDE.md says to match on "
            + "the normalized columns rather than on SQLite's case-sensitive text comparison. §7.3's "
            + "exact and fuzzy rules both compare it.");
        properties.ShouldContain(
            nameof(SuppressionRow.AddressNorm),
            "§5.2 lists address_norm too; C13's matchback compares it.");
        properties.ShouldContain(
            nameof(SuppressionRow.SourceFile),
            "§5.2: source_file, so a row that suppressed the wrong lead can be traced to the list it "
            + "came from.");
    }

    [Fact]
    public async Task A_lead_records_which_suppression_row_matched_it()
    {
        // technical-design §7.3: "Store the reason AND the matching row id." §5.2's leads column list
        // names suppression_reason only, which is the gap C5 fills - see the decisions note. A reason on
        // its own cannot answer "which list did this come from?", and with four reasons and seven rows
        // in the fixture alone that is the first question asked.
        await using var context = await OpenAsync();

        Entity<Lead>(context).GetProperties()
            .Select(property => property.Name)
            .ShouldContain(
                "SuppressionId",
                "leads needs a column for the matching suppression row (§7.3). Properties present: "
                + string.Join(", ", Entity<Lead>(context).GetProperties().Select(property => property.Name)));
    }

    [Fact]
    public async Task The_territory_lookup_columns_are_indexed_so_assignment_is_not_a_table_scan()
    {
        // assign_dealers runs §7.4 for every lead in the campaign, looking rules up by level and code.
        await using var context = await OpenAsync();
        var territory = Entity<Territory>(context);

        territory.GetIndexes().ShouldContain(
            index => index.Properties.Any(property => property.Name == nameof(Territory.Code)),
            "technical-design §7.4 looks territories up by code. Indexes found: "
            + string.Join(", ", territory.GetIndexes().Select(index => $"[{string.Join(",", index.Properties.Select(property => property.Name))}]")));
    }

    [Fact]
    public async Task A_migration_named_for_chunk_C5_exists()
    {
        await using var context = await OpenAsync();

        var migrations = context.Database.GetMigrations().ToList();

        migrations.ShouldContain(
            id => id.Contains("_C5_", StringComparison.Ordinal),
            "C5 owns the only migration of its wave. Add it with:\n\n"
            + "    dotnet ef migrations add C5_DealersAndSuppression --project src/ProspectStudio.Infrastructure "
            + "--startup-project src/ProspectStudio.Mcp\n\n"
            + $"Migrations present: {string.Join(", ", migrations)}");
    }

    private static IEntityType Entity<TEntity>(ProspectDbContext context)
        where TEntity : class =>
        context.Model.FindEntityType(typeof(TEntity))
            ?? throw new Xunit.Sdk.XunitException(
                $"{typeof(TEntity).Name} is not in the EF model; chunk C5 adds it (technical-design §5.2).");

    private async Task<ProspectDbContext> OpenAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var database = _database ?? throw new InvalidOperationException("InitializeAsync did not run.");
        return await database.CreateContextAsync(timeout.Token);
    }
}
