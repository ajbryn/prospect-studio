using ProspectStudio.Core.Dealers;
using Shouldly;

namespace ProspectStudio.Core.Tests.Dealers;

/// <summary>
/// The row rules <c>import_list</c> applies before anything reaches the database
/// (<c>ListRows.ReadTerritory</c>): a territory code is exactly five ASCII digits, and a row's identity
/// does not depend on the case the user typed.
/// </summary>
/// <remarks>
/// These are unit tests rather than import tests because **no Texas ZIP or county FIPS can exercise
/// the rule they guard**. Every ZIP in the fixtures starts with 7 and every FIPS with 48, so a
/// spreadsheet that stored the column as a number never drops a digit from any of them — the damaged
/// value has to be written out by hand to be seen at all. A rule that only ever runs against data
/// which cannot break it is a rule nothing tests.
/// </remarks>
public class ListRowTests
{
    private static readonly IReadOnlySet<string> _dealerIds =
        new HashSet<string>(StringComparer.Ordinal) { "gulf", "bay", "pine" };

    private static readonly IReadOnlyDictionary<string, string> _branchDealers =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gulf-west"] = "gulf",
            ["bay-pas"] = "bay",
            ["pine-north"] = "pine",
        };

    [Theory]
    [InlineData(TerritoryLevels.Zip, "77506")]
    [InlineData(TerritoryLevels.Zip, "07030")]
    [InlineData(TerritoryLevels.County, "48201")]
    [InlineData(TerritoryLevels.County, "01001")]
    public void ReadTerritory_AFiveDigitCode_IsAccepted(string level, string code)
    {
        var territory = ListRows.ReadTerritory(Row(level: level, code: code), _dealerIds, _branchDealers);

        territory.Code.ShouldBe(
            code,
            "a leading zero is part of the code, not formatting: 07030 is Hoboken and 01001 is Agawam, "
            + "and an importer that parsed either as a number would store 7030 and 1001.");
        territory.Level.ShouldBe(level);
    }

    [Theory]
    [InlineData("7494", "a ZIP a spreadsheet stored as a number, so the leading zero is gone")]
    [InlineData("1001", "a county FIPS that lost its leading zero the same way")]
    [InlineData("7749.0", "a ZIP that came back from a numeric cell as a decimal")]
    [InlineData("772 01", "a code with a space in it")]
    [InlineData("775061", "six digits")]
    [InlineData("7750", "four digits")]
    [InlineData("", "an empty cell")]
    [InlineData("7750A", "a letter among the digits")]
    [InlineData("٧٧٥٠٦", "Arabic-Indic digits, which are digits but not ASCII ones")]
    [InlineData("७७५०६", "Devanagari digits")]
    public void ReadTerritory_ACodeThatIsNotFiveAsciiDigits_IsRejected(string code, string why)
    {
        var thrown = Should.Throw<ImportRowException>(
            () => ListRows.ReadTerritory(Row(code: code), _dealerIds, _branchDealers),
            $"'{code}' is {why}. A code that reaches the database damaged does not fail - it routes "
            + "every lead in that ZIP or county to nobody, reported as a coverage gap, and the marketer "
            + "has no way to tell that from a territory they genuinely never covered.");

        thrown.Message.ShouldContain(
            "five digits",
            Case.Insensitive,
            $"the message has to say what was expected. Got: {thrown.Message}");
    }

    [Fact]
    public void ReadTerritory_ACodeThatLostALeadingZero_SaysSoInTheMessage()
    {
        // The failure is silent in the file: the user sees 7494 and reads it as a typo rather than as
        // what their spreadsheet did to the column, so the message has to name the cause.
        var thrown = Should.Throw<ImportRowException>(
            () => ListRows.ReadTerritory(Row(code: "7494"), _dealerIds, _branchDealers));

        thrown.Message.ShouldContain(
            "leading zero",
            Case.Insensitive,
            $"Got: {thrown.Message}");
    }

    [Fact]
    public void ReadTerritory_TheSameRuleInADifferentCase_KeepsOneIdentity()
    {
        // The id is a content hash of (dealer, branch, level, code) and the columns are stored
        // lower-cased, so anything hashed in its raw case mints a second id for the same rule. The
        // import would then report 'imported' rather than 'updated', territoryRows would climb on every
        // re-import, and the duplicate would compete with the original in §7.4's tie-break.
        var lower = ListRows.ReadTerritory(
            Row(dealerId: "bay", branchId: "bay-pas", level: TerritoryLevels.Zip, code: "77506"),
            _dealerIds,
            _branchDealers);

        var upper = ListRows.ReadTerritory(
            Row(dealerId: "BAY", branchId: "Bay-Pas", level: "ZIP", code: "77506"),
            _dealerIds,
            _branchDealers);

        upper.Id.ShouldBe(
            lower.Id,
            "'Bay-Pas' and 'bay-pas' are the same branch, so they are the same territory row. If these "
            + "ids differ, re-importing a list somebody retyped doubles it.");
        upper.BranchId.ShouldBe(lower.BranchId);
        upper.DealerId.ShouldBe(lower.DealerId);
        upper.Level.ShouldBe(lower.Level);
    }

    [Fact]
    public void ReadTerritory_ARowWithNoBranch_StillHasAStableIdentity()
    {
        var first = ListRows.ReadTerritory(
            Row(dealerId: "gulf", branchId: "", level: TerritoryLevels.County, code: "48201"),
            _dealerIds,
            _branchDealers);

        var again = ListRows.ReadTerritory(
            Row(dealerId: "gulf", branchId: "  ", level: TerritoryLevels.County, code: "48201"),
            _dealerIds,
            _branchDealers);

        first.BranchId.ShouldBeNull("a blank column routes to the dealer without naming a branch.");
        again.Id.ShouldBe(first.Id, "a blank and a whitespace cell are the same absent branch.");
    }

    [Fact]
    public void ReadTerritory_DifferentRules_GetDifferentIdentities()
    {
        // The other half: the hash has to separate rules that really are different, or an import would
        // quietly overwrite one territory with another.
        var ids = new[]
        {
            Row(dealerId: "bay", branchId: "bay-pas", level: TerritoryLevels.Zip, code: "77506"),
            Row(dealerId: "bay", branchId: "bay-pas", level: TerritoryLevels.Zip, code: "77507"),
            Row(dealerId: "bay", branchId: "bay-pas", level: TerritoryLevels.County, code: "48201"),
            Row(dealerId: "gulf", branchId: "gulf-west", level: TerritoryLevels.Zip, code: "77506"),
            Row(dealerId: "gulf", branchId: "", level: TerritoryLevels.Zip, code: "77506"),
        }
            .Select(row => ListRows.ReadTerritory(row, _dealerIds, _branchDealers).Id)
            .ToList();

        ids.Distinct(StringComparer.Ordinal).Count().ShouldBe(
            ids.Count,
            "five different rules, five different ids. A collision here would make one territory "
            + "silently replace another on import.");
    }

    [Fact]
    public void ReadTerritory_AnUnknownDealerId_NamesTheValueThatWasWrong()
    {
        var thrown = Should.Throw<ImportRowException>(
            () => ListRows.ReadTerritory(Row(dealerId: "gulff"), _dealerIds, _branchDealers));

        thrown.Message.ShouldContain("gulff", Case.Insensitive, $"Got: {thrown.Message}");
    }

    [Theory]
    [InlineData("zipcode")]
    [InlineData("postcode")]
    [InlineData("")]
    public void ReadTerritory_ALevelThatIsNotZipOrCounty_IsRejected(string level)
    {
        TerritoryLevels.IsKnown(level).ShouldBeFalse();

        Should.Throw<ImportRowException>(
            () => ListRows.ReadTerritory(Row(level: level), _dealerIds, _branchDealers),
            "technical-design §5.2 allows 'zip' and 'county'. A third value would match no lead and "
            + "report as a coverage gap instead of as a bad row.");
    }

    private static ListRow Row(
        string dealerId = "bay",
        string branchId = "bay-pas",
        string level = TerritoryLevels.Zip,
        string code = "77506",
        string priority = "1") =>
        new(
            4,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["dealer_id"] = dealerId,
                ["branch_id"] = branchId,
                ["level"] = level,
                ["code"] = code,
                ["priority"] = priority,
            });
}
