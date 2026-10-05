using System.Globalization;
using ProspectStudio.Core.Candidates;
using ProspectStudio.Core.Configuration;
using ProspectStudio.Infrastructure.Reference;
using ProspectStudio.Tests;
using Shouldly;

namespace ProspectStudio.Infrastructure.Tests.Network;

/// <summary>
/// The external facts chunk C4 rests on, checked against the live services in <strong>one</strong>
/// test: the STAC catalog still names a release, and the S3 bucket still answers anonymously with the
/// schema technical-design §6.1 records. Everything else in C4 runs off the committed fixtures.
/// </summary>
/// <remarks>
/// Opt-in: <c>[NetworkFact]</c> skips unless <c>PS_RUN_NETWORK_TESTS=1</c>, so a plain
/// <c>dotnet test</c> cannot reach the network (implementation-plan, 2026-10-01 · C2). The data read is
/// held to a bbox of about one square kilometre over downtown Houston; the Texas extract is 205 MB and
/// a careless query here would pull far more than a test should. <c>DESCRIBE</c> reads only the Parquet
/// footer, so the whole test costs a handful of range requests.
/// </remarks>
public class OvertureNetworkTests
{
    /// <summary>About 1 km square over downtown Houston.</summary>
    private const double MinLon = -95.375;
    private const double MaxLon = -95.365;
    private const double MinLat = 29.755;
    private const double MaxLat = 29.765;

    [NetworkFact]
    [Trait("Category", "Network")]
    public async Task Anonymous_S3_and_the_release_catalog_still_answer_as_the_design_says()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        // 1. Release discovery (technical-design §6.1). A divergence from the configured default is not
        //    a failure - that is exactly what discovery exists for - but a missing field is.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(PsOptionsFactory.DefaultUserAgent);

        var catalog = await http.GetStringAsync(OvertureReleaseCatalog.CatalogUrl, timeout.Token);
        var latest = OvertureReleaseCatalog.ReadLatest(catalog);

        latest.ShouldNotBeNullOrWhiteSpace(
            "release discovery reads the catalog's 'latest' field. If it is gone, the setup pipeline "
            + "has to fall back to PS_OVERTURE_RELEASE and say so, and §6.1 needs updating.");
        latest.ShouldMatch(@"^\d{4}-\d{2}-\d{2}\.\d+$", $"releases are dated like '2026-09-23.1'; got '{latest}'.");

        // 2. The release the server is configured to use must still be readable, with no credentials.
        var release = PsOptionsFactory.DefaultOvertureRelease;
        var source = $"s3://overturemaps-us-west-2/release/{release}/theme=places/type=place/*";

        using var db = DuckDbSpatial.Open(allowInstall: true);
        DuckDbSpatial.Execute(db, "INSTALL httpfs");
        DuckDbSpatial.Execute(db, "LOAD httpfs");
        DuckDbSpatial.Execute(db, "SET s3_region='us-west-2'");

        var columns = Describe(db, source);

        foreach (var column in new[]
                 {
                     "id", "names", "basic_category", "taxonomy", "confidence", "websites", "phones",
                     "addresses", "geometry", "bbox",
                 })
        {
            columns.ShouldContainKey(
                column,
                $"technical-design §6.1 selects '{column}'. Live columns: {string.Join(", ", columns.Keys)}");
        }

        columns.ShouldNotContainKey(
            "categories",
            "Overture removed it, which is why the fixture and §6.3's query use taxonomy instead. Its "
            + "return would mean §6.1 needs revisiting.");

        columns["taxonomy"].ShouldContain("hierarchy", Case.Insensitive, $"got '{columns["taxonomy"]}'.");
        columns["websites"].ShouldBe("VARCHAR[]", "a flat list, not a struct.");
        columns["addresses"].ShouldContain("freeform", Case.Insensitive, $"a struct list; got '{columns["addresses"]}'.");
        columns["geometry"].ShouldContain(
            "CRS84",
            Case.Insensitive,
            $"the CRS that makes §6.3's ST_SetCRS necessary; got '{columns["geometry"]}'.");

        // 3. One tiny data read, bbox-limited, proving the pre-filter still works and the release has
        //    data rather than just a schema.
        var sql = string.Create(
            CultureInfo.InvariantCulture,
            $"""
             SELECT count(*)
             FROM read_parquet('{source}', hive_partitioning = 1)
             WHERE bbox.xmin BETWEEN {MinLon} AND {MaxLon}
               AND bbox.ymin BETWEEN {MinLat} AND {MaxLat}
             """);

        Convert.ToInt64(DuckDbSpatial.Scalar(db, sql), CultureInfo.InvariantCulture).ShouldBeGreaterThan(
            0,
            $"downtown Houston has places in release {release}. Zero means either the release is gone "
            + "or bbox pre-filtering no longer works - and bbox is what keeps the Texas extract to "
            + "about 70 seconds instead of a full-planet scan.");
    }

    private static Dictionary<string, string> Describe(DuckDB.NET.Data.DuckDBConnection db, string source)
    {
        using var command = db.CreateCommand();
        command.CommandText = $"DESCRIBE SELECT * FROM read_parquet('{source}', hive_partitioning = 1)";
        using var reader = command.ExecuteReader();

        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            columns[reader.GetString(0)] = reader.GetString(1);
        }

        return columns;
    }
}
