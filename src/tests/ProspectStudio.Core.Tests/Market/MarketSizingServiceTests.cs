using ProspectStudio.Core.Geography;
using ProspectStudio.Core.Market;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Core.Tests.Market;

/// <summary>
/// <c>estimate_market</c> end to end over the recorded CBP bodies (mcp-tools.md §estimate_market). Every
/// number asserted here came back from the live API; <c>src/tests/Fixtures/cbp/README.md</c> lists them
/// all and says which query each one is from.
/// </summary>
public class MarketSizingServiceTests
{
    // ---------------------------------------------------------------- sums

    [Fact]
    public async Task Houston_metro_sums_every_county_and_both_naics_codes()
    {
        using var timeout = Deadline();

        var estimate = await Service(FakeCbpDataSource.Houston())
            .EstimateAsync(new(["4931", "238210"], Houston(), 20), timeout.Token);

        estimate.CbpYear.ShouldBe(CbpFixtures.LatestCbpYear);
        estimate.GeoLabel.ShouldBe("Houston-Pasadena-The Woodlands, TX");

        var warehousing = estimate.ByNaics.Single(row => row.Naics == "4931");
        warehousing.Title.ShouldBe("Warehousing and Storage");
        warehousing.Establishments.ShouldBe(462);
        warehousing.WithMinEmployees.ShouldBe(152);

        var electrical = estimate.ByNaics.Single(row => row.Naics == "238210");
        electrical.Title.ShouldBe("Electrical Contractors and Other Wiring Installation Contractors");
        electrical.Establishments.ShouldBe(1_310);
        electrical.WithMinEmployees.ShouldBe(217);

        estimate.Total.Establishments.ShouldBe(1_772);
        estimate.Total.WithMinEmployees.ShouldBe(369);
    }

    [Fact]
    public async Task The_all_establishments_band_is_never_added_to_the_others()
    {
        using var timeout = Deadline();

        var estimate = await Service(FakeCbpDataSource.Houston())
            .EstimateAsync(new(["4931"], Houston(), 20), timeout.Token);

        estimate.ByNaics.Single().Establishments.ShouldBe(
            462,
            "001 is the total and the nine standard bands sum to 439 of it. Adding 001 to them reports "
            + "901 for a market of 462 - no error, just a doubled number.");
    }

    [Fact]
    public async Task A_threshold_of_fifty_takes_the_higher_bands_only()
    {
        using var timeout = Deadline();

        var estimate = await Service(FakeCbpDataSource.Houston())
            .EstimateAsync(new(["4931", "238210"], Houston(), 50), timeout.Token);

        estimate.ByNaics.Single(row => row.Naics == "4931").WithMinEmployees.ShouldBe(78);
        estimate.ByNaics.Single(row => row.Naics == "238210").WithMinEmployees.ShouldBe(102);
        estimate.Total.WithMinEmployees.ShouldBe(180);
        estimate.Total.Establishments.ShouldBe(
            1_772,
            "a threshold filters withMinEmployees, not the establishment total.");
    }

    [Fact]
    public async Task A_threshold_of_twenty_does_not_double_count_the_nested_band()
    {
        using var timeout = Deadline();

        var estimate = await Service(FakeCbpDataSource.Houston())
            .EstimateAsync(new(["4931"], Houston(), 20), timeout.Token);

        estimate.ByNaics.Single().WithMinEmployees.ShouldBe(
            152,
            "155 means band 263 (1,500-2,499) was added alongside 260 (1,000+), which contains it. County "
            + "48157 publishes 263 = 3 and 260 = 3, so the overlap is exactly the 3 that turns 152 into "
            + "155. Harris County / NAICS 00 settles it: 262+263+271+273 = 135 = 260.");
    }

    [Fact]
    public async Task A_threshold_inside_a_band_rounds_up_to_the_next_edge_and_says_so()
    {
        using var timeout = Deadline();

        var estimate = await Service(FakeCbpDataSource.Houston())
            .EstimateAsync(new(["4931"], Houston(), 25), timeout.Token);

        estimate.ByNaics.Single().WithMinEmployees.ShouldBe(
            78,
            "25 falls inside the 20-49 band, so the threshold moves up to 50 and the answer is the 50+ one.");

        var note = OneNoteMentioning(estimate, "25");
        note.ShouldContain("50", Case.Sensitive, $"the note has to name the threshold actually used: {note}");
    }

    // ------------------------------------------------------- suppression

    [Fact]
    public async Task A_missing_band_row_adds_a_note_with_the_size_of_the_gap()
    {
        using var timeout = Deadline();

        var estimate = await Service(FakeCbpDataSource.Houston())
            .EstimateAsync(new(["4931"], Houston(), 20), timeout.Token);

        var note = OneNoteMentioning(estimate, "4931");

        note.ShouldContain("23", Case.Sensitive, $"23 of the 462 establishments have no published band: {note}");
        note.ShouldContain("462", Case.Sensitive, $"the note has to give the gap its size: {note}");
        note.ShouldContain(
            "lower bound",
            Case.Insensitive,
            "withMinEmployees cannot be exact while 23 establishments have no band, and a go/no-go number "
            + $"that is quietly a lower bound is worse than no number: {note}");
    }

    [Fact]
    public async Task An_absent_county_is_named_in_a_note_rather_than_counted_as_zero()
    {
        using var timeout = Deadline();

        var estimate = await Service(FakeCbpDataSource.Houston())
            .EstimateAsync(new(["4931"], Houston(), 20), timeout.Token);

        var notes = string.Join(" | ", estimate.Notes);

        notes.ShouldContain("Liberty", Case.Sensitive, $"48291 returned no rows at all: {notes}");
        notes.ShouldContain("San Jacinto", Case.Sensitive, $"48407 returned no rows at all: {notes}");
        notes.ShouldContain(
            "absent",
            Case.Insensitive,
            "an absent county means unknown, never zero - detection is 'which requested counties came "
            + $"back', never a value check: {notes}");
        notes.ShouldNotContain(
            "Montgomery", Case.Insensitive,
            "Montgomery answered; naming a county that did come back would make the note useless.");
    }

    [Fact]
    public async Task A_fully_published_market_gets_no_suppression_note()
    {
        using var timeout = Deadline();

        // Harris County alone, NAICS 238210: 833 establishments and 833 in bands, one county requested
        // and one returned. Nothing is suppressed, so nothing may be hedged.
        var estimate = await Service(FakeCbpDataSource.Harris())
            .EstimateAsync(new(["238210"], Harris(), 20), timeout.Token);

        estimate.ByNaics.Single().Establishments.ShouldBe(833);
        estimate.ByNaics.Single().WithMinEmployees.ShouldBe(182);
        estimate.Notes.ShouldBeEmpty(
            "a note on a complete answer trains the reader to ignore notes, which is how the real "
            + $"suppression warning gets missed. Got: {string.Join(" | ", estimate.Notes)}");
    }

    [Fact]
    public async Task A_county_with_establishments_but_no_bands_counts_towards_the_total_and_the_gap()
    {
        using var timeout = Deadline();

        // Austin County 48015 reports 3 establishments and zero band rows. Those 3 are inside the 462 and
        // inside the 23-establishment gap, and they are in no band sum.
        var estimate = await Service(FakeCbpDataSource.Houston())
            .EstimateAsync(new(["4931"], Houston(), 20), timeout.Token);

        estimate.ByNaics.Single().Establishments.ShouldBe(462);
        estimate.ByNaics.Single().WithMinEmployees.ShouldBe(
            152,
            "a county with no band rows contributes to the total and to the gap, never to a band sum.");
        estimate.Notes.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Suppression_is_reported_per_naics_code()
    {
        using var timeout = Deadline();

        // 4931 is short by 23 across eight counties; 238210 is short by 17 across all ten. One note that
        // blended them would misstate both.
        var estimate = await Service(FakeCbpDataSource.Houston())
            .EstimateAsync(new(["4931", "238210"], Houston(), 20), timeout.Token);

        OneNoteMentioning(estimate, "4931").ShouldContain("23");
        OneNoteMentioning(estimate, "238210").ShouldContain("17");
    }

    // ------------------------------------------------- NAICS overlap

    [Fact]
    public async Task An_overlapping_naics_code_is_dropped_and_the_drop_is_reported()
    {
        using var timeout = Deadline();
        var source = FakeCbpDataSource.Harris();

        var estimate = await Service(source)
            .EstimateAsync(new(["4931", "49311"], Harris(), 20), timeout.Token);

        estimate.ByNaics.Select(row => row.Naics).ShouldBe(["4931"]);
        estimate.Total.Establishments.ShouldBe(
            360,
            "49311's 256 establishments are already inside 4931's 360. Adding them reports 616.");

        source.NaicsAsked.ShouldAllBe(asked => !asked.Contains("49311"));

        var notes = string.Join(" | ", estimate.Notes);
        notes.ShouldContain("49311", Case.Sensitive, $"dropping what the caller asked for silently is not acceptable: {notes}");
        notes.ShouldContain("4931", Case.Sensitive, notes);
    }

    [Fact]
    public async Task Disjoint_naics_branches_are_both_counted()
    {
        using var timeout = Deadline();

        var estimate = await Service(FakeCbpDataSource.Harris())
            .EstimateAsync(new(["4931", "238210"], Harris(), 20), timeout.Token);

        estimate.ByNaics.Select(row => row.Naics).ShouldBe(["4931", "238210"], ignoreOrder: true);
        estimate.ByNaics.Single(row => row.Naics == "4931").Establishments.ShouldBe(360);
        estimate.ByNaics.Single(row => row.Naics == "238210").Establishments.ShouldBe(833);
        estimate.Total.Establishments.ShouldBe(
            1_193,
            "warehousing and electrical contracting are different branches, so they add up.");
    }

    // ------------------------------------------- geography: countyFips

    [Fact]
    public async Task A_multi_metro_union_scope_is_sized_from_its_counties_although_cbsa_is_null()
    {
        using var timeout = Deadline();

        // resolve_geography returns a union for {type:"cbsa", values:[two metros]}: the counties are all
        // there and cbsa is deliberately null (C2 decision, 2026-10-01). Code that reached for cbsa to
        // size the market would size the wrong market, or none at all.
        var union = new ResolvedGeography(
            GeoScopeTypes.Cbsa,
            "2 metro areas",
            Cbsa: null,
            States: ["48"],
            CountyFips: CbpFixtures.HoustonCountyFips,
            Zips: [],
            Radius: null,
            Bbox: []);

        var source = FakeCbpDataSource.Houston();
        var estimate = await Service(source).EstimateAsync(new(["4931"], union, 20), timeout.Token);

        estimate.GeoLabel.ShouldBe("2 metro areas");
        estimate.ByNaics.Single().Establishments.ShouldBe(462);
        estimate.ByNaics.Single().WithMinEmployees.ShouldBe(152);

        source.CountiesAsked.SelectMany(asked => asked).Distinct().Order(StringComparer.Ordinal).ShouldBe(
            CbpFixtures.HoustonCountyFips.Order(StringComparer.Ordinal),
            "sizing reads countyFips; cbsa is null here and must never be the input.");
    }

    [Fact]
    public async Task Every_requested_county_is_asked_for_including_the_ones_that_answer_with_nothing()
    {
        using var timeout = Deadline();
        var source = FakeCbpDataSource.Houston();

        await Service(source).EstimateAsync(new(["4931"], Houston(), 20), timeout.Token);

        var asked = source.CountiesAsked.SelectMany(counties => counties).Distinct().ToList();
        foreach (var fips in CbpFixtures.SuppressedHoustonCountyFips)
        {
            asked.ShouldContain(
                fips,
                $"{fips} has to be requested before 'it came back with nothing' can be noticed.");
        }
    }

    [Fact]
    public async Task The_cbp_year_comes_from_the_data_source_rather_than_a_constant()
    {
        using var timeout = Deadline();
        var source = new FakeCbpDataSource(year: 2019).With("4931", CbpFixtures.Harris4931);

        var estimate = await Service(source).EstimateAsync(new(["4931"], Harris(), 20), timeout.Token);

        estimate.CbpYear.ShouldBe(2019, "the vintage is whatever the client found, not a hard-coded 2023.");
        source.YearCalls.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_scope_that_carries_a_cbsa_is_still_sized_from_its_counties()
    {
        using var timeout = Deadline();
        var source = FakeCbpDataSource.Houston();

        // Houston() has cbsa 26420 AND ten counties. The counties are what CBP can be queried with, so
        // they are what has to be used - there is no CBSA-shaped query to prefer.
        await Service(source).EstimateAsync(new(["4931"], Houston(), 20), timeout.Token);

        source.CountiesAsked.SelectMany(asked => asked).Distinct().Order(StringComparer.Ordinal).ShouldBe(
            CbpFixtures.HoustonCountyFips.Order(StringComparer.Ordinal),
            "countyFips wins whether or not cbsa is set, so the two scope shapes size identically.");
    }

    // ------------------------------------------- a band sum over the total

    [Fact]
    public async Task The_all_sectors_body_reconciles_exactly_under_the_nesting_rule()
    {
        using var timeout = Deadline();

        // Harris County, all sectors: 001 is 111,215 and the nine standard bands sum to exactly that,
        // while every non-001 row added naively comes to 111,350 - 135 too many, which is precisely
        // 260 (1,000+) counted twice through 262 + 263 + 271 + 273.
        var source = new FakeCbpDataSource().With("00", CbpFixtures.Harris00);

        var estimate = await Service(source).EstimateAsync(new(["00"], Harris(), 1_000), timeout.Token);

        estimate.ByNaics.Single().Establishments.ShouldBe(111_215);
        estimate.ByNaics.Single().WithMinEmployees.ShouldBe(
            135,
            "260 (1,000+) is 135. Adding the four detail bands inside it reports 270 for the same 135 "
            + "establishments.");
        estimate.Notes.ShouldBeEmpty(
            "with the nesting rule applied the bands reconcile exactly, so there is nothing to hedge. "
            + $"Got: {string.Join(" | ", estimate.Notes)}");
    }

    [Fact]
    public async Task A_band_sum_that_exceeds_the_total_is_surfaced_rather_than_clamped_away()
    {
        using var timeout = Deadline();

        // A gap of 001 minus the band sum can only be positive for disjoint bands, so a negative one
        // means the bands were selected wrongly. Clamping it with Math.Max(0, total - bands) reports a
        // tidy "nothing suppressed" and is exactly what hid the 263 bug: Houston 4931's naive sum came to
        // 442 against a total of 462, which looks like an ordinary shortfall of 20.
        //
        // No recorded body can produce this once the rule is right - that is the point of the rule - so
        // the rows here are built by hand to contradict themselves, with the real labels a future vintage
        // would use. They are input to a Core function, not a fixture pretending to be a recording.
        var contradictory = new ContradictoryCbpSource();

        MarketEstimate? estimate = null;
        MarketExternalException? refused = null;
        try
        {
            estimate = await Service(contradictory).EstimateAsync(new(["4931"], Harris(), 20), timeout.Token);
        }
        catch (MarketExternalException exception)
        {
            refused = exception;
        }

        // Refusing is the stronger answer, since no band-derived number from such a response is
        // trustworthy - but either way the two numbers have to be named, or the reader cannot tell what
        // went wrong. The one outcome ruled out is a clean answer reporting nothing suppressed.
        var reported = refused?.Message ?? string.Join(" | ", estimate!.Notes);

        reported.ShouldNotBeEmpty(
            "150 banded establishments against a published total of 100 is impossible. Reporting it as a "
            + "tidy answer with a zero gap is what Math.Max(0, total - bands) does, and it is exactly how "
            + "a wrong band selection stays invisible.");
        reported.ShouldContain(
            "150",
            Case.Sensitive,
            $"name the band sum that overshot: {reported}");
        reported.ShouldContain(
            "100",
            Case.Sensitive,
            $"name the published total it overshot: {reported}");
    }

    // --------------------------------------------------- dangerous defaults

    [Fact]
    public async Task Without_a_threshold_withMinEmployees_is_null()
    {
        using var timeout = Deadline();

        var estimate = await Service(FakeCbpDataSource.Harris())
            .EstimateAsync(new(["238210"], Harris(), MinEmployees: null), timeout.Token);

        estimate.ByNaics.Single().Establishments.ShouldBe(833);
        estimate.ByNaics.Single().WithMinEmployees.ShouldBeNull(
            "mcp-tools.md §estimate_market: absent minEmployees means withMinEmployees is present but "
            + "null, not a copy of establishments - which would read as 'all 833 employ 20 or more'.");
        estimate.Total.WithMinEmployees.ShouldBeNull();
    }

    [Theory]
    [InlineData("a word instead of a code", "warehouse")]
    [InlineData("one digit", "4")]
    [InlineData("seven digits", "4931101")]
    [InlineData("an empty string", "")]
    [InlineData("punctuation", "49-31")]
    public async Task A_bogus_naics_code_is_refused(string because, string naics)
    {
        using var timeout = Deadline();

        await Should.ThrowAsync<MarketRequestException>(
            () => Service(FakeCbpDataSource.Harris())
                .EstimateAsync(new([naics], Harris(), 20), timeout.Token),
            $"mcp-tools.md §estimate_market: VALIDATION_FAILED for a bogus NAICS code ({because}). A code "
            + "is 2 to 6 digits.");
    }

    [Fact]
    public async Task A_geography_with_no_counties_is_refused_rather_than_sized_at_zero()
    {
        using var timeout = Deadline();
        var empty = new ResolvedGeography(
            GeoScopeTypes.Counties, "nowhere", null, [], [], [], null, []);

        await Should.ThrowAsync<MarketRequestException>(
            () => Service(FakeCbpDataSource.Harris())
                .EstimateAsync(new(["238210"], empty, 20), timeout.Token),
            "mcp-tools.md §estimate_market: 'reporting a market of zero for an empty scope is the "
            + "dangerous reading, so refuse instead'.");
    }

    // ------------------------------- county granularity for zips and radius

    [Fact]
    public async Task A_zip_scope_says_how_many_whole_counties_it_really_covers()
    {
        using var timeout = Deadline();

        // CBP is published per county, so ZIP 77494 is really the whole of Fort Bend, Harris and Waller.
        // The number is an order of magnitude too big for the ZIP, and presenting it as a precise figure
        // for "77494" is the kind of plausible-but-wrong answer this tool cannot afford.
        var zips = new ResolvedGeography(
            GeoScopeTypes.Zips,
            "ZIP 77494",
            null,
            ["48"],
            ["48157", "48201", "48473"],
            ["77494"],
            null,
            []);

        var estimate = await Service(FakeCbpDataSource.Houston())
            .EstimateAsync(new(["4931"], zips, 20), timeout.Token);

        estimate.ByNaics.Single().Establishments.ShouldBe(400, "48157 32 + 48201 360 + 48473 8.");
        estimate.ByNaics.Single().WithMinEmployees.ShouldBe(139, "10 + 126 + 3.");

        var note = OneNoteMentioning(estimate, "counties");
        note.ShouldContain(
            "3",
            Case.Sensitive,
            $"the note has to say how many whole counties the figure actually covers: {note}");
    }

    [Fact]
    public async Task A_radius_scope_says_how_many_whole_counties_it_really_covers()
    {
        using var timeout = Deadline();

        var radius = new ResolvedGeography(
            GeoScopeTypes.Radius,
            "25 miles around 29.7604, -95.3698",
            null,
            ["48"],
            ["48157", "48201", "48473"],
            [],
            new GeoRadius(29.7604, -95.3698, 25),
            []);

        var estimate = await Service(FakeCbpDataSource.Houston())
            .EstimateAsync(new(["4931"], radius, 20), timeout.Token);

        var note = OneNoteMentioning(estimate, "counties");
        note.ShouldContain(
            "3",
            Case.Sensitive,
            "a radius is no more county-shaped than a ZIP is: the figure covers whole counties and the "
            + $"note has to say so. {note}");
    }

    [Fact]
    public async Task A_cbsa_scope_needs_no_granularity_note()
    {
        using var timeout = Deadline();

        // A CBSA is defined as a set of whole counties, so the county-granularity caveat does not apply
        // and a note would be noise - and noise is how the real warnings get skipped.
        var cbsa = new ResolvedGeography(
            GeoScopeTypes.Cbsa,
            "Houston-Pasadena-The Woodlands, TX",
            "26420",
            ["48"],
            [CbpFixtures.HarrisCountyFips],
            [],
            null,
            []);

        var source = new FakeCbpDataSource().With("00", CbpFixtures.Harris00);
        var estimate = await Service(source).EstimateAsync(new(["00"], cbsa, 1_000), timeout.Token);

        estimate.Notes.ShouldBeEmpty(
            "nothing is suppressed and the scope is already county-shaped. Got: "
            + string.Join(" | ", estimate.Notes));
    }

    // ------------------------------- answers the data genuinely cannot give

    [Fact]
    public async Task A_threshold_above_the_top_published_band_is_null_rather_than_zero()
    {
        using var timeout = Deadline();

        // Harris County's top surviving band is "1,000 or more", so nothing published can say how many
        // employ 2,000+. Answering 0 reads as "there are none that large", when in fact 135 establishments
        // might all qualify - the data simply cannot tell. Null is the only honest answer.
        var source = new FakeCbpDataSource().With("00", CbpFixtures.Harris00);

        var estimate = await Service(source).EstimateAsync(new(["00"], Harris(), 2_000), timeout.Token);

        estimate.ByNaics.Single().Establishments.ShouldBe(111_215);
        estimate.ByNaics.Single().WithMinEmployees.ShouldBeNull(
            "0 is a claim the data does not support; null says 'unanswerable' and the note says why.");
        estimate.Total.WithMinEmployees.ShouldBeNull();

        var note = NoteMentioning(estimate, "2,000") ?? NoteMentioning(estimate, "2000");
        note.ShouldNotBeNull("an unanswerable threshold has to be explained, not returned bare.");
        note.ShouldContain(
            "1,000",
            Case.Sensitive,
            $"the note has to name the top band CBP actually publishes: {note}");
    }

    [Fact]
    public async Task A_total_built_from_absent_counties_is_hedged_as_well_as_the_breakdown()
    {
        using var timeout = Deadline();

        // Every requested county comes back with nothing, so establishments is 0 because the data is
        // missing, not because the market is empty. Hedging only withMinEmployees leaves a confident
        // zero total on screen - the one number a go/no-go decision reads first.
        var source = new FakeCbpDataSource().With("4931", EmptyBody);

        var estimate = await Service(source).EstimateAsync(new(["4931"], Houston(), 20), timeout.Token);

        estimate.ByNaics.Single().Establishments.ShouldBe(0);
        estimate.ByNaics.Single().WithMinEmployees.ShouldBeNull(
            "nothing was published, so the bands cannot answer the threshold. 0 is the same claim as the "
            + "unreachable-threshold case, where null was already settled on - two spellings of 'we could "
            + "not compute this' is one too many, and 0 is the one that reads as a real answer.");
        estimate.Total.WithMinEmployees.ShouldBeNull();

        var notes = string.Join(" | ", estimate.Notes);
        notes.ShouldNotBeEmpty("ten requested counties, none answered.");
        notes.ShouldContain(
            "establishments",
            Case.Insensitive,
            "the note has to hedge the establishment total too, not only withMinEmployees: an absent "
            + $"county means unknown, so a zero total is unknown as well. Got: {notes}");
    }

    [Fact]
    public async Task An_absent_county_hedges_the_total_even_when_other_counties_answered()
    {
        using var timeout = Deadline();

        // Two of the ten Houston counties are absent, so 462 is itself a lower bound - Liberty and San
        // Jacinto have warehouses whatever CBP chose to publish.
        var estimate = await Service(FakeCbpDataSource.Houston())
            .EstimateAsync(new(["4931"], Houston(), 20), timeout.Token);

        var notes = string.Join(" | ", estimate.Notes);
        notes.ShouldContain("Liberty", Case.Sensitive, notes);
        notes.ShouldContain(
            "establishments",
            Case.Insensitive,
            $"462 is a floor, not a count, while two counties are missing: {notes}");
    }

    [Fact]
    public async Task An_overlapping_code_is_dropped_before_the_request_cap_counts_it()
    {
        using var timeout = Deadline();
        var source = FakeCbpDataSource.Harris();

        // The cap counts the queries that will actually be sent. Counting 4931 and 49311 as two would
        // refuse a request that needs exactly one round trip.
        await Service(source).EstimateAsync(new(["4931", "49311"], Harris(), 20), timeout.Token);

        source.NaicsAsked.ShouldHaveSingleItem().ShouldBe(
            ["4931"],
            "deduplication happens first, so the cap sees one code rather than two.");
    }

    // ---------------------------------------------------------- helpers

    private static MarketSizingService Service(ICbpDataSource source) =>
        new(source, new FakeGeographyReference(), new FakeNaicsCatalog());

    /// <summary>The ten-county Houston CBSA as <c>resolve_geography</c> returns it.</summary>
    private static ResolvedGeography Houston() => new(
        GeoScopeTypes.Cbsa,
        "Houston-Pasadena-The Woodlands, TX",
        "26420",
        ["48"],
        CbpFixtures.HoustonCountyFips,
        [],
        null,
        []);

    private static ResolvedGeography Harris() => new(
        GeoScopeTypes.Counties,
        "Harris County, TX",
        null,
        ["48"],
        [CbpFixtures.HarrisCountyFips],
        [],
        null,
        []);

    /// <summary>The single note mentioning <paramref name="text"/>, or null when none does.</summary>
    private static string? NoteMentioning(MarketEstimate estimate, string text) =>
        estimate.Notes.SingleOrDefault(note => note.Contains(text, StringComparison.Ordinal));

    private static string OneNoteMentioning(MarketEstimate estimate, string text)
    {
        var matches = estimate.Notes.Where(note => note.Contains(text, StringComparison.Ordinal)).ToList();

        matches.Count.ShouldBe(
            1,
            $"expected exactly one note mentioning '{text}'. Notes: {string.Join(" | ", estimate.Notes)}");

        return matches[0];
    }

    /// <summary>
    /// A response with its header row and no data: the shape a query answers with when CBP publishes
    /// nothing for it. Taken from a real body so the header is the real one.
    /// </summary>
    private static string EmptyBody { get; } =
        CbpFixtures.Houston4931[..(CbpFixtures.Houston4931.IndexOf(']', CbpFixtures.Houston4931.IndexOf('[', 1)) + 1)]
        + "]";

    /// <summary>Market sizing is pure arithmetic over rows it is handed; two seconds is generous.</summary>
    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(2));
}
