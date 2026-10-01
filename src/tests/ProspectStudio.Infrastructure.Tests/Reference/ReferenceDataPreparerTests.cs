using System.Globalization;
using System.Text.Json;
using DuckDB.NET.Data;
using ProspectStudio.Core.Reference;
using ProspectStudio.Infrastructure.Reference;
using ProspectStudio.Infrastructure.Tests.Infrastructure;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Infrastructure.Tests.Reference;

/// <summary>
/// POC-2 and technical-design §6.1, the download-and-parse direction: the pipeline reads its sources
/// through <see cref="IReferenceFileSource"/>, so the committed trimmed sources stand in for census.gov
/// and CI checks the parsing instead of only the skip path. Each source keeps the shape that is easy to
/// get wrong - a shapefile reachable only through the GDAL virtual path, an xlsx whose header is on row
/// 3 with notes under the data, and a pipe-delimited file with a UTF-8 BOM and rows whose ZCTA is empty.
/// </summary>
public class ReferenceDataPreparerTests
{
    /// <summary>Harris County, the one county in the trimmed shapefile.</summary>
    private const string Harris = "48201";

    /// <summary>The three CBSAs in the trimmed workbook: Beaumont, Houston, Springfield MO.</summary>
    private static readonly string[] HoustonCbsaCounties =
        ["48015", "48039", "48071", "48157", "48167", "48201", "48291", "48339", "48407", "48473"];

    [Fact]
    public async Task Preparing_writes_all_three_outputs_and_a_manifest()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var refdata = directory.Combine("refdata");
        var source = new FakeReferenceFileSource();

        var result = await new ReferenceDataPreparer(refdata, source).PrepareAsync(force: false, timeout.Token);

        result.Steps.Select(step => step.Step).ShouldBe(ReferenceSteps.All, ignoreOrder: true);
        result.Steps.ShouldAllBe(step => !step.Skipped);
        source.TotalCalls.ShouldBe(3, "one source file per step.");

        foreach (var name in ReferenceDataFixture.FileNames)
        {
            File.Exists(Path.Combine(refdata, name)).ShouldBeTrue($"'{name}' was not written to '{refdata}'.");
        }
    }

    [Fact]
    public async Task The_county_step_reads_the_shapefile_out_of_the_zip_and_keeps_usable_geometry()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var refdata = directory.Combine("refdata");

        await new ReferenceDataPreparer(refdata, new FakeReferenceFileSource())
            .PrepareAsync(force: false, timeout.Token);

        var parquet = Forward(Path.Combine(refdata, ReferenceSteps.CountiesFile));
        using var db = Duck();

        Scalar(db, $"SELECT count(*) FROM read_parquet('{parquet}')").ShouldBe("1");
        Scalar(db, $"SELECT GEOID FROM read_parquet('{parquet}')").ShouldBe(Harris);
        Scalar(db, $"SELECT NAME FROM read_parquet('{parquet}')").ShouldBe("Harris");
        Scalar(db, $"SELECT STATEFP FROM read_parquet('{parquet}')").ShouldBe("48");

        // ST_Point takes (lon, lat). Downtown Houston has to land inside Harris County, which is the
        // whole point of storing geometry rather than a bounding box.
        Scalar(
            db,
            $"SELECT GEOID FROM read_parquet('{parquet}') WHERE ST_Within(ST_Point(-95.3698, 29.7604), geometry)")
            .ShouldBe(Harris, $"the geometry column must survive the round-trip through Parquet. {DuckDbAdvice}");
    }

    [Fact]
    public async Task The_CBSA_step_skips_the_title_rows_and_the_notes_and_concatenates_the_FIPS_columns()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var refdata = directory.Combine("refdata");

        await new ReferenceDataPreparer(refdata, new FakeReferenceFileSource())
            .PrepareAsync(force: false, timeout.Token);

        var rows = Csv(Path.Combine(refdata, ReferenceSteps.CbsaFile));

        rows.Header.ShouldBe(
            ["cbsa_code", "cbsa_title", "area_type", "county_geoid", "county_name", "state_name", "central_outlying"],
            "the committed cbsa_excerpt.csv fixture has these columns, so the pipeline must write them.");

        rows.Values.Count.ShouldBe(
            18,
            "the trimmed workbook holds 18 data rows: Beaumont 3, Houston 10, Springfield MO 5. Rows 1-2 "
            + "are a title, row 3 is the header, and the last three rows are a blank and two notes.");

        rows.Column("cbsa_code").Distinct().Order(StringComparer.Ordinal)
            .ShouldBe(["13140", "26420", "44180"]);

        rows.Column("county_geoid").ShouldAllBe(
            geoid => geoid.Length == 5 && geoid.All(char.IsDigit));

        rows.Where("cbsa_code", "26420").Select(row => row["county_geoid"]).Order(StringComparer.Ordinal)
            .ShouldBe(
                HoustonCbsaCounties,
                Case.Sensitive,
                "'48' and '015' are separate string columns in the source and must be concatenated into "
                + "48015 - not added, and not parsed as numbers, which would lose the leading zero.");

        rows.Where("cbsa_code", "26420").Select(row => row["cbsa_title"]).Distinct()
            .ShouldBe(["Houston-Pasadena-The Woodlands, TX"]);
    }

    [Fact]
    public async Task The_ZCTA_step_handles_the_BOM_and_drops_the_rows_whose_ZCTA_is_empty()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var refdata = directory.Combine("refdata");

        await new ReferenceDataPreparer(refdata, new FakeReferenceFileSource())
            .PrepareAsync(force: false, timeout.Token);

        var rows = Csv(Path.Combine(refdata, ReferenceSteps.ZctaFile));

        rows.Header.ShouldBe(["zcta5", "county_geoid"]);

        rows.Values.Count.ShouldBe(
            8,
            "the trimmed source has 10 data rows, two of which have an empty ZCTA (a county that "
            + "contains none) and must be dropped, or a ZIP lookup matches the blank.");

        rows.Column("zcta5").ShouldAllBe(zip => zip.Length == 5 && zip.All(char.IsDigit));

        rows.Where("zcta5", "77494").Select(row => row["county_geoid"]).Order(StringComparer.Ordinal)
            .ShouldBe(["48157", "48201", "48473"], Case.Sensitive, "ZIP 77494 spans three counties.");

        // A BOM left on the first field turns the header into "﻿OID_ZCTA5_20" and every lookup of
        // the first column silently misses.
        rows.Header[0].ShouldNotStartWith("﻿");
        rows.Column("zcta5").ShouldNotContain(value => value.StartsWith('﻿'));
    }

    [Fact]
    public async Task The_manifest_records_every_step_with_its_source_url_and_fetch_time()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var refdata = directory.Combine("refdata");

        var result = await new ReferenceDataPreparer(refdata, new FakeReferenceFileSource())
            .PrepareAsync(force: false, timeout.Token);

        result.ManifestPath.ShouldBe(Path.Combine(refdata, ReferenceSteps.ManifestFile));

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(result.ManifestPath, timeout.Token));
        var steps = manifest.RootElement.GetProperty("steps").EnumerateArray().ToList();

        steps.Select(step => step.GetProperty("step").GetString()).ShouldBe(ReferenceSteps.All, ignoreOrder: true);

        foreach (var step in steps)
        {
            var name = step.GetProperty("step").GetString()!;
            step.GetProperty("file").GetString().ShouldBe(ReferenceSteps.FileFor(name));
            step.GetProperty("sourceUrl").GetString().ShouldBe(
                ReferenceDataFixture.SourceUrlFor(name),
                "POC-2: the manifest records where each file came from.");
            step.GetProperty("retrievedAt").GetDateTimeOffset().ShouldBe(
                FakeReferenceFileSource.RetrievedAt,
                "...and when, so a stale vintage is visible without re-downloading.");
        }
    }

    [Fact]
    public async Task A_second_run_skips_every_step_and_never_asks_for_a_source_again()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var refdata = directory.Combine("refdata");
        var source = new FakeReferenceFileSource();
        var preparer = new ReferenceDataPreparer(refdata, source);

        await preparer.PrepareAsync(force: false, timeout.Token);
        var before = Fingerprints(refdata);

        var second = await preparer.PrepareAsync(force: false, timeout.Token);

        second.Steps.ShouldAllBe(step => step.Skipped);
        source.TotalCalls.ShouldBe(
            3,
            "a step whose output is already there must not fetch its source again - that is what makes "
            + "prepare_data idempotent, and what keeps census.gov out of a unit test run.");

        Fingerprints(refdata).ShouldBe(before, "a skipped step must not rewrite its output.");
    }

    [Fact]
    public async Task A_missing_manifest_makes_every_step_run_again_so_provenance_is_never_lost()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var refdata = directory.Combine("refdata");
        var source = new FakeReferenceFileSource();
        var preparer = new ReferenceDataPreparer(refdata, source);

        var first = await preparer.PrepareAsync(force: false, timeout.Token);
        source.TotalCalls.ShouldBe(3);

        // The outputs are all still there; only the record of where they came from is gone.
        File.Delete(first.ManifestPath);

        var second = await preparer.PrepareAsync(force: false, timeout.Token);

        second.Steps.ShouldAllBe(
            step => !step.Skipped,
            "a step counts as done only when its output AND its manifest entry are there. Skipping on "
            + "the file alone would rewrite the manifest without the source URL and vintage, and the "
            + "provenance POC-2 asks for would be gone for good.");

        source.TotalCalls.ShouldBe(6, "every step had to fetch its source again to rebuild the record.");

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(second.ManifestPath, timeout.Token));
        var steps = manifest.RootElement.GetProperty("steps").EnumerateArray().ToList();
        steps.Select(step => step.GetProperty("step").GetString()).ShouldBe(ReferenceSteps.All, ignoreOrder: true);

        foreach (var step in steps)
        {
            var name = step.GetProperty("step").GetString()!;

            step.GetProperty("sourceUrl").GetString().ShouldBe(
                ReferenceDataFixture.SourceUrlFor(name),
                "a rebuilt manifest carries the real source URL, not null.");
            step.GetProperty("retrievedAt").GetDateTimeOffset().ShouldBe(FakeReferenceFileSource.RetrievedAt);
            step.GetProperty("rows").GetInt32().ShouldBeGreaterThan(
                0,
                $"'{name}' reports the rows it actually wrote, not 0.");
        }
    }

    [Fact]
    public async Task A_manifest_missing_one_step_re_runs_only_that_step()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var refdata = directory.Combine("refdata");
        var source = new FakeReferenceFileSource();
        var preparer = new ReferenceDataPreparer(refdata, source);

        var first = await preparer.PrepareAsync(force: false, timeout.Token);
        var countiesRows = first.Steps.Single(step => step.Step == ReferenceSteps.Counties).Rows;

        // Keep only the counties entry, as a run interrupted after its first step would leave behind.
        await ReferenceManifest.WriteAsync(
            first.ManifestPath,
            FakeReferenceFileSource.RetrievedAt,
            first.Steps.Where(step => step.Step == ReferenceSteps.Counties),
            timeout.Token);

        var second = await preparer.PrepareAsync(force: false, timeout.Token);

        second.Steps.Single(step => step.Step == ReferenceSteps.Counties).Skipped.ShouldBeTrue(
            "its output and its manifest entry are both there.");
        second.Steps.Single(step => step.Step == ReferenceSteps.Cbsa).Skipped.ShouldBeFalse();
        second.Steps.Single(step => step.Step == ReferenceSteps.Zcta).Skipped.ShouldBeFalse();

        source.Calls[ReferenceSteps.Counties].ShouldBe(1, "the complete step must not fetch again.");
        source.Calls[ReferenceSteps.Cbsa].ShouldBe(2);
        source.Calls[ReferenceSteps.Zcta].ShouldBe(2);

        // The skipped step keeps reporting what the manifest remembers, so one step re-running does not
        // erase the other's provenance on the way through.
        var counties = second.Steps.Single(step => step.Step == ReferenceSteps.Counties);
        counties.SourceUrl?.ToString().ShouldBe(ReferenceDataFixture.SourceUrlFor(ReferenceSteps.Counties));
        counties.RetrievedAt.ShouldBe(FakeReferenceFileSource.RetrievedAt);
        counties.Rows.ShouldBe(countiesRows);
    }

    [Fact]
    public async Task Force_true_fetches_and_parses_everything_again()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        var refdata = directory.Combine("refdata");
        var source = new FakeReferenceFileSource();
        var preparer = new ReferenceDataPreparer(refdata, source);

        await preparer.PrepareAsync(force: false, timeout.Token);

        var forced = await preparer.PrepareAsync(force: true, timeout.Token);

        forced.Steps.ShouldAllBe(step => !step.Skipped);
        source.TotalCalls.ShouldBe(6, "force re-reads every source.");
        source.Calls.Values.ShouldAllBe(count => count == 2);
    }

    private static string DuckDbAdvice =>
        "LOAD spatial is required on every new connection, not just at install time - a fresh connection "
        + "can see a geometry column and still fail on ST_Within.";

    private static IReadOnlyList<FileFingerprint> Fingerprints(string refdata) =>
        [.. ReferenceDataFixture.FileNames.Select(name => FileFingerprint.Of(Path.Combine(refdata, name)))];

    private static DuckDBConnection Duck()
    {
        var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        using var load = connection.CreateCommand();
        load.CommandText = "LOAD spatial";
        load.ExecuteNonQuery();
        return connection;
    }

    private static string? Scalar(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string Forward(string path) => path.Replace('\\', '/');

    private static CsvFile Csv(string path)
    {
        File.Exists(path).ShouldBeTrue($"'{path}' was not written.");
        var lines = File.ReadAllLines(path);
        lines.Length.ShouldBeGreaterThan(1, $"'{path}' has no data rows.");

        var header = SplitCsv(lines[0]);
        var values = lines.Skip(1)
            .Where(line => line.Length > 0)
            .Select(line => header.Zip(SplitCsv(line)).ToDictionary(pair => pair.First, pair => pair.Second))
            .ToList();

        return new CsvFile(header, values);
    }

    /// <summary>Enough CSV for these files: the only quoted field is a title, with no quote inside it.</summary>
    private static string[] SplitCsv(string line)
    {
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;

        foreach (var character in line)
        {
            switch (character)
            {
                case '"':
                    quoted = !quoted;
                    break;
                case ',' when !quoted:
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                default:
                    field.Append(character);
                    break;
            }
        }

        fields.Add(field.ToString());
        return [.. fields];
    }

    private sealed record CsvFile(string[] Header, List<Dictionary<string, string>> Values)
    {
        public IEnumerable<string> Column(string name) => Values.Select(row => row[name]);

        public IEnumerable<Dictionary<string, string>> Where(string name, string value) =>
            Values.Where(row => row[name] == value);
    }

    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(60));
}
