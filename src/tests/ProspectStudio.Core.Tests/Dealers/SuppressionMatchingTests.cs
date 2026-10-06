using ProspectStudio.Core.Candidates;
using ProspectStudio.Core.Dealers;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Core.Tests.Dealers;

/// <summary>
/// Suppression matching, technical-design §7.3: "A lead is suppressed if any suppression row matches
/// by <strong>domain</strong>, by <strong>exact <c>name_norm</c></strong> (with the same ZIP when the
/// row has one), or by <strong>fuzzy name ≥ 0.92 with the same ZIP</strong>. Store the reason and the
/// matching row id. Dealers and competitors are suppression reasons like any other."
/// </summary>
public class SuppressionMatchingTests
{
    private const string CustomerId = "sup_customer";
    private const string DncId = "sup_dnc";
    private const string DealerId = "sup_dealer";

    /// <summary>A small hand-made list, so each rule can be isolated from the others.</summary>
    private static readonly IReadOnlyList<SuppressionRule> _rules =
    [
        new SuppressionRule(CustomerId, "coastal crane and rigging", "coastalcrane.example", "77029", SuppressionReasons.Customer),
        new SuppressionRule(DncId, "northfield storage", null, "77060", SuppressionReasons.Dnc),
        new SuppressionRule(DealerId, "gulf lift equipment", "gulflift.example", "77494", SuppressionReasons.Dealer),
    ];

    [Fact]
    public void Match_SameRegistrableDomain_SuppressesWithTheRowsReasonAndId()
    {
        var hit = SuppressionMatcher.Match(
            new SuppressionSubject("gulf lift equipment of katy", "gulflift.example", "77493"),
            _rules);

        hit.ShouldNotBeNull("the domain is the same company, whatever the name and ZIP say.");
        hit.SuppressionId.ShouldBe(
            DealerId,
            "§7.3: 'Store the reason AND the matching row id.' A reason alone cannot answer 'which list "
            + "put this lead on hold?', which is the first question the marketer asks.");
        hit.Reason.ShouldBe(SuppressionReasons.Dealer, "dealers are a reason like any other.");
        hit.Rule.ShouldBe(SuppressionRules.Domain);
    }

    [Fact]
    public void Match_DomainIgnoresWwwAndSubdomains_TheSameWayDedupeDoes()
    {
        // The C4 registrable-domain rule (§7.2): the last two labels after dropping 'www.'. Suppression
        // has to use the same rule, or a list entered as a bare host would miss a lead whose Overture
        // website is a full URL.
        foreach (var website in new[] { "https://www.gulflift.example/contact", "http://gulflift.example", "shop.gulflift.example" })
        {
            var domain = DomainKey.For(website);

            SuppressionMatcher.Match(new SuppressionSubject("unrelated name", domain, "77494"), _rules)
                .ShouldNotBeNull($"'{website}' reduces to '{domain}', which is a suppressed domain.");
        }
    }

    [Fact]
    public void Match_GenericHost_IsNotAnIdentityAndDoesNotSuppress()
    {
        // §7.2's generic-host list exists because two unrelated companies can both list a Facebook
        // page. DomainKey returns null for them, and a null domain must never match anything.
        IReadOnlyList<SuppressionRule> rules =
            [new SuppressionRule("sup_generic", "some customer", "facebook.com", "77002", SuppressionReasons.Customer)];

        DomainKey.For("https://www.facebook.com/somecustomer").ShouldBeNull("the C4 rule, restated here.");

        SuppressionMatcher.Match(new SuppressionSubject("another company", null, "77002"), rules)
            .ShouldBeNull("a lead with no usable domain must not be suppressed by a row with none either.");
    }

    [Fact]
    public void Match_ExactNameAndSameZip_SuppressesEvenWithNoDomainOnEitherSide()
    {
        var hit = SuppressionMatcher.Match(
            new SuppressionSubject("northfield storage", "northfieldstorage.example", "77060"),
            _rules);

        hit.ShouldNotBeNull(
            "the dnc row has no domain at all, so the exact-name path is the only thing that can reach "
            + "it. This is the path that had no subject in the fixture until C5 added one.");
        hit.SuppressionId.ShouldBe(DncId);
        hit.Reason.ShouldBe(SuppressionReasons.Dnc);
        hit.Rule.ShouldBe(SuppressionRules.Name);
        hit.Similarity.ShouldBe(1.0, 1e-9, "an exact name_norm match.");
    }

    [Fact]
    public void Match_ExactNameButADifferentZip_DoesNotSuppress()
    {
        // §7.3: "by exact name_norm (with the same ZIP when the row has one)". Company names repeat
        // across a state, so dropping the ZIP test would suppress a real prospect.
        SuppressionMatcher.Match(
                new SuppressionSubject("northfield storage", null, "77002"),
                _rules)
            .ShouldBeNull("same name, different ZIP, and the suppression row has a ZIP.");
    }

    [Fact]
    public void Match_ExactNameAndTheRowHasNoZip_SuppressesOnTheNameAlone()
    {
        // The other half of the same sentence: "when the row has one". A list without ZIPs still works.
        IReadOnlyList<SuppressionRule> rules =
            [new SuppressionRule("sup_nozip", "northfield storage", null, null, SuppressionReasons.Dnc)];

        var hit = SuppressionMatcher.Match(new SuppressionSubject("northfield storage", null, "77002"), rules);

        hit.ShouldNotBeNull("the row names no ZIP, so there is no ZIP to disagree about.");
        hit.Rule.ShouldBe(SuppressionRules.Name);
    }

    [Fact]
    public void Match_FuzzyNameAtOrAboveTheThresholdWithTheSameZip_Suppresses()
    {
        var subject = NameNormalizer.Normalize("Coastal Crane and Rigging Group");
        var score = JaroWinkler.Similarity(subject, "coastal crane and rigging");

        score.ShouldBeInRange(
            SuppressionMatcher.FuzzyThreshold,
            0.999,
            $"the subject has to be above the threshold without being equal; it scores {score:F4}.");

        var hit = SuppressionMatcher.Match(new SuppressionSubject(subject, "coastalcranegroup.example", "77029"), _rules);

        hit.ShouldNotBeNull();
        hit.SuppressionId.ShouldBe(CustomerId);
        hit.Reason.ShouldBe(SuppressionReasons.Customer);
        hit.Rule.ShouldBe(
            SuppressionRules.Fuzzy,
            "the names are not equal and the domains differ, so only §7.3's Jaro-Winkler rule can match.");
        hit.Similarity.ShouldBe(score, 1e-9);
    }

    [Fact]
    public void Match_FuzzyNameJustBelowTheThreshold_DoesNotSuppress()
    {
        var subject = NameNormalizer.Normalize("Coastal Crane and Haul");
        var score = JaroWinkler.Similarity(subject, "coastal crane and rigging");

        score.ShouldBeInRange(
            0.90,
            SuppressionMatcher.FuzzyThreshold - 1e-9,
            $"the control sits between §7.9's 0.90 matchback threshold and §7.3's 0.92, scoring "
            + $"{score:F4}. That gap is the point: using the wrong constant fails here.");

        SuppressionMatcher.Match(new SuppressionSubject(subject, "coastalcranehaul.example", "77029"), _rules)
            .ShouldBeNull(
                "a fuzzy rule with no negative case is half a test. Suppressing this lead would lose a "
                + "real prospect with no way for the marketer to see why.");
    }

    [Fact]
    public void Match_FuzzyNameWithADifferentZip_DoesNotSuppress()
    {
        var subject = NameNormalizer.Normalize("Coastal Crane and Rigging Group");

        SuppressionMatcher.Match(new SuppressionSubject(subject, null, "77002"), _rules)
            .ShouldBeNull("§7.3's fuzzy rule is 'fuzzy name ≥ 0.92 WITH THE SAME ZIP'. Without the ZIP "
                + "test, every 'Houston Electric' in Texas would suppress every other one.");
    }

    [Fact]
    public void Match_FuzzyNameWhenTheRowHasNoZip_DoesNotSuppress()
    {
        // The exact-name rule relaxes its ZIP test for a row with no ZIP; the fuzzy rule does not,
        // because §7.3 states the ZIP unconditionally for it. A fuzzy match with no locality check is
        // how a suppression list starts eating prospects.
        IReadOnlyList<SuppressionRule> rules =
            [new SuppressionRule("sup_nozip", "coastal crane and rigging", null, null, SuppressionReasons.Customer)];

        SuppressionMatcher.Match(
                new SuppressionSubject(NameNormalizer.Normalize("Coastal Crane and Rigging Group"), null, "77029"),
                rules)
            .ShouldBeNull("the row has no ZIP, so there is nothing for the fuzzy rule's ZIP test to match.");
    }

    [Fact]
    public void Match_NothingMatches_ReturnsNull()
    {
        SuppressionMatcher.Match(new SuppressionSubject("bayou fulfillment", "bayoufulfillment.example", "77494"), _rules)
            .ShouldBeNull("most leads are not suppressed, and that is not an error.");
    }

    [Fact]
    public void Match_AnEmptySuppressionList_SuppressesNothing()
    {
        SuppressionMatcher.Match(new SuppressionSubject("gulf lift equipment", "gulflift.example", "77494"), [])
            .ShouldBeNull("before import_list runs there is nothing to suppress against.");
    }

    [Fact]
    public void Match_DomainBeatsTheNameRules_WhenBothCouldFire()
    {
        // The third dealer's places row matches by domain AND by exact name at the same ZIP. §7.3 lists
        // domain first, so that is the rule recorded - and the recorded rule is the only way to tell
        // the two paths apart in the fixture-wide test below.
        IReadOnlyList<SuppressionRule> rules =
            [new SuppressionRule(DealerId, "pineland equipment", "pinelandequip.example", "77301", SuppressionReasons.Dealer)];

        var hit = SuppressionMatcher.Match(
            new SuppressionSubject("pineland equipment", "pinelandequip.example", "77301"),
            rules);

        hit.ShouldNotBeNull();
        hit.Rule.ShouldBe(SuppressionRules.Domain, "§7.3 lists domain first.");
    }

    [Fact]
    public void Match_WhenSeveralRowsMatch_TheRuleKindDecidesBeforeTheListOrder()
    {
        // A lead can be on the list twice - a company acquired by a customer, say, where one row names
        // the domain and another the old name. §7.3 orders the three rules, so the earlier rule wins
        // whichever row happens to come first in the list.
        IReadOnlyList<SuppressionRule> rules =
        [
            new SuppressionRule("sup_name", "northfield storage", null, "77060", SuppressionReasons.Dnc),
            new SuppressionRule("sup_domain", "something else entirely", "northfieldstorage.example", "77060", SuppressionReasons.Customer),
        ];

        var subject = new SuppressionSubject("northfield storage", "northfieldstorage.example", "77060");

        var hit = SuppressionMatcher.Match(subject, rules);

        hit.ShouldNotBeNull();
        hit.Rule.ShouldBe(
            SuppressionRules.Domain,
            "§7.3 lists domain first, so it is checked across the whole list before any name is. The "
            + "domain row is second here, so an implementation that walked the list once and took the "
            + "first row matching any rule would report the dnc row instead.");
        hit.Reason.ShouldBe(SuppressionReasons.Customer);
        hit.SuppressionId.ShouldBe("sup_domain");
    }

    [Fact]
    public void Match_WhenTwoRowsMatchOnTheSameRule_TheFirstInTheSuppliedOrderWins()
    {
        // Pinned rather than designed. Two rows that match on the *same* rule are separated only by the
        // order the store handed them over, and EfDealerStore orders by id - which is a content hash, so
        // the winner is deterministic but arbitrary. Recorded in the decisions log; this test exists so
        // that if the precedence ever changes, it changes visibly instead of quietly relabelling a
        // lead's reason from 'dealer' to 'competitor'.
        var first = new SuppressionRule("sup_0001", "apex aerial rentals", "apexaerialrentals.example", "77041", SuppressionReasons.Competitor);
        var second = new SuppressionRule("sup_0002", "apex aerial rentals", "apexaerialrentals.example", "77041", SuppressionReasons.Dealer);

        var subject = new SuppressionSubject("apex aerial rentals", "apexaerialrentals.example", "77041");

        SuppressionMatcher.Match(subject, [first, second]).ShouldNotBeNull().Reason
            .ShouldBe(SuppressionReasons.Competitor, "the first row supplied wins.");
        SuppressionMatcher.Match(subject, [second, first]).ShouldNotBeNull().Reason
            .ShouldBe(
                SuppressionReasons.Dealer,
                "and reversing the list reverses the answer, which is exactly why the store's ordering "
                + "is load-bearing and why this is a pin rather than a design.");
    }

    [Fact]
    public void Match_OverTheRealLists_SuppressesExactlyTheSevenDocumentedLeads()
    {
        // The whole committed suppression list against every row the sample profile keeps. This is the
        // arithmetic find_candidates' `suppressed` breakdown has to reproduce.
        var rules = Rules();
        var profile = SampleProfile.Load();

        var suppressed = SamplePlaces.All
            .Where(place => profile.Selects(place) && !profile.IsExcluded(place))
            .Select(place => (place.Id, Hit: SuppressionMatcher.Match(Subject(place), rules)))
            .Where(row => row.Hit is not null)
            .ToDictionary(row => row.Id, row => row.Hit!, StringComparer.Ordinal);

        suppressed.Keys.Order(StringComparer.Ordinal).ToList().ShouldBe(
            ["fx_0015", "fx_0016", "fx_0019", "fx_0020", "fx_0117", "fx_0118", "fx_0119"],
            "the two dealers and the competitor C4 placed in target categories, the customer domain, "
            + "and C5's three new rows. fx_0017 and fx_0018 are 'contractor', which no segment asks "
            + "for, so they never become leads and cannot be suppressed - which is why C5 had to add "
            + "rows rather than lean on them.");

        var byReason = suppressed
            .GroupBy(row => row.Value.Reason)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        // Every §5.2 reason the list uses appears, so none of find_candidates' suppressed keys is
        // unreachable.
        byReason.ShouldContainKeyAndValue(SuppressionReasons.Dealer, 3, "all three dealers in dealers.csv.");
        byReason.ShouldContainKeyAndValue(SuppressionReasons.Customer, 2, "one by domain, one by fuzzy name.");
        byReason.ShouldContainKeyAndValue(SuppressionReasons.Dnc, 1, "the only route is the exact-name rule.");
        byReason.ShouldContainKeyAndValue(SuppressionReasons.Competitor, 1);
        byReason.Values.Sum().ShouldBe(7);

        suppressed[SampleDealers.NameMatchPlaceId].Rule.ShouldBe(
            SuppressionRules.Name,
            "the dnc row has no domain, so this one can only be reached by the exact-name path.");
        suppressed[SampleDealers.FuzzyMatchPlaceId].Rule.ShouldBe(
            SuppressionRules.Fuzzy,
            "and this one only by the Jaro-Winkler path. Between them the two rules that an "
            + "outcome-only test cannot distinguish are both pinned.");

        suppressed.Keys.ShouldNotContain(
            SampleDealers.FuzzyControlPlaceId,
            "the negative control shares the ZIP and scores 0.908, so it must survive.");
    }

    [Fact]
    public void Match_OverTheRealLists_RecordsTheRowTheMarketerCanLookUp()
    {
        var rules = Rules();

        var hit = SuppressionMatcher.Match(Subject(SamplePlaces.Row(SampleDealers.ThirdDealerPlaceId)), rules);

        hit.ShouldNotBeNull();
        rules.Single(rule => rule.Id == hit.SuppressionId).NameNorm.ShouldBe(
            NameNormalizer.Normalize("Pineland Equipment"),
            "the recorded id has to point at the row that actually matched, not at the first row of the "
            + "list or at a reason-shaped string.");
    }

    /// <summary>The committed suppression list in the shape §7.3 consumes.</summary>
    private static IReadOnlyList<SuppressionRule> Rules() =>
    [
        .. SampleDealers.Suppression.Select(row => new SuppressionRule(
            $"sup_{row.Row:D4}",
            NameNormalizer.Normalize(row.CompanyName),
            DomainKey.For(row.Domain),
            row.Zip.Length == 0 ? null : row.Zip,
            row.Reason)),
    ];

    private static SuppressionSubject Subject(SamplePlace place) =>
        new(
            NameNormalizer.Normalize(place.Name),
            DomainKey.For(place.Websites.FirstOrDefault()),
            place.Postcode.Length > 5 ? place.Postcode[..5] : place.Postcode);
}
