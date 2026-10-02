using ProspectStudio.Core.Market;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Core.Tests.Market;

/// <summary>
/// The <c>EMPSZES</c> rules of mcp-tools.md §estimate_market. The band set is read off each response,
/// never hard-coded: the bands used here are enumerated from the recorded Houston and Harris bodies, so
/// a band only appears in a test because the API really returned it.
/// </summary>
/// <remarks>
/// The rule is: parse <c>[lower, upper]</c> from each label, <strong>discard any band whose range is
/// contained within another band's range</strong>, then take every survivor whose lower bound is at or
/// above the threshold. Getting it wrong is silent — every wrong answer is a plausible-looking number.
/// </remarks>
public class CbpSizeBandTests
{
    /// <summary>Every distinct code and label the ten-county NAICS 4931 response contains.</summary>
    private static List<CbpSizeBand> HoustonBands() => BandsIn(CbpFixtures.Houston4931);

    /// <summary>Harris County, all sectors: the nine standard bands plus four that subdivide <c>260</c>.</summary>
    private static List<CbpSizeBand> Harris00Bands() => BandsIn(CbpFixtures.Harris00);

    [Theory]
    // Every label below is verbatim from a recorded response.
    [InlineData("210", "Establishments with less than 5 employees", 0)]
    [InlineData("220", "Establishments with 5 to 9 employees", 5)]
    [InlineData("230", "Establishments with 10 to 19 employees", 10)]
    [InlineData("241", "Establishments with 20 to 49 employees", 20)]
    [InlineData("242", "Establishments with 50 to 99 employees", 50)]
    [InlineData("251", "Establishments with 100 to 249 employees", 100)]
    [InlineData("252", "Establishments with 250 to 499 employees", 250)]
    [InlineData("254", "Establishments with 500 to 999 employees", 500)]
    [InlineData("260", "Establishments with 1,000 employees or more", 1_000)]
    [InlineData("262", "Establishments with 1,000 to 1,499 employees", 1_000)]
    [InlineData("263", "Establishments with 1,500 to 2,499 employees", 1_500)]
    [InlineData("271", "Establishments with 2,500 to 4,999 employees", 2_500)]
    [InlineData("273", "Establishments with 5,000 employees or more", 5_000)]
    public void Parse_reads_the_lower_bound_out_of_the_label(string code, string label, int expected)
    {
        var band = CbpSizeBands.Parse(code, label);

        band.Code.ShouldBe(code);
        band.MinEmployees.ShouldBe(
            expected,
            $"'{label}' is the only published statement of this band's bounds: the 2023 metadata has no "
            + "values list for EMPSZES and the codes have moved between vintages.");
        band.IsAllEstablishments.ShouldBeFalse();
    }

    [Fact]
    public void All_establishments_has_no_lower_bound()
    {
        var band = CbpSizeBands.Parse(CbpSizeBands.AllEstablishmentsCode, "All establishments");

        band.IsAllEstablishments.ShouldBeTrue();
        band.MinEmployees.ShouldBeNull(
            "001 spans every size, so it is not comparable with a threshold - and it equals the sum of "
            + "the nine standard bands, so counting it alongside them doubles every number.");
    }

    // ------------------------------------------------------- containment

    [Fact]
    public void A_band_nested_inside_another_is_discarded()
    {
        // Harris County, all sectors. 262 (1,000-1,499), 263 (1,500-2,499), 271 (2,500-4,999) and
        // 273 (5,000+) sum to 51+51+21+12 = 135, which is exactly 260 (1,000+). They subdivide it, so
        // keeping any of them alongside 260 counts those 135 establishments twice.
        var selection = CbpSizeBands.Select(Harris00Bands(), 1_000);

        selection.Codes.ShouldBe(
            ["260"],
            "260's label '1,000 employees or more' is accurate, and 262/263/271/273 are detail bands "
            + "inside it. 111,215 is the nine standard bands; adding the detail bands gives 111,350, "
            + "which is 135 more than the published total of 111,215 - impossible for disjoint bands.");
    }

    [Fact]
    public void An_open_ended_band_inside_another_open_ended_band_is_discarded()
    {
        // Both 260 ("1,000 employees or more") and 273 ("5,000 employees or more") are open-ended, so
        // containment cannot be decided by "this one has an upper bound and that one does not". A missing
        // upper bound is infinity, and 273 is still inside 260.
        var bands = Harris00Bands();
        bands.Select(band => band.Code).ShouldContain("273", "the fixture carries the second open-ended band.");

        CbpSizeBands.Select(bands, 1_000).Codes.ShouldNotContain(
            "273",
            "5,000+ is inside 1,000+, so adding them double-counts the twelve largest establishments.");
    }

    [Fact]
    public void Of_two_bands_sharing_a_lower_bound_the_wider_one_survives()
    {
        // 262 is 1,000-1,499 and 260 is 1,000+. They share a lower bound and differ only at the top, so
        // picking "the first band at 1,000" by code order or by insertion order keeps 262 and loses the
        // 84 establishments above 1,499.
        var selection = CbpSizeBands.Select(Harris00Bands(), 1_000);

        selection.Codes.ShouldContain("260", "260 is the wider range, so it is the one that partitions.");
        selection.Codes.ShouldNotContain("262", "1,000-1,499 is inside 1,000+.");
    }

    [Fact]
    public void Partition_keeps_the_nine_bands_that_add_up_to_the_total()
    {
        var partition = CbpSizeBands.Partition(Harris00Bands());

        partition.Select(band => band.Code).ShouldBe(
            CbpFixtures.StandardBandCodes,
            ignoreOrder: true,
            "the survivors have to be a partition: Harris County's nine standard bands sum to 111,215, "
            + "which is its published total exactly, while all thirteen bands sum to 111,350.");
        partition.ShouldNotContain(band => band.IsAllEstablishments, "001 is the total, not a band.");
    }

    [Fact]
    public void Partition_of_bands_that_already_partition_changes_nothing()
    {
        // Harris County / NAICS 4931 publishes no detail bands, so there is nothing to discard. A rule
        // that dropped 260 here - the one open-ended band - would quietly lose the largest employers.
        var bands = BandsIn(CbpFixtures.Harris4931);

        CbpSizeBands.Partition(bands).Select(band => band.Code).ShouldBe(
            CbpFixtures.StandardBandCodes,
            ignoreOrder: true);
    }

    [Fact]
    public void A_threshold_of_twenty_takes_the_standard_bands_from_241_up()
    {
        var selection = CbpSizeBands.Select(HoustonBands(), 20);

        selection.Codes.ShouldBe(
            ["241", "242", "251", "252", "254", "260"],
            ignoreOrder: true,
            "263 is nested inside 260, so including it alongside 260 inflates Houston 4931's "
            + "withMinEmployees from 152 to 155. Both look like plausible answers.");
        selection.EffectiveMinEmployees.ShouldBe(20);
        selection.RoundedUp.ShouldBeFalse("20 is a band edge, so nothing had to move.");
    }

    [Fact]
    public void A_threshold_of_fifty_takes_the_standard_bands_from_242_up()
    {
        var selection = CbpSizeBands.Select(HoustonBands(), 50);

        selection.Codes.ShouldBe(["242", "251", "252", "254", "260"], ignoreOrder: true);
        selection.EffectiveMinEmployees.ShouldBe(50);
        selection.RoundedUp.ShouldBeFalse();
    }

    [Fact]
    public void The_nine_standard_bands_are_what_a_threshold_of_zero_selects()
    {
        // Whatever detail bands a response carries, the survivors are the partition - nothing more and
        // nothing less. This is the invariant that makes every sum in the suite add up.
        CbpSizeBands.Select(Harris00Bands(), 0).Codes.ShouldBe(
            CbpFixtures.StandardBandCodes,
            ignoreOrder: true,
            "the survivors of containment are exactly the nine bands that partition the total.");
    }

    [Fact]
    public void A_threshold_inside_a_band_rounds_up_to_the_next_edge()
    {
        var selection = CbpSizeBands.Select(HoustonBands(), 25);

        selection.RequestedMinEmployees.ShouldBe(25);
        selection.EffectiveMinEmployees.ShouldBe(
            50,
            "25 falls inside 'with 20 to 49 employees'. A band cannot be split, and counting the whole "
            + "band would overstate the market, so the threshold moves up to the next edge.");
        selection.RoundedUp.ShouldBeTrue();
        selection.Codes.ShouldNotContain("241");
        selection.Codes.ShouldContain("242");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(1_000)]
    public void No_threshold_ever_selects_the_all_establishments_band(int minEmployees)
    {
        var selection = CbpSizeBands.Select(HoustonBands(), minEmployees);

        selection.Codes.ShouldNotContain(
            CbpSizeBands.AllEstablishmentsCode,
            "001 equals the sum of the standard bands exactly, so including it doubles every count.");
    }

    [Fact]
    public void Only_bands_the_response_actually_had_are_selected()
    {
        // The NAICS 238210 response for the same ten counties carries no detail bands at all; the 4931
        // one carries 263. A hard-coded band list cannot be right for both.
        var houston238210 = BandsIn(CbpFixtures.Houston238210);
        houston238210.Select(band => band.Code).ShouldNotContain("263");

        CbpSizeBands.Select(houston238210, 20).Codes.ShouldBe(
            ["241", "242", "251", "252", "254"],
            ignoreOrder: true,
            "the band set varies by query, so Select may only return bands it was given - and 260 is "
            + "absent from this response, so it cannot be conjured either.");
    }

    [Fact]
    public void A_threshold_above_the_widest_surviving_band_selects_nothing()
    {
        // After containment the top surviving band is 260 (1,000+), so nothing starts at or above 5,000.
        // Reporting a band anyway would turn "we cannot tell" into a positive count.
        //
        // NOTE for the spec: this is the literal reading of the rule, and it loses the resolution the
        // detail bands carried - Harris County really does publish 271 = 21 and 273 = 12 above 2,500.
        // Flagged to the coordinator; a rule of "prefer the finest partition that covers the threshold"
        // would answer 33 instead of 0 here.
        CbpSizeBands.Select(Harris00Bands(), 5_000).Codes.ShouldBeEmpty();
        CbpSizeBands.Select(HoustonBands(), 5_000).Codes.ShouldBeEmpty();
    }

    /// <summary>
    /// Each distinct band in a recorded response, which is also the only legitimate way to learn the
    /// band set at runtime.
    /// </summary>
    private static List<CbpSizeBand> BandsIn(string json) =>
    [
        .. CbpTable.Parse(json)
            .GroupBy(row => row.SizeBandCode, StringComparer.Ordinal)
            .Select(group => CbpSizeBands.Parse(group.Key, group.First().SizeBandLabel)),
    ];
}
