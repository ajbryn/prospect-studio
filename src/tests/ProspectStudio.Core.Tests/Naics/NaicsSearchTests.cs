using ProspectStudio.Core.Naics;
using Shouldly;

namespace ProspectStudio.Core.Tests.Naics;

/// <summary>
/// The keyword ranking behind <c>lookup_naics</c>, against a hand-written table small enough to reason
/// about. The rule (implementation-plan C2) is <strong>common-prefix scoring</strong>: a query token
/// matches a title token when either is a prefix of the other (minimum 4 characters) <em>or</em> they
/// share a common prefix of at least 6 characters; rank by matched-token count first, then by total
/// common-prefix length. The two spec examples are also asserted end to end against the real committed
/// table in <c>ProspectStudio.Mcp.Tests/LookupNaicsContractTests.cs</c>.
/// </summary>
public class NaicsSearchTests
{
    /// <summary>
    /// Real codes and titles from NAICS 2022, chosen so each test has a plausible wrong answer to beat.
    /// <c>455211</c> is in here on purpose: it is the trap for "warehouse".
    /// </summary>
    private static readonly NaicsEntry[] Table =
    [
        new("23", "Construction", 2),
        new("238", "Specialty Trade Contractors", 3),
        new("2382", "Building Equipment Contractors", 4),
        new("238210", "Electrical Contractors and Other Wiring Installation Contractors", 6),
        new("238220", "Plumbing, Heating, and Air-Conditioning Contractors", 6),
        new("221111", "Hydroelectric Power Generation", 6),
        new("4931", "Warehousing and Storage", 4),
        new("493110", "General Warehousing and Storage", 6),
        new("493120", "Refrigerated Warehousing and Storage", 6),
        new("455211", "Warehouse Clubs and Supercenters", 6),
        new("423710", "Hardware Merchant Wholesalers", 6),
        new("4238", "Machinery, Equipment, and Supplies Merchant Wholesalers", 4),
        new("532412", "Construction, Mining, and Forestry Machinery and Equipment Rental and Leasing", 6),
    ];

    [Fact]
    public void Rank_electrical_contractor_puts_the_electrical_contractors_industry_first()
    {
        var results = NaicsSearch.Rank(Table, "electrical contractor", 10);

        results.ShouldNotBeEmpty();
        results[0].Code.ShouldBe(
            "238210",
            "it is the only title matching both query words, and matched-token count is scored first.");
    }

    [Fact]
    public void Rank_prefers_a_title_matching_every_query_word_over_one_matching_only_some()
    {
        var results = NaicsSearch.Rank(Table, "electrical contractor", 10).Select(entry => entry.Code).ToList();

        var electrical = results.IndexOf("238210");
        var plumbing = results.IndexOf("238220");

        electrical.ShouldBeGreaterThanOrEqualTo(0);
        if (plumbing >= 0)
        {
            electrical.ShouldBeLessThan(
                plumbing,
                "'Plumbing, Heating, and Air-Conditioning Contractors' matches only 'contractor'.");
        }
    }

    [Fact]
    public void Rank_matches_a_word_whose_prefix_the_query_is()
    {
        // "electric" is not a word in any title; "Electrical" is. The query is a prefix of it, 8 of its
        // 8 characters, which is both >= 4 and >= 6.
        var results = NaicsSearch.Rank(Table, "electric", 10).Select(entry => entry.Code).ToList();

        results.ShouldContain("238210");
    }

    [Fact]
    public void Rank_does_not_match_a_query_buried_inside_a_longer_word()
    {
        // "electric" and "hydroelectric" share no common prefix at all - they differ at the first
        // character - so substring matching is explicitly not what the rule asks for.
        NaicsSearch.Rank(Table, "electric", 10).Select(entry => entry.Code).ShouldNotContain(
            "221111",
            "'Hydroelectric Power Generation' contains 'electric' but does not begin a word with it.");
    }

    [Fact]
    public void Rank_matches_two_words_that_only_share_a_prefix()
    {
        // Neither "warehouse" nor "warehousing" is a prefix of the other - they diverge at
        // 'warehous|e' vs 'warehous|i' - but they share 8 characters, which is the >= 6 arm of the rule.
        // This is the case the original "token match + prefix boost" wording could not reach.
        var results = NaicsSearch.Rank(Table, "warehouse", 10).Select(entry => entry.Code).ToList();

        results.ShouldContain("4931");
        results.ShouldContain("493110");
    }

    [Fact]
    public void Rank_needs_six_shared_characters_when_neither_word_is_a_prefix_of_the_other()
    {
        // "machinist" and "Machinery" share exactly 6 ("machin"), so they match at the boundary.
        NaicsSearch.Rank(Table, "machinist", 10).Select(entry => entry.Code).ShouldContain("4238");

        // "stormy" and "Storage" share only 4 ("stor"), so they do not.
        NaicsSearch.Rank(Table, "stormy", 10).ShouldBeEmpty(
            "'stor' is 4 shared characters, under the 6 the rule requires when neither word is a prefix.");
    }

    [Fact]
    public void Rank_ignores_a_prefix_match_shorter_than_four_characters()
    {
        NaicsSearch.Rank(Table, "con", 10).ShouldBeEmpty(
            "'con' prefixes 'Construction' and 'Contractors', but a 3-character prefix is below the "
            + "4-character minimum - otherwise every short word matches half the table.");
    }

    [Fact]
    public void Rank_finds_warehousing_from_the_word_warehouse_despite_the_warehouse_clubs_trap()
    {
        // implementation-plan C2 requires 4931 or 493110 in the top three for "warehouse". Scored purely
        // on total common-prefix length that is in doubt: "Warehouse Clubs and Supercenters" shares 9
        // characters with the query and "Warehousing and Storage" only 8, so every exact-word title
        // outranks every Warehousing title. The real table has three such entries (4552, 45521, 455211),
        // which is exactly three places. The requirement is the contract; the tie-break has to yield.
        var results = NaicsSearch.Rank(Table, "warehouse", 3).Select(entry => entry.Code).ToList();

        results.ShouldContain(
            code => code == "4931" || code == "493110",
            $"got [{string.Join(", ", results)}]");
    }

    [Fact]
    public void Rank_ignores_case()
    {
        var lower = NaicsSearch.Rank(Table, "warehousing and storage", 5).Select(entry => entry.Code);
        var upper = NaicsSearch.Rank(Table, "WAREHOUSING AND STORAGE", 5).Select(entry => entry.Code);

        upper.ShouldBe(lower);
    }

    [Fact]
    public void Rank_returns_no_more_than_the_limit()
    {
        NaicsSearch.Rank(Table, "contractors", 2).Count.ShouldBeLessThanOrEqualTo(2);
        NaicsSearch.Rank(Table, "warehousing", 1).Count.ShouldBeLessThanOrEqualTo(1);
    }

    [Fact]
    public void Rank_returns_nothing_when_no_title_matches()
    {
        NaicsSearch.Rank(Table, "zymology", 10).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rank_returns_nothing_for_an_empty_query(string query)
    {
        NaicsSearch.Rank(Table, query, 10).ShouldBeEmpty(
            "an empty query has no tokens to match, so it is not a request for the whole table.");
    }
}
