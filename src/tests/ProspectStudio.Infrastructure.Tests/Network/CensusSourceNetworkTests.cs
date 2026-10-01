using ProspectStudio.Tests;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Infrastructure.Tests.Network;

/// <summary>
/// Opt-in live checks that the Census sources the setup pipeline depends on are still there and still
/// say what C2 verified they say. Skipped unless <c>PS_RUN_NETWORK_TESTS=1</c>, and excluded from CI by its category. Kept deliberately few: between them they download about 12 MB.
/// </summary>
[Trait("Category", "Network")]
public class CensusSourceNetworkTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    [NetworkFact]
    public async Task The_county_boundary_file_still_holds_about_3200_counties()
    {
        using var timeout = Deadline();
        var zip = await DownloadAsync(ReferenceDataFixture.CountiesSourceUrl, timeout.Token);

        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var names = archive.Entries.Select(entry => entry.FullName).ToList();

        names.ShouldContain(
            name => name.EndsWith(".shp", StringComparison.OrdinalIgnoreCase),
            $"ST_Read needs the shapefile inside the zip; it holds: {string.Join(", ", names)}");
        names.ShouldContain(name => name.EndsWith(".dbf", StringComparison.OrdinalIgnoreCase));
        names.ShouldContain(name => name.EndsWith(".prj", StringComparison.OrdinalIgnoreCase));

        // A DBF header stores its record count in bytes 4-7, so the row count needs no shapefile reader.
        var dbf = archive.Entries.First(entry => entry.FullName.EndsWith(".dbf", StringComparison.OrdinalIgnoreCase));
        await using var stream = dbf.Open();
        var header = new byte[32];
        await stream.ReadExactlyAsync(header, timeout.Token);

        BitConverter.ToInt32(header, 4).ShouldBeInRange(
            3_100,
            3_400,
            "C2 counted 3,235 county rows; a number far from that means the file changed shape.");
    }

    [NetworkFact]
    public async Task The_CBSA_delineation_file_still_names_the_ten_county_Houston_metro()
    {
        using var timeout = Deadline();
        var xlsx = await DownloadAsync(ReferenceDataFixture.CbsaSourceUrl, timeout.Token);

        // An .xlsx is a zip of XML; its text lives in xl/sharedStrings.xml, which is enough to confirm
        // the two facts the Houston tests are built on without an Excel reader.
        using var archive = new ZipArchive(new MemoryStream(xlsx), ZipArchiveMode.Read);
        var shared = archive.GetEntry("xl/sharedStrings.xml")
            .ShouldNotBeNull("list1_2023.xlsx is an OOXML workbook, not a CSV (verified in C2).");

        await using var stream = shared.Open();
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync(timeout.Token);

        text.ShouldContain("Houston-Pasadena-The Woodlands, TX");
        text.ShouldContain(
            "San Jacinto County",
            Case.Sensitive,
            "San Jacinto is the tenth Houston CBSA county the spec used to omit.");
        text.ShouldContain(
            "FIPS County Code",
            Case.Sensitive,
            "state and county FIPS are still separate columns.");
    }

    [NetworkFact]
    public async Task The_geocoder_still_answers_with_x_as_longitude_and_y_as_latitude()
    {
        using var timeout = Deadline();

        var url = "https://geocoding.geo.census.gov/geocoder/locations/onelineaddress"
            + "?address=1200+Main+St%2C+Houston%2C+TX&benchmark=Public_AR_Current&format=json";

        using var http = Client();
        using var response = await http.GetAsync(url, timeout.Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var coordinates = document.RootElement
            .GetProperty("result")
            .GetProperty("addressMatches")[0]
            .GetProperty("coordinates");

        coordinates.GetProperty("x").GetDouble().ShouldBeInRange(-96.0, -95.0, "x is the longitude.");
        coordinates.GetProperty("y").GetDouble().ShouldBeInRange(29.0, 30.5, "y is the latitude.");
    }

    private static async Task<byte[]> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        using var http = Client();
        using var response = await http.GetAsync(url, cancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"GET {url}");
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static HttpClient Client()
    {
        var http = new HttpClient { Timeout = Timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ProspectStudio-tests/0.1 (+https://github.com/ajbryn)");
        return http;
    }

    private static CancellationTokenSource Deadline() => new(Timeout);
}
