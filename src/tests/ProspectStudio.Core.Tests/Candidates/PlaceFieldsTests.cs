using ProspectStudio.Core.Candidates;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Core.Tests.Candidates;

/// <summary>
/// Turning a raw Overture row into a <c>sites</c> row (technical-design §5.2, §6.1). Every shape here
/// is one the real Texas extract actually produces, and each is a place where the obvious code is
/// wrong: <c>websites</c> and <c>phones</c> are lists that are often empty, <c>addresses</c> is a
/// struct list, the postcode is sometimes ZIP+4, and a missing <c>freeform</c> must not take the
/// locality and postcode down with it.
/// </summary>
public class PlaceFieldsTests
{
    [Fact]
    public void FirstWebsite_TakesTheHeadOfTheList() =>
        PlaceFields.FirstWebsite(Place(websites: ["https://www.aldinebenderfreight.example", "https://aldinebender.example"]))
            .ShouldBe("https://www.aldinebenderfreight.example");

    [Fact]
    public void FirstWebsite_NoWebsite_IsNull() =>
        PlaceFields.FirstWebsite(Place(websites: [])).ShouldBeNull(
            "a large share of real rows have no website at all (technical-design §6.1).");

    [Fact]
    public void FirstPhone_TakesTheHeadOfTheList_WithItsFormattingLeftAlone() =>
        // Real values are inconsistent: '7137477411' next to '17136884530'. §6.1 does not ask for them
        // to be normalized, so the first value is stored verbatim rather than silently reshaped.
        PlaceFields.FirstPhone(Place(phones: ["17135550210", "2815550211"])).ShouldBe("17135550210");

    [Fact]
    public void FirstPhone_NoPhone_IsNull() =>
        PlaceFields.FirstPhone(Place(phones: [])).ShouldBeNull();

    [Fact]
    public void FirstAddress_NoAddresses_IsNull() =>
        PlaceFields.FirstAddress(Place(addresses: [])).ShouldBeNull(
            "the addresses column is a list and an empty one is common.");

    [Theory]
    [InlineData("77064-3335", "77064")]
    [InlineData("77032-2514", "77032")]
    [InlineData("77504-1877", "77504")]
    [InlineData("77494", "77494")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void Zip5_CutsZipPlusFourBackToFive(string? postcode, string? expected) =>
        PlaceFields.Zip5(postcode).ShouldBe(
            expected,
            "territory routing and suppression both compare ZIP5 (technical-design §7.3, §7.4), so a "
            + "ZIP+4 stored whole would never match an override.");

    [Fact]
    public void ToSite_ReadsTheFirstOfEveryList()
    {
        var place = Place(
            id: "fx_0110",
            name: "Aldine Bender Freight Terminal",
            websites: ["https://www.aldinebenderfreight.example", "https://aldinebender.example"],
            phones: ["17135550210", "2815550211"],
            addresses: [new PlaceAddress("250 Aldine Bender Rd", "Houston", "77032-2514", "TX", "US")]);

        var site = PlaceFields.ToSite(place, CandidateFixtures.Release);

        site.OvertureId.ShouldBe("fx_0110");
        site.Website.ShouldBe("https://www.aldinebenderfreight.example");
        site.Phone.ShouldBe("17135550210");
        site.Address.ShouldBe("250 Aldine Bender Rd");
        site.City.ShouldBe("Houston");
        site.Zip.ShouldBe("77032", "ZIP+4 is truncated on the way in.");
        site.Release.ShouldBe(CandidateFixtures.Release, "a site must be traceable to its extract.");
    }

    [Fact]
    public void ToSite_AcceptsRegionTX_NotUS_TX()
    {
        // Verified against the real data: the region is plain 'TX'. Code that expected 'US-TX' and
        // stripped a prefix would silently store an empty state.
        var site = PlaceFields.ToSite(
            Place(addresses: [new PlaceAddress("1 Main St", "Houston", "77002", "TX", "US")]),
            CandidateFixtures.Release);

        site.State.ShouldBe("TX");
    }

    [Fact]
    public void ToSite_NoFreeform_KeepsTheRestOfTheAddress()
    {
        // 99,004 real Texas rows have no freeform. fx_0111 is the fixture row for it.
        var site = PlaceFields.ToSite(
            Place(addresses: [new PlaceAddress(null, "Anahuac", "77514", "TX", "US")]),
            CandidateFixtures.Release);

        site.Address.ShouldBeNull();
        site.City.ShouldBe("Anahuac", "a missing street must not take the city with it.");
        site.Zip.ShouldBe("77514");
        site.State.ShouldBe("TX");
    }

    [Fact]
    public void ToSite_NoAddressAtAll_LeavesTheAddressColumnsNull()
    {
        var site = PlaceFields.ToSite(Place(addresses: []), CandidateFixtures.Release);

        site.Address.ShouldBeNull();
        site.City.ShouldBeNull();
        site.Zip.ShouldBeNull();
        site.State.ShouldBeNull();
        site.CountyFips.ShouldBe("48201", "the county comes from the spatial join, not the address.");
    }

    [Fact]
    public void ToSite_NoWebsiteAndNoPhone_IsNotAnError()
    {
        var site = PlaceFields.ToSite(Place(websites: [], phones: []), CandidateFixtures.Release);

        site.Website.ShouldBeNull();
        site.Phone.ShouldBeNull();
    }

    [Fact]
    public void ToSite_StoresTheLeafCategoryAndTheWholeHierarchy()
    {
        var place = Place(
            basicCategory: "manufacturer",
            taxonomy: new PlaceTaxonomy(
                "steel_fabricator",
                ["services_and_business", "b2b_service", "manufacturer", "metal_fabricator", "steel_fabricator"],
                []));

        var site = PlaceFields.ToSite(place, CandidateFixtures.Release);

        site.TaxonomyPrimary.ShouldBe("steel_fabricator", "taxonomy.primary is the leaf.");
        site.BasicCategory.ShouldBe(
            "manufacturer",
            "basic_category is a coarser rollup and often NOT the leaf, so it is stored as its own "
            + "column rather than conflated with the primary (technical-design §6.1).");
        site.TaxonomyPath.ShouldNotBeNull()
            .ShouldContain("metal_fabricator", Case.Sensitive,
                "the whole hierarchy is kept, because a segment can match an interior node.");
        site.TaxonomyPath.ShouldEndWith("steel_fabricator", Case.Sensitive, "root first, leaf last.");
    }

    [Fact]
    public void ToSite_NormalizesTheName() =>
        PlaceFields.ToSite(Place(name: "Westpark Metal Fab, Inc."), CandidateFixtures.Release)
            .NameNorm.ShouldBe("westpark metal fab", "technical-design §7.1.");

    [Fact]
    public void ToSite_KeepsThePointAndConfidence()
    {
        var site = PlaceFields.ToSite(Place(lat: 29.95320, lon: -95.37940, confidence: 0.87), CandidateFixtures.Release);

        site.Lat.ShouldBe(29.95320, 1e-9);
        site.Lon.ShouldBe(-95.37940, 1e-9);
        site.Confidence.ShouldBe(0.87, 1e-9);
    }

    [Fact]
    public void ToSite_KeepsTheRawRowForProvenance() =>
        PlaceFields.ToSite(Place(), CandidateFixtures.Release).PayloadJson.ShouldNotBeNullOrWhiteSpace(
            "source_records stores the raw row (technical-design §5.2), and C7's enrichment and C13's "
            + "matchback both need to be able to look back at it.");

    [Fact]
    public void Every_fixture_row_converts_without_losing_its_county()
    {
        // The whole committed set, so a shape that only appears once (two websites, no freeform, ZIP+4)
        // cannot be missed.
        foreach (var place in SamplePlaces.All)
        {
            var site = PlaceFields.ToSite(FromFixture(place), CandidateFixtures.Release);

            site.OvertureId.ShouldBe(place.Id);
            site.CountyFips.ShouldBe(place.CountyFips, place.Id);
            site.Zip.ShouldBe(place.Postcode[..5], $"{place.Id} postcode '{place.Postcode}'");
            site.State.ShouldBe("TX", place.Id);
        }
    }

    private static OverturePlace FromFixture(SamplePlace place) =>
        new(
            place.Id,
            place.Name,
            place.BasicCategory,
            new PlaceTaxonomy(place.TaxonomyPrimary, place.TaxonomyHierarchy, []),
            place.Confidence,
            place.Websites,
            place.Phones,
            [new PlaceAddress(place.Freeform, place.Locality, place.Postcode, place.Region, place.Country)],
            place.Lat,
            place.Lon,
            place.CountyFips);

    private static OverturePlace Place(
        string id = "fx_0001",
        string? name = "Bayou Fulfillment Co.",
        string? basicCategory = "b2b_transportation_and_storage_service",
        PlaceTaxonomy? taxonomy = null,
        double confidence = 0.95,
        IReadOnlyList<string>? websites = null,
        IReadOnlyList<string>? phones = null,
        IReadOnlyList<PlaceAddress>? addresses = null,
        double lat = 29.7858,
        double lon = -95.8245,
        string countyFips = "48201") =>
        new(
            id,
            name,
            basicCategory,
            taxonomy ?? new PlaceTaxonomy(
                "warehouse",
                ["services_and_business", "b2b_service", "b2b_transportation_and_storage_service", "b2b_storage", "warehouse"],
                []),
            confidence,
            websites ?? ["https://www.bayoufulfillment.example"],
            phones ?? ["2815550101"],
            addresses ?? [new PlaceAddress("24500 Brazos Way", "Katy", "77494", "TX", "US")],
            lat,
            lon,
            countyFips);
}
