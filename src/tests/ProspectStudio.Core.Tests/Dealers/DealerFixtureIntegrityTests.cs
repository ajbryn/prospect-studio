using ProspectStudio.Core.Candidates;
using ProspectStudio.Core.Dealers;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Core.Tests.Dealers;

/// <summary>
/// The three business lists still say what the C5 tests assume, and - the part that actually bites -
/// every rule they encode still has a <strong>subject</strong> in <c>sample-places.csv</c>.
/// </summary>
/// <remarks>
/// C4 lost coverage three times to fixtures that were self-consistent and had no subject: the dealer
/// rows sat in an excluded category, so suppression had nothing to remove. C5 found two more of the
/// same kind before writing a line of test code - <c>assignment=gap</c> had no lead in an uncovered
/// county, and §7.3's exact-name and fuzzy paths had no row that was not already caught by domain.
/// Five places rows were added for those, and this file is what stops them being quietly disarmed
/// again. Fix the row, not the assertion.
/// </remarks>
public class DealerFixtureIntegrityTests
{
    /// <summary>Rows the sample profile selects and its exclusions keep - the ones that become leads.</summary>
    private static readonly List<SamplePlace> _surviving = Surviving();

    [Fact]
    public void Dealers_csv_holds_three_dealers_with_one_located_branch_each()
    {
        SampleDealers.Dealers.Count.ShouldBe(3, "poc/fixtures/README.md: '3 fictional dealers, one branch each'.");

        SampleDealers.Dealers.Select(dealer => dealer.DealerId).Order(StringComparer.Ordinal).ToList()
            .ShouldBe(["bay", "gulf", "pine"]);

        foreach (var dealer in SampleDealers.Dealers)
        {
            dealer.BranchId.ShouldNotBeNullOrWhiteSpace(dealer.DealerId);
            dealer.Lat.ShouldBeInRange(25, 37, $"{dealer.DealerId}: a Texas latitude.");
            dealer.Lon.ShouldBeInRange(-107, -93, $"{dealer.DealerId}: a Texas longitude.");
            dealer.Website.ShouldEndWith(".example", Case.Sensitive, "every fixture domain is fictional.");
        }
    }

    [Fact]
    public void Every_territory_row_names_a_dealer_and_a_branch_that_exist()
    {
        var dealers = SampleDealers.Dealers.Select(dealer => dealer.DealerId).ToHashSet(StringComparer.Ordinal);
        var branches = SampleDealers.Dealers.Select(dealer => dealer.BranchId).ToHashSet(StringComparer.Ordinal);

        var orphans = SampleDealers.Territories
            .Where(row => !dealers.Contains(row.DealerId) || !branches.Contains(row.BranchId))
            .Select(row => $"{row.DealerId}/{row.BranchId} {row.Level} {row.Code}")
            .ToList();

        orphans.ShouldBeEmpty(
            "territories.csv must be clean, or the import test cannot tell a real row-level error from "
            + "fixture rot. The deliberately broken file lives in "
            + "src/tests/Fixtures/lists/territories_bad_dealer.csv instead.");
    }

    [Fact]
    public void Every_territory_level_is_one_section_52_allows()
    {
        var unknown = SampleDealers.Territories
            .Where(row => !TerritoryLevels.IsKnown(row.Level))
            .Select(row => row.Level)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        unknown.ShouldBeEmpty("technical-design §5.2: territories.level is 'zip' or 'county'.");

        SampleDealers.ZipRules.ShouldAllBe(
            row => row.Code.Length == 5 && row.Code.All(char.IsAsciiDigit),
            "a zip rule's code is a ZIP5, which is what §7.4 matches a lead's truncated postcode against.");
        SampleDealers.CountyRules.ShouldAllBe(
            row => row.Code.Length == 5 && row.Code.All(char.IsAsciiDigit),
            "a county rule's code is a 5-digit FIPS.");
    }

    [Fact]
    public void The_priority_column_carries_a_real_value_rather_than_all_ones()
    {
        // §7.4's tie-break is "lowest priority, then the nearest branch". A fixture where every row was
        // priority 1 would let an implementation ignore the column entirely and still pass.
        var harris = SampleDealers.CountyRules.Where(row => row.Code == "48201").ToList();

        harris.ShouldHaveSingleItem().DealerId.ShouldBe("gulf", "Harris County defaults to gulf.");
        harris[0].Priority.ShouldBe(
            2,
            "territories.csv gives gulf Harris at priority 2 and its other counties at 1, so the column "
            + "has to be read rather than assumed.");

        SampleDealers.CountyRules
            .Where(row => row.DealerId == "gulf" && row.Code != "48201")
            .ShouldAllBe(row => row.Priority == 1, "gulf's other counties are priority 1.");

        SampleDealers.Territories.Select(row => row.Priority).Distinct().Count().ShouldBeGreaterThan(
            1,
            "if every priority were the same, §7.4's first tie-break would be untestable from the "
            + "fixture and an implementation could drop it unnoticed.");
    }

    [Fact]
    public void San_Jacinto_is_the_one_Houston_county_no_territory_covers()
    {
        SampleDealers.UncoveredHoustonCounties.ShouldBe(
            [SampleDealers.UncoveredCountyFips],
            "poc/fixtures/README.md says territories.csv covers 'the 9 Houston CBSA counties'. The tenth "
            + "is San Jacinto 48407, and it is the only county that can produce assignment=gap inside a "
            + "Houston search - Jefferson 48245 is outside the CBSA and is filtered out long before "
            + "assignment runs.");
    }

    [Fact]
    public void The_uncovered_county_has_a_lead_in_it_so_a_gap_can_actually_happen()
    {
        var gap = SamplePlaces.Row(SampleDealers.CoverageGapPlaceId);

        gap.CountyFips.ShouldBe(
            SampleDealers.UncoveredCountyFips,
            $"{gap.Id} exists so the plan's 'a lead in a county with no territory -> assignment=gap' "
            + "has a subject. Before C5 added it, 48407 held no places row at all and the test could "
            + "not have failed.");

        FixtureTags.Of(gap).ShouldBe(
            FixtureExpectation.Candidate,
            "a row the profile does not select never becomes a lead, so it could not be a gap either.");

        _surviving.Select(place => place.Id).ShouldContain(gap.Id);

        _surviving
            .Count(place => SampleDealers.UncoveredHoustonCounties.Contains(place.CountyFips))
            .ShouldBe(1, "exactly one lead is a coverage gap, which is what coverageGaps must report.");
    }

    [Fact]
    public void All_three_dealers_have_a_searchable_row_so_every_one_can_be_proven_suppressed()
    {
        var dealerRows = SampleDealers.Suppression
            .Where(row => row.Reason == SuppressionReasons.Dealer)
            .ToList();

        dealerRows.Count.ShouldBe(3, "one suppression row per dealer in dealers.csv.");

        foreach (var row in dealerRows)
        {
            var matches = _surviving
                .Where(place => Suppresses(row, place))
                .Select(place => place.Id)
                .ToList();

            matches.ShouldNotBeEmpty(
                $"'{row.CompanyName}' is a dealer the campaign must never mail, and no row the sample "
                + "profile returns matches it - so apply_suppression would have nothing to remove and "
                + "the test could not fail. Add a places row in a target category, as C5 did for "
                + $"{SampleDealers.ThirdDealerPlaceId}.");
        }
    }

    [Fact]
    public void Every_suppression_reason_can_appear_in_a_result()
    {
        // 'dnc' was the one that could not: its only row has no domain, and nothing in the places
        // fixture matched it by name. A reason that cannot appear is a branch of the output contract
        // that nothing exercises.
        foreach (var reason in SampleDealers.Suppression.Select(row => row.Reason).Distinct(StringComparer.Ordinal))
        {
            SuppressionReasons.IsKnown(reason).ShouldBeTrue($"'{reason}' is not a §5.2 reason.");

            var rows = SampleDealers.Suppression.Where(row => row.Reason == reason).ToList();

            _surviving.ShouldContain(
                place => rows.Any(row => Suppresses(row, place)),
                $"no lead can ever carry the reason '{reason}', so find_candidates' suppressed "
                + "breakdown has a key nothing can fill.");
        }

        SampleDealers.Suppression.Select(row => row.Reason).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToList()
            .ShouldBe(
                ["competitor", "customer", "dealer", "dnc"],
                "poc/fixtures/README.md: 'Dealers, customers, a do-not-contact entry and a competitor'.");
    }

    [Fact]
    public void The_dnc_row_is_the_only_route_to_section_73s_exact_name_rule()
    {
        var dnc = SampleDealers.SuppressionFor("Northfield Storage Co");

        dnc.Domain.ShouldBeEmpty(
            "this row has no domain on purpose: it is the only suppression row whose match has to come "
            + "from the name, so §7.3's exact name_norm + ZIP path has a subject at all.");

        var subject = SamplePlaces.Row(SampleDealers.NameMatchPlaceId);

        NameNormalizer.Normalize(subject.Name).ShouldBe(
            NameNormalizer.Normalize(dnc.CompanyName),
            $"{subject.Id} exists to match '{dnc.CompanyName}' exactly after §7.1 normalization - note "
            + "the trailing 'Co' is stripped from both.");
        Zip5(subject.Postcode).ShouldBe(dnc.Zip, "and at the same ZIP, which §7.3 requires.");

        DomainKey.For(subject.Websites.FirstOrDefault()).ShouldNotBeNull(
            "the row does have a website - that is the point. If it were blank, an implementation that "
            + "only ever matched on domain would still suppress nothing here and look correct.");

        SampleDealers.Suppression
            .Select(row => row.Domain)
            .Where(domain => domain.Length > 0)
            .ShouldNotContain(
                DomainKey.For(subject.Websites[0]),
                $"{subject.Id}'s domain must be in no suppression row, or the domain rule would reach "
                + "it first and the name rule would stay untested.");
    }

    [Fact]
    public void The_fuzzy_rule_has_a_subject_above_the_threshold_and_a_control_below_it()
    {
        var customer = SampleDealers.SuppressionFor("Coastal Crane & Rigging LLC");
        var target = NameNormalizer.Normalize(customer.CompanyName);

        var hit = SamplePlaces.Row(SampleDealers.FuzzyMatchPlaceId);
        var control = SamplePlaces.Row(SampleDealers.FuzzyControlPlaceId);

        var hitScore = JaroWinkler.Similarity(NameNormalizer.Normalize(hit.Name), target);
        var controlScore = JaroWinkler.Similarity(NameNormalizer.Normalize(control.Name), target);

        hitScore.ShouldBeGreaterThanOrEqualTo(
            SuppressionMatcher.FuzzyThreshold,
            $"{hit.Id} '{hit.Name}' must clear §7.3's 0.92 (it scores {hitScore:F4}).");
        hitScore.ShouldBeLessThan(
            1.0,
            $"and must not be an exact match, or it would exercise the name rule rather than the fuzzy "
            + $"one. {hit.Id} normalizes to '{NameNormalizer.Normalize(hit.Name)}'.");

        controlScore.ShouldBeLessThan(
            SuppressionMatcher.FuzzyThreshold,
            $"{control.Id} '{control.Name}' is the negative control and must stay below 0.92 "
            + $"(it scores {controlScore:F4}). A fuzzy rule with no negative case is half a test.");
        controlScore.ShouldBeGreaterThanOrEqualTo(
            0.90,
            $"and it has to be close: at {controlScore:F4} it would be suppressed by §7.9's 0.90 "
            + "matchback threshold, so wiring that figure into §7.3 by mistake fails here. A control "
            + "far below 0.92 would let that mistake through.");

        Zip5(hit.Postcode).ShouldBe(customer.Zip, "both rules need the same ZIP as the suppression row.");
        Zip5(control.Postcode).ShouldBe(
            customer.Zip,
            "the control shares the ZIP too, so the only thing that saves it is the score.");
    }

    [Fact]
    public void The_control_is_the_only_near_miss_in_the_whole_fixture()
    {
        // Otherwise a loosened threshold would suppress some unrelated lead and the counts would move
        // for a reason no test names.
        var rules = SampleDealers.Suppression
            .Select(row => (row.CompanyName, Norm: NameNormalizer.Normalize(row.CompanyName), row.Zip))
            .ToList();

        var close = _surviving
            .Select(place => (
                place.Id,
                place.Name,
                Best: rules
                    .Where(rule => rule.Zip == Zip5(place.Postcode))
                    .Select(rule => (rule.CompanyName, Score: JaroWinkler.Similarity(NameNormalizer.Normalize(place.Name), rule.Norm)))
                    .OrderByDescending(scored => scored.Score)
                    .FirstOrDefault()))
            .Where(row => row.Best.Score is > 0.80 and < SuppressionMatcher.FuzzyThreshold)
            .Select(row => $"{row.Id} '{row.Name}' vs '{row.Best.CompanyName}' = {row.Best.Score:F4}")
            .ToList();

        close.ShouldBe(
            [$"{SampleDealers.FuzzyControlPlaceId} 'Coastal Crane and Haul' vs 'Coastal Crane & Rigging LLC' = 0.9076"],
            "exactly one row sits between 0.80 and the threshold, and it is the deliberate control. "
            + "Anything else here is an accident waiting to change the suppression counts.");
    }

    [Theory]
    [InlineData("fx_0002", "77506", "48201", "bay", "the plan's Pasadena ZIP override, in a county that defaults to gulf")]
    [InlineData("fx_0001", "77494", "48157", "gulf", "Katy, Fort Bend - a county default with no ZIP rule")]
    [InlineData("fx_0022", "77301", "48339", "pine", "Conroe, Montgomery")]
    [InlineData("fx_0026", "77551", "48167", "bay", "Galveston")]
    [InlineData("fx_0065", "77504", "48201", "bay", "a ZIP+4 postcode on a ZIP that routes, so truncation is load-bearing")]
    [InlineData("fx_0036", "77449", "48201", "gulf", "a Harris ZIP no rule covers, so the county default applies")]
    public void Each_routing_case_the_plan_names_has_a_lead_to_route(
        string id,
        string zip,
        string countyFips,
        string dealerId,
        string why)
    {
        var place = SamplePlaces.Row(id);

        Zip5(place.Postcode).ShouldBe(zip, $"{id}: {why}");
        place.CountyFips.ShouldBe(countyFips, $"{id}: {why}");
        _surviving.Select(row => row.Id).ShouldContain(
            id,
            $"{id} has to reach the campaign or it cannot be routed: {why}");

        var expected = SampleDealers.ZipRules.Where(rule => rule.Code == zip).ToList();
        if (expected.Count > 0)
        {
            expected.Select(rule => rule.DealerId).Distinct(StringComparer.Ordinal).ShouldBe([dealerId]);
        }
        else
        {
            SampleDealers.CountyRules
                .Where(rule => rule.Code == countyFips)
                .Select(rule => rule.DealerId)
                .Distinct(StringComparer.Ordinal)
                .ShouldBe([dealerId], $"{id}: {why}");
        }
    }

    [Fact]
    public void Thirty_five_territory_rows_and_seven_suppression_rows_are_what_get_status_counts()
    {
        SampleDealers.Territories.Count.ShouldBe(35, "9 county defaults, 15 bay ZIPs and 11 pine ZIPs.");
        SampleDealers.Suppression.Count.ShouldBe(7);

        var perDealer = SampleDealers.Territories
            .GroupBy(row => row.DealerId)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        // list_dealers reports territoryRows per dealer, and get_status reports the total.
        perDealer.ShouldContainKeyAndValue("gulf", 4);
        perDealer.ShouldContainKeyAndValue("bay", 18);
        perDealer.ShouldContainKeyAndValue("pine", 13);
        perDealer.Values.Sum().ShouldBe(SampleDealers.Territories.Count);
    }

    /// <summary>§7.3 as the fixture guard needs it - the production rule is in <c>SuppressionMatcher</c>.</summary>
    private static bool Suppresses(SampleSuppression row, SamplePlace place)
    {
        var placeDomain = DomainKey.For(place.Websites.FirstOrDefault());
        var rowDomain = DomainKey.For(row.Domain);
        if (rowDomain is not null && placeDomain is not null && rowDomain == placeDomain)
        {
            return true;
        }

        var name = NameNormalizer.Normalize(place.Name);
        var target = NameNormalizer.Normalize(row.CompanyName);
        var zip = Zip5(place.Postcode);

        return (name == target && (row.Zip.Length == 0 || row.Zip == zip))
            || (row.Zip == zip && JaroWinkler.Similarity(name, target) >= SuppressionMatcher.FuzzyThreshold);
    }

    private static string Zip5(string postcode) => postcode.Length > 5 ? postcode[..5] : postcode;

    private static List<SamplePlace> Surviving()
    {
        var profile = SampleProfile.Load();
        return [.. SamplePlaces.All.Where(place => profile.Selects(place) && !profile.IsExcluded(place))];
    }
}
