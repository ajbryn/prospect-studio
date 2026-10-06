using System.Globalization;
using System.Text.Json;
using ProspectStudio.Core.Leads;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Core.Tests.Leads;

/// <summary>
/// The guard on the research fixtures, in the spirit of C4's <c>FixtureTags</c> and C5's
/// <c>DealerFixtureIntegrityTests</c>: a fixture that is internally consistent but has no subject
/// silently guarantees a green test.
/// </summary>
/// <remarks>
/// A research document saved against a company the sample profile never selects would score nothing a
/// real run could reproduce, and a company routed to a different dealer would land in a different
/// <c>proximity</c> band. Both would leave the scoring tests passing while testing something else.
/// </remarks>
public class ResearchFixtureIntegrityTests
{
    public static TheoryData<string> Examples() => WorkedExamples.Numbers();

    [Theory]
    [MemberData(nameof(Examples))]
    public void TheWorkedExamplesSubjectIsACompanyTheSampleProfileActuallySelects(string placeId)
    {
        var example = WorkedExamples.ByPlaceId(placeId);
        var profile = SampleProfile.Load();
        var place = example.Place;

        profile.Outcome(place).ShouldBe(
            FixtureExpectation.Candidate,
            $"{example} is scored against lead '{placeId}', so a real find_candidates run has to produce "
            + $"it: {profile.Explain(place)}");
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void TheWorkedExamplesSubjectMatchesASegmentByCategoryRatherThanByName(string placeId)
    {
        // WorkedExample.Features claims SegmentMatch.Taxonomy for all three. If one of them only matched
        // a name keyword, §7.6 would score segmentFit 0.8 instead of 1.0 and every expected score below
        // would be five points out.
        var example = WorkedExamples.ByPlaceId(placeId);
        var profile = SampleProfile.Load();

        profile.MatchesCategory(example.Place).ShouldBeTrue(
            $"{example}: '{example.Place.TaxonomyPrimary}' (hierarchy "
            + $"{string.Join(" > ", example.Place.TaxonomyHierarchy)}) has to reach a segment's "
            + "overtureCategories, or segmentFit is 0.8 rather than 1.0.");

        example.Features.SegmentMatch.ShouldBe(SegmentMatch.Taxonomy);
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void TheWorkedExamplesSubjectNamesTheSegmentTheStrategyDocumentGivesIt(string placeId)
    {
        var example = WorkedExamples.ByPlaceId(placeId);
        var profile = SampleProfile.Load();

        var reachable = example.Place.TaxonomyHierarchy
            .Append(example.Place.TaxonomyPrimary)
            .Where(category => profile.SegmentByCategory.ContainsKey(category))
            .Select(category => profile.SegmentByCategory[category])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        reachable.ShouldContain(
            example.SegmentName,
            $"{example} is labelled '{example.SegmentName}', which is what list_leads shows in its "
            + $"'segment' column. Its categories reach: {string.Join(", ", reachable)}");
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void TheWorkedExamplesSubjectRoutesToTheDealerTheStrategyDocumentNames(string placeId)
    {
        // §7.4: a ZIP rule wins, otherwise the county default. Example 2 is the ZIP-override case
        // (Pasadena 77506 -> bay, although Harris defaults to gulf), which is also why its proximity is
        // measured to the Pasadena branch and not to West Houston.
        var example = WorkedExamples.ByPlaceId(placeId);
        var place = example.Place;

        var zipRule = SampleDealers.ZipRules.FirstOrDefault(rule => rule.Code == place.Postcode);
        var countyRule = SampleDealers.CountyRules.FirstOrDefault(rule => rule.Code == place.CountyFips);
        var winner = zipRule ?? countyRule;

        winner.ShouldNotBeNull($"{example} must be routed somewhere, or §7.6's proximity is 0.");
        winner.DealerId.ShouldBe(
            example.DealerId,
            $"docs/03 §8 routes {example} to '{example.DealerId}'. ZIP {place.Postcode} / county "
            + $"{place.CountyFips} in territories.csv says '{winner.DealerId}' at level '{winner.Level}'.");
        winner.BranchId.ShouldBe(example.BranchId);
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void TheWorkedExamplesSubjectSitsInsideSection76sTopProximityBand(string placeId)
    {
        // Every expected score assumes proximity = 1.0. Pin the assumption rather than the distance: a
        // fixture coordinate that drifted a few hundred metres must not fail, one that moved 30 miles must.
        var example = WorkedExamples.ByPlaceId(placeId);

        example.BranchDistanceMiles.ShouldBeLessThanOrEqualTo(
            25d,
            $"{example} is {example.BranchDistanceMiles.ToString("0.0", CultureInfo.InvariantCulture)} mi "
            + $"from branch '{example.BranchId}'. §7.6 scores proximity 1.0 only at or inside 25 mi, and "
            + "every expected score in these tests assumes it.");
    }

    [Fact]
    public void TheSampleProfileStillModelsASegmentMinimumThatDisagreesWithTheProfileMinimum()
    {
        // §7.6's applicable-minimum rule is only testable because the fixture profile sets both numbers and
        // they disagree. If a future edit dropped the segment's minEmployees, SizeFit_PrefersTheMatched-
        // SegmentsOwnMinimum would still pass against a hand-built feature set while nothing real
        // exercised the rule.
        var profile = SampleProfile.Load();

        profile.MinEmployees.ShouldBe(
            20,
            "poc/fixtures/sample-search-profile.json sets size.employeesMin. Every sizeFit expectation in "
            + "the C6 tests is computed from it.");

        profile.SegmentMinEmployees.ShouldContainKeyAndValue(
            "Facilities & property management",
            50,
            "the fourth segment wants bigger companies than the profile as a whole, which is the conflict "
            + "§7.6's 'the matched segment's minEmployees when that segment sets one' resolves. Segments "
            + $"with their own minimum: {string.Join(", ", profile.SegmentMinEmployees.Select(entry => $"{entry.Key}={entry.Value}"))}");

        profile.SegmentMinEmployees.Count.ShouldBe(
            1,
            "exactly one segment overrides the profile, so a lead in any other segment is the plain case.");
    }

    [Fact]
    public void TheSegmentWithItsOwnMinimumHasACompanyThatReachesIt()
    {
        // The §7.6 rule has a subject on real data, not only in unit tests: property_management is the
        // fourth segment's only category, and fx_0071 is the fixture row that lands in it.
        var profile = SampleProfile.Load();

        var reached = SamplePlaces.All
            .Where(place => profile.Outcome(place) == FixtureExpectation.Candidate)
            .Where(place => place.TaxonomyHierarchy
                .Append(place.TaxonomyPrimary)
                .Any(category => profile.SegmentByCategory.TryGetValue(category, out var segment)
                    && segment == "Facilities & property management"))
            .ToList();

        reached.ShouldNotBeEmpty(
            "no fixture company matches the one segment that sets its own minEmployees, so nothing on real "
            + "data would ever exercise the segment-minimum branch of §7.6.");
    }

    [Fact]
    public void EveryResearchFixtureValidatesAgainstTheSchema()
    {
        var validator = new ResearchValidator();

        foreach (var path in new[]
                 {
                     SampleResearch.ValidPath,
                     SampleResearch.GulfCoastSignPath,
                     SampleResearch.WestparkMetalFabPath,
                     SampleResearch.NorthlineGlassNoSignalPath,
                 })
        {
            var problems = validator.Validate(SampleResearch.Read(path));

            problems.ShouldBeEmpty(
                $"'{Path.GetFileName(path)}' is saved through save_research in the tool tests, so it has "
                + "to pass poc/schemas/research.schema.json. Reported: "
                + string.Join("; ", problems.Select(problem => $"{problem.Pointer} :: {problem.Message}")));
        }
    }

    [Fact]
    public void TheInvalidFixtureCarriesACaseAndAPointerForEveryEntry()
    {
        // If a case is added without an expectedError that parses to a pointer, the theory above would
        // assert against "/" and pass on any failure at all.
        SampleResearch.InvalidCases.ShouldNotBeEmpty();

        foreach (var entry in SampleResearch.InvalidCases)
        {
            entry.Case.ShouldNotBeNullOrWhiteSpace();
            entry.Research.ValueKind.ShouldBe(JsonValueKind.Object, entry.Case);
            entry.Pointer.Length.ShouldBeGreaterThan(
                1,
                $"'{entry.Case}' has expectedError '{entry.ExpectedError}', which gives the pointer "
                + $"'{entry.Pointer}'. An expectedError with no 'location: reason' shape would make the "
                + "precision assertion vacuous.");
            entry.Reason.ShouldNotBeNullOrWhiteSpace(entry.Case);
        }

        SampleResearch.InvalidCases
            .GroupBy(entry => entry.Case, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ShouldBeEmpty("two cases with the same name would make a failure impossible to place.");
    }

    [Fact]
    public void TheResearchDocumentOfWorkedExampleOneBelongsToWorkedExampleOne()
    {
        // The spec pack's sample-research-valid.json doubles as worked example 1's research. Nothing in
        // the file says so, so the link is asserted rather than assumed: if the fixture were rewritten
        // for another company, the Bayou scoring tests would quietly be scoring someone else.
        var research = SampleResearch.Valid();
        var place = WorkedExamples.BayouFulfillment.Place;

        var domain = new Uri(place.Websites[0]).Host.Replace("www.", string.Empty, StringComparison.Ordinal);

        research.ToString().ShouldContain(
            domain,
            Case.Insensitive,
            $"poc/fixtures/sample-research-valid.json is used as {WorkedExamples.BayouFulfillment}'s "
            + $"research, so it should cite '{domain}'. If the spec fixture now describes another "
            + "company, give worked example 1 a fixture of its own under src/tests/Fixtures/research.");

        research.GetProperty("employeeEstimate").GetProperty("value").GetInt32().ShouldBe(
            140,
            "the expected score of 100 is computed from 140 employees against the profile minimum of 20.");
        research.GetProperty("llmAdjustment").GetInt32().ShouldBe(
            5,
            "the +5 is what takes the base 97 to 102 and so proves the clamp at 100.");
    }
}
