using ProspectStudio.Core.Candidates;
using Shouldly;

namespace ProspectStudio.Core.Tests.Candidates;

/// <summary>
/// Dedupe within a campaign, technical-design §7.2. Each of the three rules gets a pair that
/// <strong>only that rule</strong> can collapse, plus a control pair it must leave alone - otherwise
/// an implementation with two of the three rules passes.
/// </summary>
public class CandidateDedupeTests
{
    // Two points in Katy, 2.2 km apart: far too far for either place rule.
    private const double KatyLat = 29.7858;
    private const double KatyLon = -95.8245;
    private const double FarKatyLat = 29.776331;
    private const double FarKatyLon = -95.804646;

    [Fact]
    public void Rule1_SameDomain_CollapsesEvenWhenTheNamesAndPlacesDiffer()
    {
        var keeper = CandidateFixtures.Site("a", "bayou fulfillment", KatyLat, KatyLon, 0.95, "https://www.bayoufulfillment.example");
        var duplicate = CandidateFixtures.Site("b", "brazos way fulfillment", FarKatyLat, FarKatyLon, 0.71, "https://bayoufulfillment.example/contact");

        var groups = CandidateDedupe.Group([keeper, duplicate]);

        groups.Count.ShouldBe(1, "one domain means one company (§7.2 rule 1).");
        groups[0].Primary.OvertureId.ShouldBe("a", "the highest-confidence record becomes the lead.");
        groups[0].Duplicates.Select(site => site.OvertureId).ShouldBe(["b"]);
        groups[0].Rule.ShouldBe(
            DedupeRules.Domain,
            "nothing else can have merged these two: Jaro-Winkler on the names is 0.85 and they are "
            + "2.2 km apart.");
    }

    [Fact]
    public void Rule1_IgnoresGenericHosts()
    {
        var left = CandidateFixtures.Site("a", "addicks barker distribution", 29.82810, -95.63540, 0.88, "https://www.facebook.com/addicksbarkerdist");
        var right = CandidateFixtures.Site("b", "clodine machine works", 29.63720, -95.64830, 0.86, "https://www.facebook.com/clodinemachineworks");

        CandidateDedupe.Group([left, right]).Count.ShouldBe(
            2,
            "a shared Facebook page is not a shared identity (§7.2 rule 1); merging these would lose a "
            + "real company every time two of them use the same platform.");
    }

    [Fact]
    public void Rule2_SameNameInTheSameGeohashCell_Collapses()
    {
        // The real fixture pair: 88 m apart, same cell 9vk5s6h, and neither has a website, so rule 1
        // cannot fire.
        var keeper = CandidateFixtures.From("fx_0025", "pecan storage and distribution");
        var duplicate = CandidateFixtures.From("fx_0113", "pecan storage and distribution");

        var groups = CandidateDedupe.Group([keeper, duplicate]);

        groups.Count.ShouldBe(1);
        groups[0].Primary.OvertureId.ShouldBe("fx_0025", "0.82 beats 0.70.");
        groups[0].Rule.ShouldBe(
            DedupeRules.NamePlace,
            "rule 2 comes before rule 3, and both would match here - so a group reported as 'fuzzy' "
            + "means rule 2 is missing or the rules are applied out of order.");
    }

    [Fact]
    public void Rule3_SameNameAcrossAGeohashBoundary_StillCollapses()
    {
        // The real fixture pair: 15 m apart but in different geohash-7 cells (9vk11mq / 9vk11mw), and
        // the duplicate has no website. Only rule 3 can catch it.
        var keeper = CandidateFixtures.From("fx_0007", "westpark metal fab");
        var duplicate = CandidateFixtures.From("fx_0014", "westpark metal fab");

        var groups = CandidateDedupe.Group([keeper, duplicate]);

        groups.Count.ShouldBe(
            1,
            "two records of the same company 15 m apart must merge. They are in different geohash-7 "
            + "cells, so rule 2 misses them and rule 3 (Jaro-Winkler >= 0.92 within 200 m) is the only "
            + "thing that can.");
        groups[0].Primary.OvertureId.ShouldBe("fx_0007", "0.90 beats 0.69.");
        groups[0].Rule.ShouldBe(DedupeRules.Fuzzy);
    }

    [Fact]
    public void Rule3_SameNameBeyondTheWindow_DoesNotCollapse()
    {
        // fx_0017 / fx_0018: identical name_norm, 1.4 km apart. Two branches of one firm are two sites,
        // and in C5 they are two suppression matches - merging them would hide one.
        var left = CandidateFixtures.From("fx_0017", "coastal crane and rigging");
        var right = CandidateFixtures.From("fx_0018", "coastal crane and rigging");

        CandidateDedupe.Group([left, right]).Count.ShouldBe(
            2,
            $"{CandidateDedupe.FuzzyRadiusMeters} m is the window; these are 1,414 m apart.");
    }

    [Fact]
    public void A_near_identical_name_sixty_kilometres_away_is_a_different_company()
    {
        // fx_0065 / fx_0066: Jaro-Winkler 0.98, Pasadena and Cypress. The distance is the only thing
        // keeping them apart.
        var left = CandidateFixtures.From("fx_0065", "summit signs");
        var right = CandidateFixtures.From("fx_0066", "summit sign");

        CandidateDedupe.Group([left, right]).Count.ShouldBe(2);
    }

    [Fact]
    public void Two_different_companies_a_hundred_metres_apart_stay_separate()
    {
        // fx_0032 / fx_0045: 115 m apart, Jaro-Winkler 0.62. Proximity alone must merge nothing.
        var left = CandidateFixtures.From("fx_0032", "frontier 3pl services");
        var right = CandidateFixtures.From("fx_0045", "buffalo industries");

        CandidateDedupe.Group([left, right]).Count.ShouldBe(2);
    }

    [Fact]
    public void The_highest_confidence_record_wins_whatever_order_it_arrives_in()
    {
        var weak = CandidateFixtures.Site("weak", "westpark metal fab", 29.7372, -95.5618, 0.69);
        var strong = CandidateFixtures.Site("strong", "westpark metal fab", 29.7373, -95.5617, 0.90);

        CandidateDedupe.Group([weak, strong])[0].Primary.OvertureId.ShouldBe("strong");
        CandidateDedupe.Group([strong, weak])[0].Primary.OvertureId.ShouldBe("strong");
    }

    [Fact]
    public void Duplicates_keep_their_own_record_so_a_merge_can_be_explained()
    {
        var keeper = CandidateFixtures.Site("a", "westpark metal fab", 29.7372, -95.5618, 0.90);
        var duplicate = CandidateFixtures.Site("b", "westpark metal fab", 29.7373, -95.5617, 0.69);

        var group = CandidateDedupe.Group([keeper, duplicate]).Single();

        group.Duplicates.ShouldHaveSingleItem().PayloadJson.ShouldBe(
            duplicate.PayloadJson,
            "§7.2: 'Mark the others duplicate and keep their source records.' A merge that throws the "
            + "absorbed row away cannot be audited or undone.");
    }

    [Fact]
    public void Grouping_is_deterministic()
    {
        var sites = new[]
        {
            CandidateFixtures.Site("a", "westpark metal fab", 29.7372, -95.5618, 0.90),
            CandidateFixtures.Site("b", "westpark metal fab", 29.7373, -95.5617, 0.90),
            CandidateFixtures.Site("c", "summit signs", 29.657976, -95.197889, 0.91),
        };

        var first = CandidateDedupe.Group(sites).Select(group => group.Ids().Order(StringComparer.Ordinal).ToList()).ToList();
        var second = CandidateDedupe.Group(sites).Select(group => group.Ids().Order(StringComparer.Ordinal).ToList()).ToList();

        second.ShouldBe(
            first,
            "equal confidences must not leave the primary to chance: find_candidates is re-run, and a "
            + "lead that changed identity between runs would break every reference to it.");
    }

    [Fact]
    public void A_single_record_is_a_group_of_one()
    {
        var groups = CandidateDedupe.Group([CandidateFixtures.Site("a", "lone star racking and shelving", 29.60006, -95.551579, 0.84)]);

        groups.ShouldHaveSingleItem();
        groups[0].Duplicates.ShouldBeEmpty();
        groups[0].Rule.ShouldBe(DedupeRules.None, "nothing merged into it.");
    }

    [Fact]
    public void Nothing_in_means_nothing_out() =>
        CandidateDedupe.Group([]).ShouldBeEmpty();

    [Fact]
    public void A_chain_of_matches_collapses_into_one_group()
    {
        // A matches B by domain; B matches C by name and place; A matches C by nothing at all. §7.2
        // treats the matches as edges and takes connected components, so all three are one company.
        var a = CandidateFixtures.Site("a", "alpha logistics", KatyLat, KatyLon, 0.80, "https://www.chain.example");
        var b = CandidateFixtures.Site("b", "beta freight", 30.158697, -95.431533, 0.95, "https://chain.example/contact");
        var c = CandidateFixtures.Site("c", "beta freight", 30.158097, -95.432133, 0.70);

        var groups = CandidateDedupe.Group([a, b, c]);

        groups.Count.ShouldBe(
            1,
            "handling pairs independently gives two groups here and which two depends on row order. "
            + "§7.2: 'Treat the matches as edges and take connected components.'");

        groups[0].Primary.OvertureId.ShouldBe(
            "b",
            "the highest confidence in the whole component, not the winner of some pair: 0.95 beats "
            + "0.80 and 0.70.");
        groups[0].Ids().Order(StringComparer.Ordinal).ToList().ShouldBe(["a", "b", "c"]);
    }

    [Fact]
    public void A_chain_collapses_the_same_way_whatever_order_the_rows_arrive_in()
    {
        var a = CandidateFixtures.Site("a", "alpha logistics", KatyLat, KatyLon, 0.80, "https://www.chain.example");
        var b = CandidateFixtures.Site("b", "beta freight", 30.158697, -95.431533, 0.95, "https://chain.example/contact");
        var c = CandidateFixtures.Site("c", "beta freight", 30.158097, -95.432133, 0.70);

        foreach (var order in new[]
                 {
                     new[] { a, b, c }, new[] { c, b, a }, new[] { b, a, c }, new[] { c, a, b },
                 })
        {
            var groups = CandidateDedupe.Group(order);

            groups.Count.ShouldBe(1, $"order {string.Join(",", order.Select(site => site.OvertureId))}");
            groups[0].Primary.OvertureId.ShouldBe(
                "b",
                "an order-dependent primary means a re-run of find_candidates can hand the same "
                + "company a different lead id, which breaks idempotency (NFR-3) silently. Order "
                + $"{string.Join(",", order.Select(site => site.OvertureId))}");
        }
    }

    [Fact]
    public void A_chain_does_not_drag_in_an_unrelated_neighbour()
    {
        // Transitivity must follow edges, not proximity: D sits 115 m from C with an unrelated name and
        // no shared domain, so it stays its own company however large the component next to it grows.
        var a = CandidateFixtures.Site("a", "alpha logistics", KatyLat, KatyLon, 0.80, "https://www.chain.example");
        var b = CandidateFixtures.Site("b", "beta freight", 30.158697, -95.431533, 0.95, "https://chain.example/contact");
        var c = CandidateFixtures.Site("c", "beta freight", 30.158097, -95.432133, 0.70);
        var d = CandidateFixtures.Site("d", "gamma industries", 30.157400, -95.431900, 0.88, "https://www.gamma.example");

        var groups = CandidateDedupe.Group([a, b, c, d]);

        groups.Count.ShouldBe(2);
        groups.GroupWith("d").Duplicates.ShouldBeEmpty();
        groups.GroupWith("a").Ids().Order(StringComparer.Ordinal).ToList().ShouldBe(["a", "b", "c"]);
    }

    [Fact]
    public void The_three_fixture_pairs_collapse_together_and_nothing_else_does()
    {
        // All six rows at once, plus three controls, so a rule cannot be satisfied in isolation and
        // then over-merge when the real set arrives.
        var sites = new[]
        {
            CandidateFixtures.From("fx_0001", "bayou fulfillment"),
            CandidateFixtures.From("fx_0013", "brazos way fulfillment"),
            CandidateFixtures.From("fx_0007", "westpark metal fab"),
            CandidateFixtures.From("fx_0014", "westpark metal fab"),
            CandidateFixtures.From("fx_0025", "pecan storage and distribution"),
            CandidateFixtures.From("fx_0113", "pecan storage and distribution"),
            CandidateFixtures.From("fx_0065", "summit signs"),
            CandidateFixtures.From("fx_0066", "summit sign"),
            CandidateFixtures.From("fx_0104", "addicks barker distribution"),
        };

        var groups = CandidateDedupe.Group(sites);

        groups.Count.ShouldBe(6, "nine records, three duplicate pairs.");
        groups.GroupWith("fx_0013").Primary.OvertureId.ShouldBe("fx_0001");
        groups.GroupWith("fx_0014").Primary.OvertureId.ShouldBe("fx_0007");
        groups.GroupWith("fx_0113").Primary.OvertureId.ShouldBe("fx_0025");
        groups.Sum(group => group.Duplicates.Count).ShouldBe(3);
    }
}
