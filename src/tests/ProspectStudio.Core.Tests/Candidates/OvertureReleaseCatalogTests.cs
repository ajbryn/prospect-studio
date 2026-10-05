using ProspectStudio.Core.Candidates;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Core.Tests.Candidates;

/// <summary>
/// Release discovery from the Overture STAC catalog (technical-design §6.1), against a recorded
/// response. <c>src/tests/Fixtures/places/stac-catalog.json</c> is the real document with only the
/// 50-entry registry manifest trimmed.
/// </summary>
public class OvertureReleaseCatalogTests
{
    private static string Recorded { get; } = File.ReadAllText(RepoFixtures.OvertureStacCatalogJson);

    [Fact]
    public void ReadLatest_ReadsTheLatestField() =>
        OvertureReleaseCatalog.ReadLatest(Recorded).ShouldBe(
            "2026-09-23.1",
            "the release verified against the live dataset in C4, and the default "
            + "PS_OVERTURE_RELEASE.");

    [Fact]
    public void ReadLatest_DoesNotGuessFromTheLinksArray()
    {
        // The real links array mixes 'root', 'self' and 'child' entries in no particular order, and the
        // newest release is not last. A reader that sorted or took the final child would be right today
        // and wrong on the next publish, which is the worst kind of bug to inherit.
        var reordered = """
            {
              "type": "Catalog",
              "latest": "2026-09-23.1",
              "links": [
                { "rel": "child", "href": "https://stac.overturemaps.org/2026-09-23.0/catalog.json" },
                { "rel": "self",  "href": "https://stac.overturemaps.org/catalog.json" },
                { "rel": "child", "href": "https://stac.overturemaps.org/2026-08-19.0/catalog.json" }
              ]
            }
            """;

        OvertureReleaseCatalog.ReadLatest(reordered).ShouldBe("2026-09-23.1");
    }

    [Theory]
    [InlineData("""{ "type": "Catalog", "links": [] }""")]
    [InlineData("""{ "type": "Catalog", "latest": null }""")]
    [InlineData("""{ "type": "Catalog", "latest": "" }""")]
    [InlineData("{}")]
    public void ReadLatest_NoLatestField_IsNull(string json) =>
        OvertureReleaseCatalog.ReadLatest(json).ShouldBeNull(
            "no release is better than a guessed one: the caller can fall back to the configured "
            + "PS_OVERTURE_RELEASE and say so.");

    [Fact]
    public void The_catalog_url_is_the_one_the_design_names() =>
        OvertureReleaseCatalog.CatalogUrl.ShouldBe("https://stac.overturemaps.org/catalog.json");
}
