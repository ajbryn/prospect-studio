using Microsoft.Extensions.DependencyInjection;
using ProspectStudio.Core.Dealers;
using ProspectStudio.Infrastructure.Tests.Infrastructure;
using ProspectStudio.Tests.Shared;
using Shouldly;
using Xunit.Sdk;

namespace ProspectStudio.Infrastructure.Tests.Dealers;

/// <summary>
/// <c>import_list</c> for the three business lists (mcp-tools.md §import_list): CSV and XLSX with the
/// same headers, row-level errors that do not stop the file, and a re-import that adds nothing. Real
/// SQLite in a temp file through the production registration - never the EF InMemory provider.
/// </summary>
public class ListImporterTests
{
    private static CancellationTokenSource Timeout() => new(TimeSpan.FromSeconds(30));

    [Theory]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task ImportList_Dealers_StoresThreeDealersAndTheirBranches(string format)
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var (importer, dealers) = await PrepareAsync(database, timeout.Token);

        var result = await importer.ImportAsync(
            ImportListKinds.Dealers,
            ListPath(format, "dealers"),
            reason: null,
            cancellationToken: timeout.Token);

        result.Imported.ShouldBe(3, $"dealers.csv holds three dealers. Errors: {Describe(result)}");
        result.Errors.ShouldBeEmpty(Describe(result));

        var counts = await dealers.CountListsAsync(timeout.Token);
        counts.Dealers.ShouldBe(3);
        counts.Branches.ShouldBe(3, "one branch each (poc/fixtures/README.md).");

        var branches = await dealers.GetBranchesAsync(timeout.Token);
        branches.ShouldContain(branch => branch.BranchId == "bay-pas");
        var pasadena = branches.Single(branch => branch.BranchId == "bay-pas");
        pasadena.DealerId.ShouldBe("bay");
        pasadena.Lat.ShouldBe(29.6911, 1e-6, "§7.4's nearest-branch tie-break measures from here, so the "
            + "coordinates have to survive the import rather than being parsed as text and dropped.");
        pasadena.Lon.ShouldBe(-95.2091, 1e-6);
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task ImportList_Territories_StoresEveryRowWithItsLevelCodeAndPriority(string format)
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var (importer, dealers) = await PrepareAsync(database, timeout.Token);

        await importer.ImportAsync(
            ImportListKinds.Dealers,
            ListPath(format, "dealers"),
            null,
            cancellationToken: timeout.Token);
        var result = await importer.ImportAsync(
            ImportListKinds.Territories,
            ListPath(format, "territories"),
            reason: null,
            cancellationToken: timeout.Token);

        result.Imported.ShouldBe(35, $"9 county defaults, 15 bay ZIPs and 11 pine ZIPs. {Describe(result)}");
        result.Errors.ShouldBeEmpty(Describe(result));

        var rules = await dealers.GetTerritoriesAsync(timeout.Token);
        rules.Count.ShouldBe(35);

        var harris = rules.Single(rule => rule.Level == TerritoryLevels.County && rule.Code == "48201");
        harris.DealerId.ShouldBe("gulf");
        harris.Priority.ShouldBe(2, "territories.csv gives Harris to gulf at priority 2, not 1.");

        rules.ShouldContain(
            rule => rule.Level == TerritoryLevels.Zip && rule.Code == "77506" && rule.DealerId == "bay",
            "the Pasadena override. A ZIP read as a number would arrive as 77506 here but 7030 for a "
            + "leading-zero ZIP, which is why the XLSX fixture writes every cell as text.");
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task ImportList_Suppression_NormalizesTheNameAndKeepsTheReasonAndSource(string format)
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var (importer, dealers) = await PrepareAsync(database, timeout.Token);

        var path = ListPath(format, "suppression");
        var result = await importer.ImportAsync(
            ImportListKinds.Suppression,
            path,
            reason: null,
            cancellationToken: timeout.Token);

        result.Imported.ShouldBe(7, Describe(result));
        result.Errors.ShouldBeEmpty(Describe(result));

        var rows = await dealers.GetSuppressionAsync(timeout.Token);
        rows.Count.ShouldBe(7);

        rows.ShouldContain(row => row.Reason == SuppressionReasons.Dnc);
        var dnc = rows.Single(row => row.Reason == SuppressionReasons.Dnc);
        dnc.NameNorm.ShouldBe(
            "northfield storage",
            "§7.1 strips the trailing 'Co', and §7.3 compares name_norm. Storing the raw name would make "
            + "the exact-name rule miss its only subject.");
        dnc.Domain.ShouldBeNull("the row's domain column is empty, and an empty string is not a domain.");
        dnc.Zip.ShouldBe("77060");

        rows.ShouldContain(
            row => row.NameNorm == "coastal crane and rigging",
            "'Coastal Crane & Rigging LLC' loses the '&' to 'and' and the trailing 'LLC' (§7.1).");

        rows.Select(row => row.Reason).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()
            .ShouldBe(["competitor", "customer", "dealer", "dnc"]);

        rows.ShouldAllBe(
            row => row.Id.Length > 0,
            "every row needs an id, because §7.3 stores the matching row id on the lead.");
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task ImportList_TerritoriesWithAnUnknownDealerId_ReportsTheRowNumberAndImportsTheRest(string format)
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var (importer, dealers) = await PrepareAsync(database, timeout.Token);

        await importer.ImportAsync(
            ImportListKinds.Dealers,
            ListPath(format, "dealers"),
            null,
            cancellationToken: timeout.Token);

        var result = await importer.ImportAsync(
            ImportListKinds.Territories,
            RepoFixtures.ListFixture($"territories_bad_dealer.{format}"),
            reason: null,
            cancellationToken: timeout.Token);

        var error = result.Errors.ShouldHaveSingleItem(
            $"one row names 'gulff'; the other four are fine. {Describe(result)}");

        error.Row.ShouldBe(
            4,
            "mcp-tools.md §import_list reports {row, message}, and the only number a user can act on is "
            + "the one they see in the file: the header is row 1, so the broken row is row 4. "
            + $"{Describe(result)}");
        error.Message.ShouldContain(
            "gulff",
            Case.Insensitive,
            $"the message has to name the value that was wrong, not just the column. {Describe(result)}");

        result.Imported.ShouldBe(4, $"the other four rows still import. {Describe(result)}");
        (await dealers.GetTerritoriesAsync(timeout.Token)).Count.ShouldBe(
            4,
            "a single bad row must not roll the whole file back - the marketer would have no way to tell "
            + "which rows were good.");
    }

    [Fact]
    public async Task ImportList_ReImportingTheSameFile_AddsNothing()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var (importer, dealers) = await PrepareAsync(database, timeout.Token);

        foreach (var (kind, file) in Lists)
        {
            await importer.ImportAsync(
                kind,
                RepoFixtures.Fixture($"{file}.csv"),
                null,
                cancellationToken: timeout.Token);
        }

        var before = await dealers.CountListsAsync(timeout.Token);

        foreach (var (kind, file) in Lists)
        {
            var again = await importer.ImportAsync(
                kind,
                RepoFixtures.Fixture($"{file}.csv"),
                null,
                cancellationToken: timeout.Token);

            again.Imported.ShouldBe(
                0,
                $"NFR-3: re-importing {file}.csv must create nothing. {Describe(again)}");
            again.Errors.ShouldBeEmpty(Describe(again));
            (again.Imported + again.Updated).ShouldBeGreaterThan(
                0,
                $"the rows were seen, just not created again. {Describe(again)}");
        }

        (await dealers.CountListsAsync(timeout.Token)).ShouldBe(
            before,
            "the same lists imported twice must leave the same number of rows. Doubling the territory "
            + "rows would double every byDealer count without failing anything else.");
    }

    [Fact]
    public async Task ImportList_AfterAnEditedFile_ReplacesTheRowRatherThanAddingASecond()
    {
        // The reason "idempotent" is not enough on its own: the marketer's workflow is to edit the CSV
        // and import it again, and a dealer who moved has to move rather than exist twice.
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var (importer, dealers) = await PrepareAsync(database, timeout.Token);

        await importer.ImportAsync(
            ImportListKinds.Dealers,
            RepoFixtures.DealersCsv,
            null,
            cancellationToken: timeout.Token);

        var edited = Path.Combine(directory.Path, "dealers-edited.csv");
        await File.WriteAllLinesAsync(
            edited,
            (await File.ReadAllLinesAsync(RepoFixtures.DealersCsv, timeout.Token))
                .Select(line => line.StartsWith("bay,", StringComparison.Ordinal)
                    ? line.Replace("Bayport Aerial Supply", "Bayport Aerial Supply Co", StringComparison.Ordinal)
                    : line),
            timeout.Token);

        var result = await importer.ImportAsync(
            ImportListKinds.Dealers,
            edited,
            null,
            cancellationToken: timeout.Token);

        result.Updated.ShouldBeGreaterThan(0, $"the edited row replaced the stored one. {Describe(result)}");
        (await dealers.CountListsAsync(timeout.Token)).Dealers.ShouldBe(3, "still three dealers, not six.");

        (await dealers.ListDealersAsync(timeout.Token))
            .Single(dealer => dealer.Id == "bay").Name
            .ShouldBe("Bayport Aerial Supply Co", "the new name won.");
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task ImportList_ATerritoryCodeThatLostALeadingZero_IsAReportedRowError(string format)
    {
        // The five-digit rule reaching the user: a damaged code has to come back as a row they can go
        // and fix, not as an exception that abandons the file and not as a stored row that silently
        // routes a whole ZIP to nobody.
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var (importer, dealers) = await PrepareAsync(database, timeout.Token);

        await importer.ImportAsync(
            ImportListKinds.Dealers,
            ListPath(format, "dealers"),
            null,
            cancellationToken: timeout.Token);

        var result = await importer.ImportAsync(
            ImportListKinds.Territories,
            RepoFixtures.ListFixture($"territories_bad_code.{format}"),
            reason: null,
            cancellationToken: timeout.Token);

        var error = result.Errors.ShouldHaveSingleItem($"only row 4's code is damaged. {Describe(result)}");

        error.Row.ShouldBe(4, $"the header is row 1. {Describe(result)}");
        error.Message.ShouldContain(
            "7494",
            Case.Insensitive,
            $"the message names the damaged value. {Describe(result)}");

        result.Imported.ShouldBe(4, $"the other four rows still import. {Describe(result)}");

        var rules = await dealers.GetTerritoriesAsync(timeout.Token);
        rules.Select(rule => rule.Code).ShouldNotContain(
            "7494",
            "a four-digit code must never reach the database. Stored, it matches no lead, so every "
            + "business in the real ZIP is reported as a coverage gap - indistinguishable from a "
            + "territory the dealer genuinely does not cover.");
        rules.ShouldContain(rule => rule.Code == "77507", "the good ZIP row after it still landed.");
    }

    [Fact]
    public async Task ImportList_ATerritoryListWhoseBranchIdsAreUpperCased_IsStillIdempotent()
    {
        // The territory id is a content hash of (dealer, branch, level, code) while the branch column is
        // stored lower-cased. Anything hashed in its raw case mints a second id for the same rule, so a
        // list somebody retyped would double: territoryRows climbs on every import and the duplicate
        // competes with the original in §7.4's tie-break.
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var (importer, dealers) = await PrepareAsync(database, timeout.Token);

        await importer.ImportAsync(
            ImportListKinds.Dealers,
            RepoFixtures.DealersCsv,
            null,
            cancellationToken: timeout.Token);
        await importer.ImportAsync(
            ImportListKinds.Territories,
            RepoFixtures.TerritoriesCsv,
            null,
            cancellationToken: timeout.Token);

        var again = await importer.ImportAsync(
            ImportListKinds.Territories,
            RepoFixtures.ListFixture("territories_mixed_case_branch.csv"),
            reason: null,
            cancellationToken: timeout.Token);

        again.Errors.ShouldBeEmpty(
            $"'BAY-PAS' is the same branch as 'bay-pas', so no row is unknown. {Describe(again)}");
        again.Imported.ShouldBe(
            0,
            $"the same 35 rules, typed in a different case. {Describe(again)}");

        (await dealers.GetTerritoriesAsync(timeout.Token)).Count.ShouldBe(35, "not 70.");

        var listed = await dealers.ListDealersAsync(timeout.Token);
        listed.Single(dealer => dealer.Id == "bay").TerritoryRows.ShouldBe(18, "still 18, not 36.");
        listed.Sum(dealer => dealer.TerritoryRows).ShouldBe(35);
    }

    [Fact]
    public async Task ImportList_SuppressionWithoutReplace_KeepsARowTheFileNoLongerNames()
    {
        // The decision recorded in C5: upsert is the default, so a row dropped from the file keeps
        // suppressing. A stale entry costs one lead; dropping a stale 'dnc' row risks contacting
        // somebody who asked not to be, so over-suppression is the safe direction. What is tested here
        // is that the safe direction is the one that actually happens - being upsert-only made this true
        // by accident rather than by decision, and a comment once claimed the opposite.
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var (importer, dealers) = await PrepareAsync(database, timeout.Token);

        await importer.ImportAsync(
            ImportListKinds.Suppression,
            RepoFixtures.SuppressionCsv,
            null,
            cancellationToken: timeout.Token);

        var shortened = await WithoutNorthfieldAsync(directory.Path, timeout.Token);
        var result = await importer.ImportAsync(
            ImportListKinds.Suppression,
            shortened,
            null,
            cancellationToken: timeout.Token);

        result.Removed.ShouldBe(
            0,
            $"nothing is deleted without being asked. {Describe(result)}");

        var rows = await dealers.GetSuppressionAsync(timeout.Token);
        rows.Count.ShouldBe(7, $"six in the new file plus the one it dropped. {Describe(result)}");
        rows.ShouldContain(
            row => row.NameNorm == "northfield storage",
            "the dropped row is still on the list and still suppressing.");
    }

    [Fact]
    public async Task ImportList_SuppressionWithReplace_DeletesTheRowsTheFileNoLongerNames()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var (importer, dealers) = await PrepareAsync(database, timeout.Token);

        await importer.ImportAsync(
            ImportListKinds.Suppression,
            RepoFixtures.SuppressionCsv,
            null,
            cancellationToken: timeout.Token);

        var shortened = await WithoutNorthfieldAsync(directory.Path, timeout.Token);
        var result = await importer.ImportAsync(
            ImportListKinds.Suppression,
            shortened,
            reason: null,
            cancellationToken: timeout.Token,
            replace: true);

        result.Removed.ShouldBe(
            1,
            "the file is authoritative now, so the row it no longer names goes - and the count has to be "
            + $"reported, because a shrinking suppression list is a change the user must notice. {Describe(result)}");
        result.Errors.ShouldBeEmpty(Describe(result));

        var rows = await dealers.GetSuppressionAsync(timeout.Token);
        rows.Count.ShouldBe(6, Describe(result));
        rows.ShouldNotContain(row => row.NameNorm == "northfield storage");
    }

    [Fact]
    public async Task ImportList_SuppressionWithReplace_AndNoChanges_RemovesNothing()
    {
        // replace is not "delete and re-add": re-importing the unchanged file must still report nothing
        // created and nothing removed, or every run would look like a change.
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var (importer, dealers) = await PrepareAsync(database, timeout.Token);

        await importer.ImportAsync(
            ImportListKinds.Suppression,
            RepoFixtures.SuppressionCsv,
            null,
            cancellationToken: timeout.Token);

        var result = await importer.ImportAsync(
            ImportListKinds.Suppression,
            RepoFixtures.SuppressionCsv,
            reason: null,
            cancellationToken: timeout.Token,
            replace: true);

        result.Removed.ShouldBe(0, Describe(result));
        result.Imported.ShouldBe(0, Describe(result));
        (await dealers.GetSuppressionAsync(timeout.Token)).Count.ShouldBe(7, Describe(result));
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("xlsx")]
    public async Task ImportList_Warranty_IsRejectedAsUnsupportedRatherThanAttempted(string format)
    {
        // mcp-tools.md §import_list advertises four kinds and C13 builds the fourth. Until then the
        // honest answer is UNSUPPORTED: anything else sends the user looking for a file problem that is
        // not there. A readable, well-formed file is passed deliberately, so the only thing that can
        // refuse it is the kind.
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var (importer, _) = await PrepareAsync(database, timeout.Token);

        ImportListKinds.IsKnown(ImportListKinds.Warranty).ShouldBeTrue(
            "warranty is a documented kind, which is why it cannot be rejected as an unknown one.");

        var thrown = await Should.ThrowAsync<ImportListUnsupportedException>(
            () => importer.ImportAsync(
                ImportListKinds.Warranty,
                ListPath(format, "dealers"),
                null,
                cancellationToken: timeout.Token),
            "mcp-tools.md §Errors: UNSUPPORTED is 'Feature not built yet'. The MCP layer maps this "
            + "exception to that code, which C5ToolContractTests pins at the boundary.");

        thrown.Hint.ShouldNotBeNullOrWhiteSpace("the hint should name the chunk it arrives in.");
    }

    /// <summary>
    /// The committed suppression list with the <c>dnc</c> row taken out, written to the test's own temp
    /// folder. A near-duplicate of the real list is not worth committing: what matters is the one row
    /// that differs, and deriving it here keeps the two files from drifting apart.
    /// </summary>
    private static async Task<string> WithoutNorthfieldAsync(string directory, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(RepoFixtures.SuppressionCsv, cancellationToken);
        var kept = lines
            .Where(line => !line.StartsWith("Northfield Storage Co,", StringComparison.Ordinal))
            .ToList();

        kept.Count.ShouldBe(
            lines.Length - 1,
            "exactly one row is dropped. If this is wrong, suppression.csv's first column changed and "
            + "the test is no longer removing what it thinks it is.");

        var path = Path.Combine(directory, "suppression-without-dnc.csv");
        await File.WriteAllLinesAsync(path, kept, cancellationToken);
        return path;
    }

    [Fact]
    public async Task ImportList_AnUnknownKind_IsRejectedRatherThanSilentlyIgnored()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var (importer, _) = await PrepareAsync(database, timeout.Token);

        ImportListKinds.IsKnown("dealerz").ShouldBeFalse();

        var thrown = await Should.ThrowAsync<Exception>(
            () => importer.ImportAsync("dealerz", RepoFixtures.DealersCsv, null, cancellationToken: timeout.Token),
            "mcp-tools.md §import_list takes four kinds. Importing an unknown one as nothing would "
            + "report 'imported: 0' and look like an empty file.");

        thrown.ShouldNotBeOfType<NullReferenceException>(
            "the rejection has to be deliberate. The tool maps it to VALIDATION_FAILED, which "
            + "C5ToolContractTests pins at the boundary.");
        thrown.Message.ShouldContain(
            "dealerz",
            Case.Insensitive,
            $"the message names the kind that was not understood. Got: {thrown.Message}");
    }

    [Fact]
    public async Task ImportList_AMissingFile_IsRejectedRatherThanReportingAnEmptyImport()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var (importer, _) = await PrepareAsync(database, timeout.Token);

        var thrown = await Should.ThrowAsync<Exception>(
            () => importer.ImportAsync(
                ImportListKinds.Dealers,
                Path.Combine(directory.Path, "not-here.csv"),
                null,
                cancellationToken: timeout.Token),
            "'imported: 0, errors: []' for a path that does not exist reads as 'your file is empty', "
            + "which sends the user looking in the wrong place.");

        thrown.ShouldNotBeOfType<NullReferenceException>("the rejection has to be deliberate.");
        thrown.Message.ShouldContain(
            "not-here.csv",
            Case.Insensitive,
            $"the message names the path the server looked at, or the user cannot find their file. "
            + $"Got: {thrown.Message}");
    }

    [Fact]
    public async Task ListDealers_ReportsBranchAndTerritoryCountsPerDealer()
    {
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var (importer, dealers) = await PrepareAsync(database, timeout.Token);

        await importer.ImportAsync(
            ImportListKinds.Dealers,
            RepoFixtures.DealersCsv,
            null,
            cancellationToken: timeout.Token);
        await importer.ImportAsync(
            ImportListKinds.Territories,
            RepoFixtures.TerritoriesCsv,
            null,
            cancellationToken: timeout.Token);

        var listed = await dealers.ListDealersAsync(timeout.Token);

        listed.Count.ShouldBe(3);

        var gulf = listed.Single(dealer => dealer.Id == "gulf");
        gulf.Name.ShouldBe("Gulf Lift Equipment");
        gulf.Branches.ShouldBe(1);
        gulf.TerritoryRows.ShouldBe(4, "four county rows and no ZIP overrides of its own.");

        listed.Single(dealer => dealer.Id == "bay").TerritoryRows.ShouldBe(18, "3 counties + 15 ZIPs.");
        listed.Single(dealer => dealer.Id == "pine").TerritoryRows.ShouldBe(13, "2 counties + 11 ZIPs.");
        listed.Sum(dealer => dealer.TerritoryRows).ShouldBe(35);
    }

    [Fact]
    public async Task FindTerritoryScope_ForADealer_IsTheUnionOfItsZipsAndCounties()
    {
        // technical-design §6.2: "dealer: dealer:<id> -> union of its territory ZIPs and counties."
        // resolve_geography turns this into a GeoScope; expanding ZIPs to counties needs the ZCTA file
        // and belongs to the geography service, so what the store owes is the raw union.
        using var timeout = Timeout();
        using var directory = new TempDirectory();
        await using var database = TempDatabase.In(directory.Path);
        var (importer, dealers) = await PrepareAsync(database, timeout.Token);

        await importer.ImportAsync(
            ImportListKinds.Dealers,
            RepoFixtures.DealersCsv,
            null,
            cancellationToken: timeout.Token);
        await importer.ImportAsync(
            ImportListKinds.Territories,
            RepoFixtures.TerritoriesCsv,
            null,
            cancellationToken: timeout.Token);

        var scope = await dealers.FindTerritoryScopeAsync("bay", timeout.Token);

        scope.ShouldNotBeNull();
        scope.DealerName.ShouldBe("Bayport Aerial Supply");
        scope.CountyFips.Order(StringComparer.Ordinal).ToList().ShouldBe(
            ["48039", "48071", "48167"],
            "bay's county rules: Brazoria, Chambers and Galveston.");
        scope.Zips.Count.ShouldBe(15, "and its fifteen ZIP overrides.");
        scope.Zips.ShouldContain("77506", "the Pasadena override the plan names.");

        (await dealers.FindTerritoryScopeAsync("nope", timeout.Token)).ShouldBeNull(
            "an unknown dealer is NOT_FOUND at the tool boundary, which needs null rather than an "
            + "empty scope - an empty scope would resolve to a market of zero.");
    }

    /// <summary>
    /// The same list in either format: the CSV is the spec pack's own file, the XLSX is the committed
    /// copy from <c>src/tests/Fixtures/lists</c> (see its README).
    /// </summary>
    private static string ListPath(string format, string name) =>
        format == "csv" ? RepoFixtures.Fixture($"{name}.csv") : RepoFixtures.ListFixture($"{name}.xlsx");

    private static (string Kind, string File)[] Lists =>
    [
        (ImportListKinds.Dealers, "dealers"),
        (ImportListKinds.Territories, "territories"),
        (ImportListKinds.Suppression, "suppression"),
    ];

    private static string Describe(ImportListResult result) =>
        $"imported={result.Imported} updated={result.Updated} errors=["
        + string.Join("; ", result.Errors.Select(error => $"row {error.Row}: {error.Message}"))
        + "]";

    private static async Task<(IListImporter Importer, IDealerStore Dealers)> PrepareAsync(
        TempDatabase database,
        CancellationToken cancellationToken)
    {
        await using var context = await database.MigrateAsync(cancellationToken);

        var importer = database.Services.GetService<IListImporter>()
            ?? throw new XunitException(
                "AddProspectStudioStorage does not register an IListImporter. Chunk C5 adds import_list "
                + "for dealers, territories and suppression (mcp-tools.md §import_list) over the four "
                + "tables in technical-design §5.2.");

        var dealers = database.Services.GetService<IDealerStore>()
            ?? throw new XunitException(
                "AddProspectStudioStorage does not register an IDealerStore. Chunk C5 adds it for "
                + "list_dealers, get_status's counts and §7.3/§7.4's inputs.");

        return (importer, dealers);
    }
}
