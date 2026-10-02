namespace ProspectStudio.Tests.Shared;

/// <summary>
/// The recorded Census CBP response bodies in <c>src/tests/Fixtures/cbp</c>, which the test projects
/// copy to their output. See that folder's <c>README.md</c> for each body's query and for every number
/// the C3 tests assert.
/// </summary>
/// <remarks>
/// Bodies only: no fixture holds a request URL, so no fixture can hold a key.
/// </remarks>
internal static class CbpFixtures
{
    /// <summary>The ten requested Houston CBSA counties, in the order <c>resolve_geography</c> returns them.</summary>
    public static IReadOnlyList<string> HoustonCountyFips { get; } =
        ["48015", "48039", "48071", "48157", "48167", "48201", "48291", "48339", "48407", "48473"];

    /// <summary>The two counties the ten-county NAICS 4931 query leaves out altogether.</summary>
    public static IReadOnlyList<string> SuppressedHoustonCountyFips { get; } = ["48291", "48407"];

    public const string HarrisCountyFips = "48201";

    /// <summary>The latest CBP vintage; 2024 and 2025 are 404.</summary>
    public const int LatestCbpYear = 2023;

    /// <summary>
    /// The nine bands that <strong>partition</strong> the total exactly: &lt;5, 5-9, 10-19, 20-49,
    /// 50-99, 100-249, 250-499, 500-999 and 1,000+. Any other band a response carries subdivides one of
    /// these and must be discarded before summing. Named here so a test can state the expected
    /// arithmetic without the production code's own notion of which bands those are.
    /// </summary>
    public static IReadOnlyList<string> StandardBandCodes { get; } =
        ["210", "220", "230", "241", "242", "251", "252", "254", "260"];

    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures", "cbp");

    /// <summary>NAICS 4931 across the ten requested counties. Eight answer; 462 total, 439 banded.</summary>
    public static string Houston4931 => Read("houston10_4931.json");

    /// <summary>NAICS 238210 across the same ten counties. All answer; 1310 total, 1293 banded.</summary>
    public static string Houston238210 => Read("houston10_238210.json");

    public static string Harris4931 => Read("harris_4931.json");

    /// <summary>The descendant of 4931, which must be dropped rather than added when both are asked for.</summary>
    public static string Harris49311 => Read("harris_49311.json");

    /// <summary>Fully banded: 833 establishments, 833 in bands, so no suppression note is due.</summary>
    public static string Harris238210 => Read("harris_238210.json");

    /// <summary>
    /// Harris County, every sector, every band - the body that settles the nesting rule. The nine
    /// standard bands sum to <c>001</c> = 111,215 exactly, while <c>262</c>, <c>263</c>, <c>271</c> and
    /// <c>273</c> subdivide <c>260</c> = 135 and bring a naive sum to 111,350, which is 135 too many.
    /// Two of its bands - <c>260</c> and <c>273</c> - are both open-ended.
    /// </summary>
    public static string Harris00 => Read("harris_00_allbands.json");

    /// <summary>Header with <c>EMPSZES</c> twice, the second copy after the <c>get=</c> columns.</summary>
    public static string DuplicateColumn => Read("tx_counties_4931_duplicate_column.json");

    /// <summary><c>EMP</c> of <c>"0"</c> with <c>EMP_F</c> of <c>"N"</c>, and flag columns that are JSON <c>null</c>.</summary>
    public static string WithFlagColumns => Read("tx_counties_238210_with_flags.json");

    /// <summary>The 400 body for a request whose <c>in=state:</c> names two states.</summary>
    public static string CrossStateError => Read("cross_state_error.txt");

    /// <summary>The Tomcat 404 page: a probed year that does not exist, and a keyed query against one.</summary>
    public static string NotFoundHtml => Read("not_found_404.html");

    /// <summary>The year probe's success body, trimmed to the variables the client reads.</summary>
    public static string Variables2023 => Read("cbp_variables_2023.json");

    private static string Read(string name) => File.ReadAllText(Existing(name));

    private static string Existing(string name)
    {
        var full = System.IO.Path.Combine(Directory, name);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException(
                $"'src/tests/Fixtures/cbp/{name}' was not copied to the test output. Expected it at "
                + $"'{full}'. The test project copies src/tests/Fixtures as Content with CopyToOutputDirectory.",
                full);
        }

        return full;
    }
}
