namespace ProspectStudio.Tests.Shared;

/// <summary>
/// Locates the spec pack's fixtures and schemas. The test projects copy <c>poc/fixtures</c> and
/// <c>poc/schemas</c> into their output as content (technical-design §3), so tests read them from
/// next to the test assembly and never from a path relative to the repo checkout.
/// </summary>
internal static class RepoFixtures
{
    public static string FixturesDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "poc", "fixtures");

    public static string SchemasDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "poc", "schemas");

    /// <summary>The brand kit that chunk C1 seeds into an empty <c>Brand Kit</c> folder.</summary>
    public static string BrandKitDirectory => Fixture("brand-kit");

    public static string SampleSearchProfile => Fixture("sample-search-profile.json");

    public static string SearchProfileSchema => Schema("search-profile.schema.json");

    /// <summary>
    /// Fixtures that belong to the tests rather than to the spec pack, from
    /// <c>src/tests/Fixtures</c>. The test projects copy that folder to their output too.
    /// </summary>
    public static string TestFixturesDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>
    /// The ten Houston CBSA counties plus Jefferson 48245, with simplified geometry. Generated from
    /// the real Census shapefile by <c>src/tests/Fixtures/geo/build-geo-fixtures.cs</c>.
    /// </summary>
    public static string CountiesHoustonParquet => GeoFixture("counties_houston.parquet");

    /// <summary>Every Texas CBSA row plus every "Springfield, *" row, for the ambiguous-query case.</summary>
    public static string CbsaExcerptCsv => GeoFixture("cbsa_excerpt.csv");

    /// <summary>ZCTA-to-county rows for the eleven fixture counties, including multi-county ZIP 77494.</summary>
    public static string ZctaCountyExcerptCsv => GeoFixture("zcta_county_excerpt.csv");

    /// <summary>
    /// Trimmed copies of the three raw Census sources, each keeping the awkward shape its real
    /// counterpart has, so the parsing side of the setup pipeline is testable with no network.
    /// </summary>
    public static string CountyShapefileZipSource => GeoFixture("sources", "cb_2025_us_county_500k.zip");

    public static string CbsaXlsxSource => GeoFixture("sources", "list1_2023.xlsx");

    public static string ZctaCountyTextSource => GeoFixture("sources", "tab20_zcta520_county20_natl.txt");

    /// <summary>A path under <c>src/tests/Fixtures/geo</c>, checked so a broken content copy is obvious.</summary>
    public static string GeoFixture(params string[] parts) =>
        Existing(Path.Combine(TestFixturesDirectory, "geo"), "src/tests/Fixtures/geo", parts);

    /// <summary>A path under <c>poc/fixtures</c>, checked so a broken content copy is obvious.</summary>
    public static string Fixture(params string[] parts) => Existing(FixturesDirectory, "poc/fixtures", parts);

    /// <summary>A path under <c>poc/schemas</c>, checked so a broken content copy is obvious.</summary>
    public static string Schema(params string[] parts) => Existing(SchemasDirectory, "poc/schemas", parts);

    private static string Existing(string root, string label, string[] parts)
    {
        var full = Path.Combine([root, .. parts]);
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            throw new FileNotFoundException(
                $"'{label}/{string.Join('/', parts)}' was not copied to the test output. Expected it at "
                + $"'{full}'. The test project copies {label} as Content with CopyToOutputDirectory.",
                full);
        }

        return full;
    }
}
