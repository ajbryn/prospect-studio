using ProspectStudio.Core.Candidates;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Core.Tests.Candidates;

/// <summary>
/// Name normalization, technical-design §7.1: lowercase, <c>&amp;</c> to <c>and</c>,
/// <strong>remove</strong> punctuation, strip legal words <strong>by position</strong>, collapse
/// whitespace. The C4 test list asks for 20+ table cases and names two of them outright.
/// </summary>
/// <remarks>
/// This is the most load-bearing string function in the POC: <c>name_norm</c> decides who merges with
/// whom in dedupe (§7.2), who is suppressed (§7.3) and which warranty registration matches which lead
/// (§7.9). Loosening a case here silently changes all three.
/// </remarks>
public class NameNormalizerTests
{
    [Theory]
    // The two cases the C4 plan names.
    [InlineData("The Bayou Fulfillment Co., LLC", "bayou fulfillment")]
    [InlineData("Gulf Coast Sign & Lighting", "gulf coast sign and lighting")]
    // Case folding, and an ampersand already spelled out.
    [InlineData("GULF COAST SIGN AND LIGHTING", "gulf coast sign and lighting")]
    [InlineData("Bayou Fulfillment Co.", "bayou fulfillment")]
    // Every legal word §7.1 lists, one per row, so none can be quietly dropped from the list.
    [InlineData("Ship Channel Logistics LLC", "ship channel logistics")]
    [InlineData("Westpark Metal Fab, Inc.", "westpark metal fab")]
    [InlineData("Westpark Metal Fab Incorporated", "westpark metal fab")]
    [InlineData("Harris Ridge Cold Storage Corp", "harris ridge cold storage")]
    [InlineData("Harris Ridge Cold Storage Corporation", "harris ridge cold storage")]
    [InlineData("Summit Signs Company", "summit signs")]
    [InlineData("Bayside Products Ltd.", "bayside products")]
    [InlineData("Oakmont Fabrication LP", "oakmont fabrication")]
    [InlineData("Pecan Electrical Contractors LLP", "pecan electrical contractors")]
    [InlineData("Northline Glass PLLC", "northline glass")]
    // Punctuation is REMOVED, not replaced by a space - which is the literal reading of §7.1's
    // "strip punctuation" and also the only reading that makes its "l.l.c" entry work: stripping the
    // dots first turns "L.L.C." into "llc", which is already on the list.
    [InlineData("Ship Channel Logistics, L.L.C.", "ship channel logistics")]
    // "the" goes only in LEADING position (§7.1), so this loses its first word...
    [InlineData("The Woodlands Facilities Group", "woodlands facilities group")]
    // ...and keeps an interior one. The metro is literally called "Houston-Pasadena-The Woodlands".
    [InlineData("Houston The Woodlands Glass", "houston the woodlands glass")]
    // Position is the whole point: §7.1's original "remove these tokens anywhere" turned this into
    // "industries" and threw away the distinguishing word. A leading legal word is content.
    [InlineData("CO Industries", "co industries")]
    [InlineData("Corp Services LLC", "corp services")]
    // Trailing removal repeats until nothing legal is left at the end.
    [InlineData("Bayou Fulfillment Co Inc LLC", "bayou fulfillment")]
    [InlineData("Energy Corridor Facilities Group", "energy corridor facilities group")]
    // Several legal words at once, and the long fixture name the postcard QA checks use.
    [InlineData(
        "The Waller County Industrial Park Facilities Management Company, LLC",
        "waller county industrial park facilities management")]
    // Whitespace collapse, including leading and trailing.
    [InlineData("  Prairie   Warehousing  ", "prairie warehousing")]
    [InlineData("Prairie Products  Inc", "prairie products")]
    // Punctuation that is not a legal word: parentheses, apostrophes, commas, periods.
    [InlineData("Bayside Steel Works (unverified)", "bayside steel works unverified")]
    [InlineData("O'Brien Rigging", "obrien rigging")]
    [InlineData("Gulfway Power & Lighting, Inc.", "gulfway power and lighting")]
    // Digits are content, not punctuation.
    [InlineData("Buffalo 3PL Services", "buffalo 3pl services")]
    [InlineData("Summit Sign Co", "summit sign")]
    // Nothing left once the legal words go. Empty is the honest answer; a crash is not.
    [InlineData("LLC", "")]
    [InlineData("The Company", "")]
    // An interior legal word is kept even when the ends are stripped.
    [InlineData("The Lift Co Of Texas Inc", "lift co of texas")]
    // Empty and whitespace-only input.
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Normalize_FollowsTheTable(string input, string expected) =>
        NameNormalizer.Normalize(input).ShouldBe(expected);

    [Fact]
    public void Normalize_Null_IsEmpty() =>
        NameNormalizer.Normalize(null).ShouldBe(
            string.Empty,
            "a place with no name still has to get a name_norm, because the column is not nullable.");

    [Fact]
    public void Normalize_IsIdempotent()
    {
        // name_norm is stored and compared against freshly normalized values later (suppression in C5,
        // matchback in C13). If normalizing twice differed, those comparisons would quietly miss.
        foreach (var place in SamplePlaces.All)
        {
            var once = NameNormalizer.Normalize(place.Name);
            NameNormalizer.Normalize(once).ShouldBe(once, $"'{place.Name}' ({place.Id})");
        }
    }

    [Fact]
    public void Every_fixture_name_normalizes_to_something()
    {
        var empty = SamplePlaces.All
            .Where(place => NameNormalizer.Normalize(place.Name).Length == 0)
            .Select(place => $"{place.Id} '{place.Name}'")
            .ToList();

        empty.ShouldBeEmpty(
            "no fixture company is only legal words, so none may normalize away to nothing - a lead "
            + "with an empty name_norm would merge with every other empty one.");
    }

    [Fact]
    public void The_legal_words_are_split_by_position_as_section_71_now_says()
    {
        NameNormalizer.LeadingWords.ShouldBe(
            ["the"],
            "§7.1 strips 'the' only in leading position; it is the only word on that list.");

        NameNormalizer.TrailingWords.Order(StringComparer.Ordinal).ToList().ShouldBe(
            [
                "co", "company", "corp", "corporation", "inc", "incorporated", "llc", "llp", "lp",
                "ltd", "pllc",
            ],
            "§7.1's trailing list. 'l.l.c' is not on it and does not need to be: punctuation is removed "
            + "rather than replaced, so 'L.L.C.' has already become 'llc' by the time this runs.");
    }

    [Fact]
    public void Normalize_KeepsTheTwoDedupePairsEqualAndTheControlsApart()
    {
        // These four pairs are what §7.2's name rules are tested against, so the normalizer has to
        // agree with src/tests/Fixtures/places/README.md about which names are "the same".
        Same("fx_0007", "fx_0014");   // Westpark Metal Fab / Westpark Metal Fab, Inc.
        Same("fx_0025", "fx_0113");   // Pecan Storage & Distribution / Pecan Storage and Distribution Company
        Same("fx_0017", "fx_0018");   // Coastal Crane & Rigging LLC / Coastal Crane and Rigging
        Different("fx_0001", "fx_0013");   // Bayou Fulfillment Co. / Brazos Way Fulfillment
        Different("fx_0065", "fx_0066");   // Summit Signs / Summit Sign Co.

        static void Same(string left, string right) =>
            NameNormalizer.Normalize(SamplePlaces.Row(left).Name).ShouldBe(
                NameNormalizer.Normalize(SamplePlaces.Row(right).Name),
                $"{left} and {right} are a name-match pair in the fixture README.");

        static void Different(string left, string right) =>
            NameNormalizer.Normalize(SamplePlaces.Row(left).Name).ShouldNotBe(
                NameNormalizer.Normalize(SamplePlaces.Row(right).Name),
                $"{left} and {right} are deliberately different names in the fixture README.");
    }
}
