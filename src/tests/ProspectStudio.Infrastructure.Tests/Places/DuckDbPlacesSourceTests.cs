using ProspectStudio.Core.Candidates;
using ProspectStudio.Infrastructure.Tests.Infrastructure;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Infrastructure.Tests.Places;

/// <summary>
/// Candidate search over the local Parquet, technical-design §6.3, against the committed fixtures.
/// Covers the first four bullets of C4's test list: county scoping with Beaumont excluded, the
/// confidence floor, category-or-keyword matching, and the field shapes the real data has.
/// </summary>
public class DuckDbPlacesSourceTests
{
    /// <summary>A DuckDB query over 113 rows is milliseconds; this only stops a hang from stalling CI.</summary>
    private static CancellationTokenSource Timeout => new(TimeSpan.FromSeconds(30));

    private static readonly string[] AllTargetCategories =
    [
        "warehouse", "distribution_service", "freight_and_cargo_service", "motor_freight_trucking",
        "manufacturer", "industrial_equipment_manufacturer", "metal_fabricator", "machine_shop",
        "electrician", "hvac_service", "sign_making", "glass_and_mirror_sales_service",
        "property_management",
    ];

    private static readonly string[] ProfileKeywords =
    [
        "racking", "fulfillment", "distribution", "logistics", "3pl", "warehouse", "fabrication",
        "manufacturing", "machining", "steel", "electrical", "hvac", "mechanical", "sign", "glazing",
        "facilities", "property services",
    ];

    [Fact]
    public void IsReady_IsTrueOnlyForAStateWithAnExtract()
    {
        using var environment = new PlacesTestEnvironment();

        environment.Source.IsReady("TX").ShouldBeTrue();
        environment.Source.IsReady("OK").ShouldBeFalse(
            "a state with no extract is NOT_READY, not an empty result - reporting zero candidates for "
            + "a state nobody prepared reads as a real answer.");
    }

    [Fact]
    public async Task A_Houston_scope_returns_the_metro_and_excludes_Beaumont()
    {
        using var environment = new PlacesTestEnvironment();

        var found = await FindAsync(environment, Query());
        var ids = found.Select(place => place.Id).ToList();

        foreach (var jefferson in SamplePlaces.InJeffersonCounty)
        {
            ids.ShouldNotContain(
                jefferson.Id,
                $"{jefferson.Id} is in Jefferson County {SamplePlaces.JeffersonCountyFips} (CBSA 13140). "
                + "A Houston scope that returned it would be matching on state or bbox rather than on "
                + "the county join (§6.3).");
        }

        // The Beaumont rows are warehouse, metal_fabricator and electrician with confidence 0.85-0.90,
        // so nothing but the county filter can be excluding them.
        SamplePlaces.InJeffersonCounty.ShouldAllBe(
            place => place.Confidence >= 0.6 && AllTargetCategories.Contains(place.TaxonomyPrimary));

        ids.ShouldContain("fx_0001", "Katy, Fort Bend 48157.");
        ids.ShouldContain("fx_0107", "Hempstead, Waller 48473 - the far western edge of the metro.");
        ids.ShouldContain("fx_0021", "Anahuac, Chambers 48071 - the far eastern edge.");
    }

    [Fact]
    public async Task The_county_of_each_place_comes_back_from_the_join()
    {
        using var environment = new PlacesTestEnvironment();

        var found = await FindAsync(environment, Query());

        foreach (var place in found)
        {
            place.CountyFips.ShouldBe(
                SamplePlaces.Row(place.Id).CountyFips,
                $"{place.Id}: the county is the GEOID the spatial join produced, and C5 routes on it.");
        }
    }

    [Fact]
    public async Task A_single_county_scope_returns_only_that_county()
    {
        using var environment = new PlacesTestEnvironment();

        var found = await FindAsync(environment, Query(counties: ["48339"]));

        found.ShouldNotBeEmpty();
        found.ShouldAllBe(place => place.CountyFips == "48339");
    }

    [Fact]
    public async Task The_confidence_floor_excludes_everything_below_it()
    {
        using var environment = new PlacesTestEnvironment();

        var found = await FindAsync(environment, Query(minConfidence: 0.6));
        var ids = found.Select(place => place.Id).ToList();

        found.ShouldAllBe(place => place.Confidence >= 0.6);

        foreach (var weak in SamplePlaces.All.Where(place => place.Confidence < 0.6))
        {
            ids.ShouldNotContain(weak.Id, $"{weak.Id} has confidence {weak.Confidence}.");
        }

        // And the floor is a parameter, not a constant: dropping it brings them back, which is the only
        // way to tell "filtered" from "absent from the fixture".
        var everything = await FindAsync(environment, Query(minConfidence: 0.0));
        everything.Select(place => place.Id).ShouldContain("fx_0076");
    }

    [Fact]
    public async Task A_place_matched_only_by_a_name_keyword_is_included()
    {
        using var environment = new PlacesTestEnvironment();

        // No categories at all, so the only thing that can match is the name.
        var found = await FindAsync(environment, Query(categories: [], keywords: ["racking"]));

        found.Select(place => place.Id).Order(StringComparer.Ordinal).ToList().ShouldBe(
            ["fx_0005", "fx_0074", "fx_0114"],
            "all three have 'Racking' in the name and a category no segment asks for. This is C4's "
            + "'a place matched only by a name keyword is included'. fx_0114 is also in an excluded "
            + "category, but this query passes no exclusions - that is the next test's job.");
    }

    [Fact]
    public async Task Keyword_matching_is_case_insensitive()
    {
        using var environment = new PlacesTestEnvironment();

        var lower = await FindAsync(environment, Query(categories: [], keywords: ["racking"]));
        var upper = await FindAsync(environment, Query(categories: [], keywords: ["RACKING"]));

        upper.Select(place => place.Id).Order(StringComparer.Ordinal).ToList()
            .ShouldBe([.. lower.Select(place => place.Id).Order(StringComparer.Ordinal)],
                "§6.3 lowercases the name before matching, so the profile's keyword casing cannot "
                + "change the result.");
    }

    [Fact]
    public async Task A_category_that_is_only_an_interior_node_of_the_hierarchy_still_matches()
    {
        using var environment = new PlacesTestEnvironment();

        // fx_0106's taxonomy.primary is 'steel_fabricator', which is in no segment; its hierarchy holds
        // 'metal_fabricator', which is. Only list_has_any(taxonomy.hierarchy, ...) finds it.
        var found = await FindAsync(environment, Query(categories: ["metal_fabricator"], keywords: []));
        var ids = found.Select(place => place.Id).ToList();

        ids.ShouldContain(
            "fx_0106",
            "the hierarchy half of §6.3's category test is missing: 'taxonomy.primary IN (...)' alone "
            + "cannot see an interior node, and a segment naming a broad category would silently miss "
            + "every more specific child.");
        ids.ShouldContain("fx_0007", "a row whose primary IS metal_fabricator.");
    }

    [Fact]
    public async Task A_category_that_is_only_a_leaf_still_matches()
    {
        using var environment = new PlacesTestEnvironment();

        // The other half: 'machine_shop' appears nowhere in the fixture as an interior node, so only
        // 'taxonomy.primary IN (...)' can find these two - whereas 'manufacturer' is both a leaf and an
        // interior node, which is what makes the OR worth writing.
        var found = await FindAsync(environment, Query(categories: ["machine_shop"], keywords: []));

        found.Select(place => place.Id).Order(StringComparer.Ordinal).ToList()
            .ShouldBe(["fx_0010", "fx_0105"]);
    }

    [Fact]
    public async Task Category_and_keyword_matches_are_unioned_not_intersected()
    {
        using var environment = new PlacesTestEnvironment();

        var categoriesOnly = await FindAsync(environment, Query(keywords: []));
        var keywordsOnly = await FindAsync(environment, Query(categories: []));
        var both = await FindAsync(environment, Query());

        var union = categoriesOnly.Select(place => place.Id)
            .Union(keywordsOnly.Select(place => place.Id), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        both.Select(place => place.Id).Order(StringComparer.Ordinal).ToList().ShouldBe(
            union,
            "§6.3 joins the three tests with OR. An AND would drop every place whose category matches "
            + "but whose name says nothing, which is most of them.");
    }

    [Fact]
    public async Task An_excluded_category_is_dropped_even_when_a_keyword_matched_the_name()
    {
        using var environment = new PlacesTestEnvironment();

        // fx_0075 is 'Warehouse Grill & Bar', category 'restaurant'. The keyword 'warehouse' matches its
        // name, and the sample profile excludes 'restaurant'.
        var withExclusion = await FindAsync(environment, Query(excluded: ["restaurant"]));
        var withoutExclusion = await FindAsync(environment, Query(excluded: []));

        withoutExclusion.Select(place => place.Id).ShouldContain(
            "fx_0075",
            "without the exclusion the keyword alone pulls the restaurant in - which is what makes the "
            + "exclusion worth having.");
        withExclusion.Select(place => place.Id).ShouldNotContain(
            "fx_0075",
            "the profile's exclusions.overtureCategories must win over a keyword match, or a lift "
            + "campaign mails a bar.");
    }

    [Fact]
    public async Task An_excluded_keyword_drops_a_place_its_category_had_already_matched()
    {
        using var environment = new PlacesTestEnvironment();

        // The mirror image of the test above. fx_0115 is 'Coastal Equipment Rental Services', a
        // distribution_service - a target category - whose name holds the profile's excluded keyword
        // 'equipment rental'. exclusions.keywords is a declared profile field and this is the only
        // thing that consumes it.
        var withExclusion = await FindAsync(environment, Query(excludedKeywords: ["equipment rental"]));
        var withoutExclusion = await FindAsync(environment, Query());

        withoutExclusion.Select(place => place.Id).ShouldContain(
            "fx_0115",
            "its category alone pulls it in, which is what makes the keyword exclusion load-bearing.");
        withExclusion.Select(place => place.Id).ShouldNotContain("fx_0115");

        // And it must not take the rest of distribution_service with it.
        withExclusion.Select(place => place.Id).ShouldContain("fx_0003");
    }

    [Fact]
    public async Task An_exclusion_matches_the_hierarchy_too()
    {
        using var environment = new PlacesTestEnvironment();

        // fx_0109 is 'truck_rental_service', whose hierarchy holds 'vehicle_rental_service'. Excluding
        // the broader node must take the leaf with it, the same way a segment's broad category takes
        // its children.
        var found = await FindAsync(environment, Query(
            categories: ["truck_rental_service"],
            keywords: [],
            excluded: ["vehicle_rental_service"]));

        found.Select(place => place.Id).ShouldNotContain("fx_0109");
    }

    [Fact]
    public async Task The_limit_is_honoured()
    {
        using var environment = new PlacesTestEnvironment();

        var found = await FindAsync(environment, Query(limit: 5));

        found.Count.ShouldBe(5, "the tool takes a limit so a wide search cannot blow the budget.");
    }

    [Fact]
    public async Task The_first_website_and_phone_arrive_as_lists_not_as_single_values()
    {
        using var environment = new PlacesTestEnvironment();

        var place = (await FindAsync(environment, Query())).Single(found => found.Id == "fx_0110");

        place.Websites.ToList().ShouldBe(
            ["https://www.aldinebenderfreight.example", "https://aldinebender.example"],
            "the source hands back what Overture holds; picking the first is PlaceFields' job, so the "
            + "second value must not be silently dropped here.");
        place.Phones.ToList().ShouldBe(["17135550210", "2815550211"]);
    }

    [Fact]
    public async Task A_place_with_no_website_comes_back_with_an_empty_list()
    {
        using var environment = new PlacesTestEnvironment();

        var place = (await FindAsync(environment, Query())).Single(found => found.Id == "fx_0029");

        place.Websites.ShouldBeEmpty("23 of the 120 fixture rows have no website, as the real data does.");
    }

    [Fact]
    public async Task The_address_struct_survives_including_a_missing_freeform()
    {
        using var environment = new PlacesTestEnvironment();

        var found = await FindAsync(environment, Query());

        var noStreet = found.Single(place => place.Id == "fx_0111").Addresses.ShouldHaveSingleItem();
        noStreet.Freeform.ShouldBeNull("no freeform, as 99,004 real Texas rows have.");
        noStreet.Locality.ShouldBe("Anahuac", "and nothing else may be lost with it.");
        noStreet.Postcode.ShouldBe("77514");
        noStreet.Region.ShouldBe("TX", "the real region is plain 'TX'.");

        var zipPlusFour = found.Single(place => place.Id == "fx_0110").Addresses.ShouldHaveSingleItem();
        zipPlusFour.Postcode.ShouldBe(
            "77032-2514",
            "the source returns the raw postcode; truncation to five digits is PlaceFields.Zip5's job, "
            + "so doing it here would hide whether that rule exists at all.");
    }

    [Fact]
    public async Task The_taxonomy_struct_survives_with_its_whole_hierarchy()
    {
        using var environment = new PlacesTestEnvironment();

        var place = (await FindAsync(environment, Query())).Single(found => found.Id == "fx_0106");

        place.Taxonomy.Primary.ShouldBe("steel_fabricator");
        place.Taxonomy.Hierarchy.ToList().ShouldBe(
            ["services_and_business", "b2b_service", "manufacturer", "metal_fabricator", "steel_fabricator"]);
        place.BasicCategory.ShouldBe("manufacturer", "a coarser rollup, and here not the leaf.");
    }

    [Fact]
    public async Task Count_categories_reports_real_category_names_with_in_state_counts()
    {
        using var environment = new PlacesTestEnvironment();

        var counts = await environment.Source.CountCategoriesAsync("TX", "warehouse", 15, Timeout.Token);

        var warehouse = counts.SingleOrDefault(count => count.Category == "warehouse")
            .ShouldNotBeNull($"'warehouse' must be among the results: {string.Join(", ", counts.Select(count => count.Category))}");
        warehouse.CountInState.ShouldBe(
            20,
            "the 20 'warehouse' rows in the extract, counted on taxonomy.primary - the same way the "
            + "real 960 Texas rows were counted. C5 added fx_0117 'Northfield Storage Co.', which is "
            + "the only row §7.3's exact-name suppression rule can reach.");
        warehouse.Path.ToList().ShouldBe(
            ["services_and_business", "b2b_service", "b2b_transportation_and_storage_service", "b2b_storage", "warehouse"],
            "mcp-tools.md §lookup_overture_categories returns the hierarchy as 'path'.");
    }

    [Fact]
    public async Task Count_categories_honours_the_limit_and_leads_with_the_commonest()
    {
        using var environment = new PlacesTestEnvironment();

        var counts = await environment.Source.CountCategoriesAsync("TX", null, 3, Timeout.Token);

        counts.Count.ShouldBe(3);
        counts.Select(count => count.CountInState).ShouldBeInOrder(SortDirection.Descending);
        counts[0].Category.ShouldBe("warehouse", "20 rows, the commonest in the fixture.");
    }

    [Fact]
    public async Task Count_categories_is_cached_while_the_extract_is_unchanged()
    {
        using var environment = new PlacesTestEnvironment();
        var source = environment.Source;
        var extract = environment.ExtractPath;

        var first = await source.CountCategoriesAsync("TX", "warehouse", 15, Timeout.Token);

        // Deterministic proof without timing anything: make the file unreadable as Parquet while
        // leaving its length and last-write time alone. A second full scan would now throw; a cache
        // hit returns what it had. (If the key ever becomes a content hash, this test needs updating
        // rather than deleting - the property it pins, "an unchanged extract is not re-scanned", is
        // what makes lookup_overture_categories cheap enough for a skill to call freely.)
        var fingerprint = FileFingerprint.Of(extract);
        await File.WriteAllBytesAsync(extract, new byte[fingerprint.Length], Timeout.Token);
        File.SetLastWriteTimeUtc(extract, fingerprint.LastWriteUtc);

        var second = await source.CountCategoriesAsync("TX", "warehouse", 15, Timeout.Token);

        second.ToList().ShouldBe(
            [.. first],
            "mcp-tools.md calls these 'counts from the extract, cached'. A full scan of a 205 MB file on "
            + "every lookup is not something a skill can call freely.");
    }

    [Fact]
    public async Task Count_categories_refreshes_after_the_extract_is_rebuilt()
    {
        // The other half, and the one that matters more: a cache keyed on the state alone never
        // expires, so a prepare_data --force inside a running server would serve pre-force counts for
        // the lifetime of the process. The counts would be confidently wrong, which is the failure mode
        // this tool can least afford - it is what a skill writes a search profile from.
        using var environment = new PlacesTestEnvironment();
        var source = environment.Source;

        // Both calls pass the same arguments - no query term - so the only thing that differs between
        // them is the file. A query term would filter the category *names*, which would make "warehouse
        // is gone" true for the wrong reason.
        var before = await source.CountCategoriesAsync("TX", null, 50, Timeout.Token);
        before.ShouldContain(count => count.Category == "warehouse");

        environment.ReplaceExtractWithout("warehouse");

        var after = await source.CountCategoriesAsync("TX", null, 50, Timeout.Token);

        // Non-empty FIRST. ShouldNotContain passes vacuously on an empty collection, so asserting the
        // absence of 'warehouse' before knowing anything came back would report a failed read as a
        // cache problem - the same trap as a fixture that guarantees a green test.
        after.ShouldNotBeEmpty(
            "the rebuilt extract still holds every other category, so an empty result means the read "
            + "itself failed rather than the cache refreshing. Look at ReplaceExtractWithout's output "
            + "before reading anything into the cache behaviour.");

        after.ShouldNotContain(
            count => count.Category == "warehouse",
            "the extract no longer holds a single warehouse row. Still reporting them means the cache "
            + "outlived the file it summarised: key it on the extract's identity, not just the state. "
            + $"Got: {string.Join(", ", after.Select(count => $"{count.Category}={count.CountInState}"))}");
    }

    private static async Task<IReadOnlyList<OverturePlace>> FindAsync(PlacesTestEnvironment environment, CandidateQuery query) =>
        await environment.Source.FindAsync("TX", query, Timeout.Token);

    private static CandidateQuery Query(
        IReadOnlyList<string>? counties = null,
        IReadOnlyList<string>? categories = null,
        IReadOnlyList<string>? keywords = null,
        double minConfidence = 0.6,
        IReadOnlyList<string>? excluded = null,
        IReadOnlyList<string>? excludedKeywords = null,
        int limit = 5000) =>
        new(
            counties ?? SamplePlaces.HoustonCbsaCounties,
            categories ?? AllTargetCategories,
            keywords ?? ProfileKeywords,
            minConfidence,
            excluded ?? [],
            excludedKeywords ?? [],
            limit);
}
