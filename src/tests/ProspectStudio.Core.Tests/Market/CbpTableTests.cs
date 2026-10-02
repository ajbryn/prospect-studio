using ProspectStudio.Core.Market;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Core.Tests.Market;

/// <summary>
/// Parsing the Census data API's array-of-arrays body, against bodies recorded from the live API
/// (<c>src/tests/Fixtures/cbp/README.md</c>). The shape is awkward in three ways that each produce a
/// plausible-but-wrong number rather than an error: every value is a string, a variable named in
/// <c>get=</c> and filtered on appears twice in the header, and <c>state</c>/<c>county</c> are trailing
/// columns that have to be concatenated into a GEOID.
/// </summary>
public class CbpTableTests
{
    [Fact]
    public void Every_data_row_is_read_and_the_header_is_skipped()
    {
        var rows = CbpTable.Parse(CbpFixtures.Houston4931);

        rows.Count.ShouldBe(35, "the recorded body has a header row and 35 data rows.");
        rows.ShouldAllBe(row => row.Naics == "4931");
        rows.ShouldAllBe(row => row.Establishments > 0, "ESTAB is never empty, null or negative.");
        rows.ShouldNotContain(
            row => row.SizeBandCode == "EMPSZES",
            "the first element of the body is the header row, not data.");
    }

    [Fact]
    public void The_trailing_state_and_county_columns_are_concatenated_into_a_geoid()
    {
        var counties = CbpTable.Parse(CbpFixtures.Houston4931)
            .Select(row => row.CountyFips)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        counties.ShouldBe(
            ["48015", "48039", "48071", "48157", "48167", "48201", "48339", "48473"],
            "eight of the ten requested counties answered; the GEOID is state + county, which is the "
            + "form resolve_geography hands over.");
        counties.ShouldNotContain("201", "a bare county code would never match a resolved scope.");
    }

    [Fact]
    public void A_column_name_that_appears_twice_is_tolerated()
    {
        // The header is ESTAB, EMPSZES, EMPSZES_LABEL, NAICS2017, NAICS2017, state, county: NAICS2017 is
        // duplicated because it is both named in get= and filtered on. Reading EMPSZES by a name->index
        // map built from the last occurrence, or assuming fixed positions, mangles this.
        var rows = CbpTable.Parse(CbpFixtures.Harris4931);

        rows.Count.ShouldBe(10);
        rows.ShouldAllBe(row => row.Naics == "4931");
        rows.ShouldAllBe(row => row.CountyFips == CbpFixtures.HarrisCountyFips);
        rows.Single(row => row.SizeBandCode == CbpSizeBands.AllEstablishmentsCode)
            .Establishments.ShouldBe(360);
    }

    [Fact]
    public void A_duplicate_column_away_from_the_first_one_is_tolerated()
    {
        // Filtering on EMPSZES as well as NAICS2017 repeats both, and the repeats land after the get=
        // columns: ESTAB, EMPSZES, EMPSZES_LABEL, NAICS2017, NAICS2017, EMPSZES, state, county. The
        // second EMPSZES is at index 5, so "the duplicate sits beside the original" is not a rule.
        var rows = CbpTable.Parse(CbpFixtures.DuplicateColumn);

        rows.Count.ShouldBe(3);
        rows.ShouldAllBe(row => row.SizeBandCode == CbpSizeBands.AllEstablishmentsCode);
        rows.Single(row => row.CountyFips == "48157").Establishments.ShouldBe(32);
        rows.Single(row => row.CountyFips == "48201").Establishments.ShouldBe(360);
        rows.Single(row => row.CountyFips == "48473").Establishments.ShouldBe(8);
    }

    [Fact]
    public void Flag_columns_that_come_back_as_json_null_are_tolerated()
    {
        // This body asked for EMP, ESTAB_F and EMP_F as well. ESTAB_F and EMP_F are JSON null rather than
        // strings, so "every value is a string" is not quite true and a non-nullable string read throws.
        var rows = CbpTable.Parse(CbpFixtures.WithFlagColumns);

        rows.ShouldNotBeEmpty();
        rows.ShouldAllBe(row => row.Naics == "238210");

        var anderson = rows.Where(row => row.CountyFips == "48001").ToList();
        anderson.Single(row => row.SizeBandCode == CbpSizeBands.AllEstablishmentsCode)
            .Establishments.ShouldBe(8);
        anderson.Single(row => row.SizeBandCode == "210").Establishments.ShouldBe(
            3,
            "ESTAB is 3 on that row although EMP is \"0\" with EMP_F \"N\". EMP is the suppressed column "
            + "here, not a real zero, and nothing may read it.");
    }

    [Fact]
    public void The_naics_column_is_found_without_knowing_the_vintage()
    {
        // 2023 publishes NAICS2017; earlier vintages used other names and later ones will too. The
        // header below is the recorded one with the column renamed, which is the whole difference a new
        // vintage makes to the body's shape.
        var nextVintage = CbpFixtures.Harris4931.Replace("NAICS2017", "NAICS2022", StringComparison.Ordinal);

        var rows = CbpTable.Parse(nextVintage);

        rows.Count.ShouldBe(10, "the NAICS column is found by its NAICS prefix, not by a hard-coded name.");
        rows.ShouldAllBe(row => row.Naics == "4931");
    }

    [Fact]
    public void The_band_label_survives_parsing_alongside_the_code()
    {
        var rows = CbpTable.Parse(CbpFixtures.Houston4931);

        rows.Single(row => row.CountyFips == "48157" && row.SizeBandCode == "263")
            .SizeBandLabel.ShouldBe(
                "Establishments with 1,500 to 2,499 employees",
                "the label is the only source of a band's bounds, so it has to survive parsing.");
    }
}
