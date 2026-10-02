using System.Text.Json;
using ProspectStudio.Core.Market;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Infrastructure.Tests.Census;

/// <summary>
/// Checks the recorded CBP bodies before any logic test leans on them. Without this, a fixture that was
/// re-recorded wrongly shows up as a dozen failing sizing tests and sends the reader hunting through the
/// arithmetic instead of the fixture. Numbers come from <c>src/tests/Fixtures/cbp/README.md</c>.
/// </summary>
/// <remarks>
/// These read the fixtures with <see cref="JsonDocument"/> rather than with <see cref="CbpTable"/>, so
/// they still say something while the parser is a stub.
/// </remarks>
public class CbpFixtureIntegrityTests
{
    [Fact]
    public void The_houston_4931_body_still_returns_eight_of_the_ten_requested_counties()
    {
        var rows = Rows(CbpFixtures.Houston4931);

        rows.Select(row => row.County).Distinct().Order(StringComparer.Ordinal).ShouldBe(
            ["48015", "48039", "48071", "48157", "48167", "48201", "48339", "48473"]);

        foreach (var absent in CbpFixtures.SuppressedHoustonCountyFips)
        {
            rows.ShouldNotContain(
                row => row.County == absent,
                $"{absent} is suppressed by omission, which is the case the whole chunk is built on.");
        }
    }

    [Fact]
    public void The_houston_4931_body_still_totals_462_with_439_in_the_standard_bands()
    {
        var rows = Rows(CbpFixtures.Houston4931);

        Total(rows, CbpSizeBands.AllEstablishmentsCode).ShouldBe(462);
        Standard(rows).ShouldBe(439, "the 23-establishment gap is what the suppression note reports.");
        Banded(rows).ShouldBe(
            442,
            "adding every non-001 row naively gives 442, because county 48157's 263 (1,500-2,499) is "
            + "counted again inside its 260 (1,000+). That is the 3 that turns 439 into 442 and 23 into "
            + "20 - the plausible wrong answer.");
    }

    [Fact]
    public void The_houston_238210_body_still_totals_1310_with_1293_banded()
    {
        var rows = Rows(CbpFixtures.Houston238210);

        rows.Select(row => row.County).Distinct().Count().ShouldBe(10, "all ten counties answer for 238210.");
        Total(rows, CbpSizeBands.AllEstablishmentsCode).ShouldBe(1_310);
        Standard(rows).ShouldBe(1_293);
        Banded(rows).ShouldBe(
            1_293,
            "this response carries no detail bands at all, so naive and standard agree - which is exactly "
            + "why one worked example could not validate the nesting rule.");
    }

    [Fact]
    public void Band_263_is_in_the_4931_body_and_not_in_the_238210_one()
    {
        Rows(CbpFixtures.Houston4931).ShouldContain(
            row => row.Band == "263",
            "263 (1,500-2,499) is a detail band nested inside 260 (1,000+), whose label is accurate.");

        Rows(CbpFixtures.Houston238210).ShouldNotContain(
            row => row.Band == "263",
            "the band set varies by query, which is why no band list may be hard-coded.");
    }

    [Fact]
    public void The_all_establishments_band_equals_the_sum_of_the_others_where_nothing_is_suppressed()
    {
        // Harris County, NAICS 238210: the arithmetic identity behind "never sum 001".
        var rows = Rows(CbpFixtures.Harris238210);

        Total(rows, CbpSizeBands.AllEstablishmentsCode).ShouldBe(833);
        Standard(rows).ShouldBe(833);
    }

    [Fact]
    public void A_naics_code_already_contains_its_descendants()
    {
        Total(Rows(CbpFixtures.Harris4931), CbpSizeBands.AllEstablishmentsCode).ShouldBe(360);
        Total(Rows(CbpFixtures.Harris49311), CbpSizeBands.AllEstablishmentsCode).ShouldBe(
            256,
            "49311's 256 are inside 4931's 360, so 4931 + 49311 would report 616.");
    }

    [Fact]
    public void The_all_sectors_body_still_shows_the_detail_bands_adding_up_to_band_260()
    {
        var rows = Rows(CbpFixtures.Harris00);

        Total(rows, CbpSizeBands.AllEstablishmentsCode).ShouldBe(111_215);
        Standard(rows).ShouldBe(
            111_215,
            "the nine standard bands partition the total exactly. This identity is the whole basis of the "
            + "arithmetic, so if it ever stops holding every number in the suite is suspect.");

        Total(rows, "260").ShouldBe(135);
        new[] { "262", "263", "271", "273" }.Sum(band => Total(rows, band)).ShouldBe(
            135,
            "51 + 51 + 21 + 12 = 135, which is 260 exactly: they subdivide it rather than extending it, "
            + "so 260's label '1,000 employees or more' is accurate and not misleading.");
    }

    [Fact]
    public void The_all_sectors_body_naively_summed_exceeds_its_own_total()
    {
        var rows = Rows(CbpFixtures.Harris00);
        var total = Total(rows, CbpSizeBands.AllEstablishmentsCode);

        Banded(rows).ShouldBe(111_350, "every non-001 row added together, nested bands included.");
        Banded(rows).ShouldBeGreaterThan(
            total,
            "a band sum above the published total is impossible for disjoint bands, which is the one "
            + "signal that catches a wrong band selection. A gap clamped to zero hides it.");
        (Banded(rows) - total).ShouldBe(135, "the overcount is exactly band 260, counted twice.");
    }

    [Fact]
    public void Two_of_the_all_sectors_bands_are_open_ended()
    {
        var labels = Rows(CbpFixtures.Harris00)
            .Where(row => row.Band is "260" or "273")
            .ToDictionary(row => row.Band, row => row.Label);

        labels["260"].ShouldBe("Establishments with 1,000 employees or more");
        labels["273"].ShouldBe(
            "Establishments with 5,000 employees or more",
            "both bands are open-ended, so containment cannot be decided by 'this one has an upper bound'. "
            + "A missing upper bound is infinity, and 5,000+ still sits inside 1,000+.");
    }

    [Fact]
    public void The_all_sectors_body_repeats_the_naics_header_column()
    {
        using var document = JsonDocument.Parse(CbpFixtures.Harris00);
        var header = Header(document);

        header.Count(name => name == "NAICS2017").ShouldBe(
            2,
            "NAICS2017 was named in get= as well as filtered on, so the contract's duplicate-header "
            + $"warning is now backed by recorded data rather than only by prose. Header: {string.Join(",", header)}");
        header[^2..].ShouldBe(["state", "county"], "state and county stay the trailing columns.");
    }

    [Fact]
    public void County_48157_is_the_trap_in_miniature()
    {
        // Its naive band sum lands exactly on its 001, so a naive sum makes a county that is really three
        // short look complete. This is why the bug survived a worked example.
        var rows = Rows(CbpFixtures.Houston4931).Where(row => row.County == "48157").ToList();

        Total(rows, CbpSizeBands.AllEstablishmentsCode).ShouldBe(32);
        Banded(rows).ShouldBe(32, "naive: it looks like nothing is suppressed.");
        Standard(rows).ShouldBe(29, "really: three establishments have no published band.");
        Total(rows, "260").ShouldBe(3);
        Total(rows, "263").ShouldBe(3, "the whole of 260 here is in fact 1,500-2,499.");
    }

    [Fact]
    public void Estab_is_never_blank_null_or_negative_anywhere_in_the_recorded_bodies()
    {
        string[] bodies =
        [
            CbpFixtures.Houston4931, CbpFixtures.Houston238210, CbpFixtures.Harris4931,
            CbpFixtures.Harris49311, CbpFixtures.Harris238210, CbpFixtures.Harris00,
            CbpFixtures.DuplicateColumn, CbpFixtures.WithFlagColumns,
        ];

        foreach (var body in bodies)
        {
            Rows(body).ShouldAllBe(
                row => row.Establishments > 0,
                "Census suppresses by leaving rows out, not by blanking ESTAB. A value check would find "
                + "nothing and report suppressed data as complete.");
        }
    }

    [Fact]
    public void A_suppressed_employment_value_is_a_zero_with_an_N_flag_rather_than_a_blank()
    {
        using var document = JsonDocument.Parse(CbpFixtures.WithFlagColumns);
        var header = Header(document);

        var emp = header.IndexOf("EMP");
        var empFlag = header.IndexOf("EMP_F");
        emp.ShouldBeGreaterThan(-1);
        empFlag.ShouldBeGreaterThan(-1);

        var flagged = document.RootElement.EnumerateArray()
            .Skip(1)
            .Where(row => row[empFlag].ValueKind == JsonValueKind.String && row[empFlag].GetString() == "N")
            .ToList();

        flagged.ShouldNotBeEmpty("the body was recorded because it has EMP_F = N rows.");
        flagged.ShouldAllBe(
            row => row[emp].GetString() == "0",
            "EMP comes back \"0\" on a suppressed row, which is not a real zero and must not be read.");
    }

    [Fact]
    public void The_duplicate_column_body_still_repeats_empszes_away_from_the_first_copy()
    {
        using var document = JsonDocument.Parse(CbpFixtures.DuplicateColumn);
        var header = Header(document);

        header.Count(name => name == "EMPSZES").ShouldBe(
            2,
            $"naming EMPSZES in get= and filtering on it duplicates the column. Header: {string.Join(",", header)}");
        header.IndexOf("EMPSZES").ShouldBe(1);
        header.LastIndexOf("EMPSZES").ShouldBe(
            5,
            "the repeat lands after the get= columns, not beside the original.");
    }

    [Fact]
    public void The_metadata_fixture_still_names_naics2017_and_publishes_no_band_list()
    {
        using var document = JsonDocument.Parse(CbpFixtures.Variables2023);
        var variables = document.RootElement.GetProperty("variables");

        variables.TryGetProperty("NAICS2017", out _).ShouldBeTrue("2023's NAICS variable is NAICS2017.");
        variables.TryGetProperty("NAICS2022", out _).ShouldBeFalse(
            "NAICS2022 does not exist in this vintage, so hard-coding it would have broken every query.");

        var bands = variables.GetProperty("EMPSZES");
        bands.TryGetProperty("values", out _).ShouldBeFalse(
            "EMPSZES publishes no value list, which is why the bands are read off each data response.");
        bands.GetProperty("attributes").GetString().ShouldBe(
            "EMPSZES_LABEL",
            "the label column is the only published statement of a band's bounds.");
    }

    [Fact]
    public void The_recorded_404_is_an_html_page_rather_than_json()
    {
        CbpFixtures.NotFoundHtml.ShouldContain(
            "404", Case.Sensitive,
            "a probed year that does not exist answers 404 with a Tomcat HTML page; parsing it as JSON "
            + "throws, so the status has to be checked first.");
        Should.Throw<JsonException>(() => JsonDocument.Parse(CbpFixtures.NotFoundHtml));
    }

    [Fact]
    public void No_fixture_holds_anything_that_looks_like_a_request_url()
    {
        var files = Directory.GetFiles(CbpFixtures.Directory, "*", SearchOption.AllDirectories);
        files.Length.ShouldBeGreaterThan(5, $"nothing was copied to '{CbpFixtures.Directory}'.");

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            text.ShouldNotContain("api.census.gov", Case.Insensitive, $"'{name}' holds a URL; fixtures are response bodies only.");
            text.ShouldNotContain("key=", Case.Insensitive, $"'{name}' holds a key parameter.");
        }
    }

    private sealed record Row(string County, string Band, string Label, int Establishments);

    private static List<string> Header(JsonDocument document) =>
        [.. document.RootElement[0].EnumerateArray().Select(value => value.GetString() ?? string.Empty)];

    private static List<Row> Rows(string json)
    {
        using var document = JsonDocument.Parse(json);
        var header = Header(document);

        // First occurrence of each name, which is what index-based reading has to do too.
        var estab = header.IndexOf("ESTAB");
        var band = header.IndexOf("EMPSZES");
        var label = header.IndexOf("EMPSZES_LABEL");
        var state = header.IndexOf("state");
        var county = header.IndexOf("county");

        return
        [
            .. document.RootElement.EnumerateArray().Skip(1).Select(row => new Row(
                (row[state].GetString() ?? string.Empty) + row[county].GetString(),
                row[band].GetString() ?? string.Empty,
                row[label].GetString() ?? string.Empty,
                int.Parse(row[estab].GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture))),
        ];
    }

    private static int Total(List<Row> rows, string band) =>
        rows.Where(row => row.Band == band).Sum(row => row.Establishments);

    /// <summary>Every non-<c>001</c> row added naively, nested detail bands and all.</summary>
    private static int Banded(List<Row> rows) =>
        rows.Where(row => row.Band != CbpSizeBands.AllEstablishmentsCode).Sum(row => row.Establishments);

    /// <summary>Only the nine bands that partition the total, which is the sum that means something.</summary>
    private static int Standard(List<Row> rows) =>
        rows.Where(row => CbpFixtures.StandardBandCodes.Contains(row.Band)).Sum(row => row.Establishments);
}
