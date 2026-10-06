using ProspectStudio.Core.Dealers;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Core.Tests.Dealers;

/// <summary>
/// Territory assignment, technical-design §7.4: "ZIP5 match (<c>level=zip</c>) wins; otherwise county
/// FIPS (<c>level=county</c>). Ties: lowest <c>priority</c>, then the nearest branch (haversine). No
/// match → <c>assignment=gap</c>, <c>dealer_id=null</c>."
/// </summary>
/// <remarks>
/// The fixture cases come from <c>territories.csv</c> so the committed list is what is proven, not a
/// convenient copy of it. The tie-breaks are synthetic because the fixture has no genuine contest:
/// Harris is the only county rule for Harris, so its priority 2 never loses to anything. A rule that
/// is only ever applied to data where it cannot change the answer is not tested.
/// </remarks>
public class TerritoryAssignmentTests
{
    private static readonly IReadOnlyList<TerritoryRule> _fixtureRules =
    [
        .. SampleDealers.Territories.Select(row =>
            new TerritoryRule(row.DealerId, row.BranchId, row.Level, row.Code, row.Priority)),
    ];

    private static readonly IReadOnlyList<BranchPoint> _fixtureBranches =
    [
        .. SampleDealers.Dealers.Select(dealer =>
            new BranchPoint(dealer.DealerId, dealer.BranchId, dealer.Lat, dealer.Lon)),
    ];

    [Fact]
    public void ResolveAssignment_Pasadena77506_RoutesToBayByZipOverrideDespiteHarrisDefaultingToGulf()
    {
        var place = SamplePlaces.Row("fx_0002");

        var assignment = Assign(place);

        assignment.DealerId.ShouldBe(
            "bay",
            "territories.csv gives 77506 to bay and Harris County 48201 to gulf. §7.4: 'ZIP5 match wins'. "
            + "A result of 'gulf' means the county rule was consulted first, which would route every "
            + "east-Harris lead to the wrong dealer.");
        assignment.BranchId.ShouldBe("bay-pas", "the branch comes from the winning territory row.");
        assignment.Assignment.ShouldBe(Assignments.Auto);
        assignment.MatchedLevel.ShouldBe(
            TerritoryLevels.Zip,
            "and it must be recorded as a ZIP match: 'bay' by county would be the right dealer for the "
            + "wrong reason, and nothing else can tell the two apart.");
    }

    [Fact]
    public void ResolveAssignment_Katy77494_RoutesToGulfByItsCountyDefault()
    {
        // 77494 is not in territories.csv at all, so the Fort Bend county rule is what answers.
        var assignment = Assign(SamplePlaces.Row("fx_0001"));

        assignment.DealerId.ShouldBe("gulf");
        assignment.MatchedLevel.ShouldBe(TerritoryLevels.County, "no ZIP rule covers 77494.");
        assignment.Assignment.ShouldBe(Assignments.Auto);
    }

    [Fact]
    public void ResolveAssignment_ConroeMontgomery_RoutesToPine()
    {
        var assignment = Assign(SamplePlaces.Row("fx_0022"));

        assignment.DealerId.ShouldBe("pine", "Montgomery County 48339 is pine's.");
        assignment.BranchId.ShouldBe("pine-north");
    }

    [Fact]
    public void ResolveAssignment_Galveston_RoutesToBay()
    {
        var assignment = Assign(SamplePlaces.Row("fx_0026"));

        assignment.DealerId.ShouldBe("bay", "Galveston County 48167 is bay's.");
        assignment.MatchedLevel.ShouldBe(TerritoryLevels.County);
    }

    [Fact]
    public void ResolveAssignment_HarrisZipWithNoRule_FallsBackToTheCountyDefaultAtPriorityTwo()
    {
        // 77449 is a Harris ZIP no override covers. The only Harris rule is gulf at priority 2, so the
        // priority column has to be read without changing the answer.
        var assignment = Assign(SamplePlaces.Row("fx_0036"));

        assignment.DealerId.ShouldBe("gulf");
        assignment.MatchedLevel.ShouldBe(TerritoryLevels.County);
    }

    [Fact]
    public void ResolveAssignment_CountyWithNoTerritory_IsGapWithNoDealer()
    {
        var place = SamplePlaces.Row(SampleDealers.CoverageGapPlaceId);

        var assignment = Assign(place);

        assignment.Assignment.ShouldBe(
            Assignments.Gap,
            $"{place.Id} is in San Jacinto {SampleDealers.UncoveredCountyFips}, a real Houston CBSA "
            + "county that territories.csv deliberately does not cover. §7.4: 'No match → "
            + "assignment=gap, dealer_id=null'.");
        assignment.DealerId.ShouldBeNull("a gap has no dealer; an empty string is not the same thing.");
        assignment.BranchId.ShouldBeNull();
        assignment.MatchedLevel.ShouldBeNull();
    }

    [Fact]
    public void ResolveAssignment_ZipRuleWins_EvenWhenItsPriorityIsWorseThanTheCountys()
    {
        // The sharp case: §7.4 orders the two levels, it does not merge them and sort by priority. A
        // ZIP rule at priority 9 still beats a county rule at priority 1.
        var assignment = TerritoryAssigner.Assign(
            new AssignmentSubject("77506", "48201", 29.7184, -95.2285),
            [
                new TerritoryRule("bay", "bay-pas", TerritoryLevels.Zip, "77506", 9),
                new TerritoryRule("gulf", "gulf-west", TerritoryLevels.County, "48201", 1),
            ],
            _fixtureBranches);

        assignment.DealerId.ShouldBe(
            "bay",
            "a ZIP rule is more specific than a county rule whatever the priorities say. If priority "
            + "were compared across levels, one low-priority county row would silently cancel every ZIP "
            + "override in it.");
        assignment.MatchedLevel.ShouldBe(TerritoryLevels.Zip);
    }

    [Fact]
    public void ResolveAssignment_TwoCountyRules_PrefersTheLowestPriority()
    {
        var assignment = TerritoryAssigner.Assign(
            new AssignmentSubject("77002", "48201", 29.7604, -95.3698),
            [
                // The far branch has the better priority, so priority has to be compared before distance.
                new TerritoryRule("gulf", "gulf-west", TerritoryLevels.County, "48201", 2),
                new TerritoryRule("pine", "pine-north", TerritoryLevels.County, "48201", 1),
            ],
            _fixtureBranches);

        assignment.DealerId.ShouldBe(
            "pine",
            "§7.4: 'Ties: lowest priority, then the nearest branch'. Priority comes first, so a dealer "
            + "who has asked to own a county outranks a nearer branch.");
    }

    [Fact]
    public void ResolveAssignment_SamePriority_PrefersTheNearestBranchByHaversine()
    {
        // Downtown Houston: bay-pas in Pasadena is about 17 km away, gulf-west in Katy about 44 km.
        var assignment = TerritoryAssigner.Assign(
            new AssignmentSubject("77002", "48201", 29.7604, -95.3698),
            [
                new TerritoryRule("gulf", "gulf-west", TerritoryLevels.County, "48201", 1),
                new TerritoryRule("bay", "bay-pas", TerritoryLevels.County, "48201", 1),
            ],
            _fixtureBranches);

        assignment.DealerId.ShouldBe(
            "bay",
            "equal priority, so the nearest branch wins. bay-pas (Pasadena, 29.6911/-95.2091) is about "
            + "17 km from downtown; gulf-west (Katy, 29.7858/-95.8245) is about 44 km. A result of "
            + "'gulf' means the tie was broken by list order, which is not a rule.");
        assignment.BranchId.ShouldBe("bay-pas");
    }

    [Fact]
    public void ResolveAssignment_SamePriorityFromTheOtherSideOfTheCounty_PrefersTheOtherBranch()
    {
        // The mirror image, so the previous test cannot pass by always choosing bay.
        var assignment = TerritoryAssigner.Assign(
            new AssignmentSubject("77494", "48201", 29.7858, -95.8100),
            [
                new TerritoryRule("gulf", "gulf-west", TerritoryLevels.County, "48201", 1),
                new TerritoryRule("bay", "bay-pas", TerritoryLevels.County, "48201", 1),
            ],
            _fixtureBranches);

        assignment.DealerId.ShouldBe("gulf", "out in Katy, gulf-west is the near branch.");
    }

    [Fact]
    public void ResolveAssignment_TwoZipRulesForTheSameZip_BreakTheTieTheSameWay()
    {
        // The tie-break belongs to the rule, not to the level: two dealers can both claim a ZIP.
        var assignment = TerritoryAssigner.Assign(
            new AssignmentSubject("77506", "48201", 29.7184, -95.2285),
            [
                new TerritoryRule("gulf", "gulf-west", TerritoryLevels.Zip, "77506", 1),
                new TerritoryRule("bay", "bay-pas", TerritoryLevels.Zip, "77506", 1),
            ],
            _fixtureBranches);

        assignment.DealerId.ShouldBe("bay", "Pasadena is next door to bay-pas and 60 km from gulf-west.");
        assignment.MatchedLevel.ShouldBe(TerritoryLevels.Zip);
    }

    [Fact]
    public void ResolveAssignment_TwoRulesWhoseBranchesAreBothUnknown_BreaksTheTieOnDealerId()
    {
        // The last rung of §7.4's tie-break, which no other test can reach: both branches are missing
        // from dealer_branches, so both distances are the same sentinel and priority is equal. Without
        // a final deterministic comparison the answer would come down to the order SQLite happened to
        // return the rules in, so the same campaign could route the same lead to a different dealer on
        // a re-run - and NFR-3 says a re-run changes nothing.
        IReadOnlyList<TerritoryRule> rules =
        [
            new TerritoryRule("zulu", "zulu-ghost", TerritoryLevels.County, "48201", 1),
            new TerritoryRule("alpha", "alpha-ghost", TerritoryLevels.County, "48201", 1),
        ];

        var subject = new AssignmentSubject("77002", "48201", 29.7604, -95.3698);

        var forwards = TerritoryAssigner.Assign(subject, rules, []);
        var backwards = TerritoryAssigner.Assign(subject, [.. rules.Reverse()], []);

        forwards.DealerId.ShouldBe(
            "alpha",
            "equal priority and equal (unknown) distance, so the lowest dealer id wins. Any rule will "
            + "do as long as it is a rule; what must not happen is the answer depending on list order.");
        backwards.ShouldBe(forwards, "and reversing the input cannot change it.");
        forwards.Assignment.ShouldBe(Assignments.Auto, "it is still assigned, not a gap.");
    }

    [Fact]
    public void ResolveAssignment_TwoUnknownBranchesOfTheSameDealer_BreaksTheTieOnBranchId()
    {
        // And the rung below that: same dealer, so the dealer-id comparison is a draw too.
        IReadOnlyList<TerritoryRule> rules =
        [
            new TerritoryRule("bay", "bay-zeta", TerritoryLevels.Zip, "77506", 1),
            new TerritoryRule("bay", "bay-beta", TerritoryLevels.Zip, "77506", 1),
        ];

        var subject = new AssignmentSubject("77506", "48201", 29.7184, -95.2285);

        var forwards = TerritoryAssigner.Assign(subject, rules, []);
        var backwards = TerritoryAssigner.Assign(subject, [.. rules.Reverse()], []);

        forwards.BranchId.ShouldBe("bay-beta", "the lowest branch id breaks the last tie.");
        backwards.ShouldBe(forwards);
    }

    [Fact]
    public void ResolveAssignment_AKnownBranchBeatsAnUnknownOne_AtTheSamePriority()
    {
        // The sentinel distance has to lose to a real one, however far away the real branch is -
        // otherwise a missing dealer_branches row would quietly win territory from a dealer who has one.
        IReadOnlyList<TerritoryRule> rules =
        [
            new TerritoryRule("alpha", "alpha-ghost", TerritoryLevels.County, "48201", 1),
            new TerritoryRule("gulf", "gulf-west", TerritoryLevels.County, "48201", 1),
        ];

        var assignment = TerritoryAssigner.Assign(
            new AssignmentSubject("77002", "48201", 29.7604, -95.3698),
            rules,
            _fixtureBranches);

        assignment.DealerId.ShouldBe(
            "gulf",
            "gulf-west is 44 km away and alpha-ghost is nowhere. 'alpha' sorts first, so if that is the "
            + "answer the distance comparison was skipped rather than lost.");
    }

    [Fact]
    public void ResolveAssignment_ARuleWhoseBranchIsUnknown_StillAssignsTheDealer()
    {
        // A branch missing from dealer_branches is a data problem, not a reason to drop the lead on the
        // floor: §7.4's gap is about territory coverage, not about branch bookkeeping.
        var assignment = TerritoryAssigner.Assign(
            new AssignmentSubject("77506", "48201", 29.7184, -95.2285),
            [new TerritoryRule("bay", "bay-ghost", TerritoryLevels.Zip, "77506", 1)],
            _fixtureBranches);

        assignment.DealerId.ShouldBe("bay");
        assignment.Assignment.ShouldBe(Assignments.Auto, "it is assigned, not a gap.");
    }

    [Fact]
    public void ResolveAssignment_NoZipOnTheLead_FallsBackToTheCounty()
    {
        // Overture leaves the postcode empty on plenty of rows; the county always comes from the
        // spatial join, so it is the reliable half.
        var assignment = TerritoryAssigner.Assign(
            new AssignmentSubject(null, "48339", 30.3119, -95.4561),
            _fixtureRules,
            _fixtureBranches);

        assignment.DealerId.ShouldBe("pine");
        assignment.MatchedLevel.ShouldBe(TerritoryLevels.County);
    }

    [Fact]
    public void ResolveAssignment_NoRulesAtAll_IsAGapRatherThanAFailure()
    {
        // The state every user is in before import_list runs, and what find_candidates reports as
        // coverageGaps until they do.
        var assignment = TerritoryAssigner.Assign(
            new AssignmentSubject("77494", "48157", 29.7858, -95.8245),
            [],
            []);

        assignment.Assignment.ShouldBe(Assignments.Gap);
        assignment.DealerId.ShouldBeNull();
    }

    [Fact]
    public void ResolveAssignment_IsStableAcrossTheOrderTheRulesArriveIn()
    {
        // NFR-3: a re-run must give the same answer, and the rules come back from SQLite in no
        // guaranteed order.
        var subject = new AssignmentSubject("77002", "48201", 29.7604, -95.3698);
        IReadOnlyList<TerritoryRule> rules =
        [
            new TerritoryRule("gulf", "gulf-west", TerritoryLevels.County, "48201", 1),
            new TerritoryRule("bay", "bay-pas", TerritoryLevels.County, "48201", 1),
            new TerritoryRule("pine", "pine-north", TerritoryLevels.County, "48201", 1),
        ];

        var forwards = TerritoryAssigner.Assign(subject, rules, _fixtureBranches);
        var backwards = TerritoryAssigner.Assign(subject, [.. rules.Reverse()], _fixtureBranches);

        backwards.ShouldBe(forwards, "the same inputs in a different order must give the same dealer.");
    }

    [Fact]
    public void ResolveAssignment_OverTheWholeFixture_ReachesEveryDealerAndExactlyOneGap()
    {
        // The arithmetic find_candidates' byDealer and coverageGaps have to match, computed from the
        // committed lists rather than asserted twice.
        var profile = SampleProfile.Load();

        var leads = SamplePlaces.All
            .Where(place => profile.Selects(place) && !profile.IsExcluded(place))
            .Select(place => (place.Id, Assignment: Assign(place)))
            .ToList();

        leads.Count(row => row.Assignment.Assignment == Assignments.Gap).ShouldBe(
            1,
            "only the San Jacinto row. Every other Houston county has a territory rule.");
        leads.Where(row => row.Assignment.Assignment == Assignments.Gap).Select(row => row.Id)
            .ShouldBe([SampleDealers.CoverageGapPlaceId]);

        leads.Where(row => row.Assignment.DealerId is not null)
            .Select(row => row.Assignment.DealerId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList()
            .ShouldBe(["bay", "gulf", "pine"], "all three dealers get leads, so byDealer has three rows.");

        leads.Count(row => row.Assignment.MatchedLevel == TerritoryLevels.Zip).ShouldBe(
            27,
            "27 of the 86 rows the profile keeps carry a ZIP another dealer owns, which is what makes "
            + "the override path worth having. If this is 0 the ZIP rules were never consulted; if it "
            + "is 86 the county rules were not.");
        leads.Count(row => row.Assignment.MatchedLevel == TerritoryLevels.County).ShouldBe(58);
    }

    private static TerritoryAssignment Assign(SamplePlace place) =>
        TerritoryAssigner.Assign(
            new AssignmentSubject(Zip5(place.Postcode), place.CountyFips, place.Lat, place.Lon),
            _fixtureRules,
            _fixtureBranches);

    private static string Zip5(string postcode) => postcode.Length > 5 ? postcode[..5] : postcode;
}
