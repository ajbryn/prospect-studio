using System.Text.Json;
using ProspectStudio.Core.Candidates;
using ProspectStudio.Tests.Shared;

namespace ProspectStudio.Core.Tests.Candidates;

/// <summary>
/// Builders for the Core candidate tests. Dedupe consumes <c>name_norm</c> rather than computing it,
/// so these helpers take it as an argument: a dedupe test must not fail because the normalizer is
/// wrong, and vice versa.
/// </summary>
internal static class CandidateFixtures
{
    public const string Release = PlacesDataFixture.Release;

    public static CandidateSite Site(
        string overtureId,
        string nameNorm,
        double lat,
        double lon,
        double confidence,
        string? website = null,
        string? name = null,
        string countyFips = "48201",
        string? taxonomyPrimary = "warehouse") =>
        new(
            OvertureId: overtureId,
            Name: name ?? nameNorm,
            NameNorm: nameNorm,
            Address: "1 Test Rd",
            City: "Houston",
            State: "TX",
            Zip: "77001",
            CountyFips: countyFips,
            Lat: lat,
            Lon: lon,
            Phone: "7135550100",
            Website: website,
            TaxonomyPrimary: taxonomyPrimary,
            TaxonomyPath: $"services_and_business|{taxonomyPrimary}",
            BasicCategory: "b2b_transportation_and_storage_service",
            Confidence: confidence,
            Release: Release,
            PayloadJson: $"{{\"id\":{JsonSerializer.Serialize(overtureId)}}}");

    /// <summary>
    /// A site built from a committed fixture row, with <c>name_norm</c> supplied explicitly so the
    /// dedupe tests stay independent of <see cref="NameNormalizer"/>.
    /// </summary>
    public static CandidateSite From(string fixtureId, string nameNorm)
    {
        var place = SamplePlaces.Row(fixtureId);

        return Site(
            place.Id,
            nameNorm,
            place.Lat,
            place.Lon,
            place.Confidence,
            place.Websites.FirstOrDefault(),
            place.Name,
            place.CountyFips,
            place.TaxonomyPrimary);
    }

    /// <summary>The group that holds <paramref name="overtureId"/>, as primary or as a duplicate.</summary>
    public static CandidateGroup GroupWith(this IReadOnlyList<CandidateGroup> groups, string overtureId) =>
        groups.Single(group =>
            group.Primary.OvertureId == overtureId
            || group.Duplicates.Any(site => site.OvertureId == overtureId));

    public static IReadOnlyList<string> Ids(this CandidateGroup group) =>
        [group.Primary.OvertureId, .. group.Duplicates.Select(site => site.OvertureId)];
}
