using System.Globalization;
using DuckDB.NET.Data;
using ProspectStudio.Infrastructure.Reference;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Infrastructure.Tests.Places;

/// <summary>
/// The committed Overture fixtures still say what the candidate tests assume. These are facts about
/// the real dataset, verified against release <c>2026-09-23.1</c> in C4; if a regeneration ever
/// silently changes one, the failure should point here rather than at <c>find_candidates</c>.
/// </summary>
/// <remarks>
/// The CRS assertions are the reason this file exists. A fixture that quietly lost its
/// <c>OGC:CRS84</c> geometry would make every candidate test pass while the production query threw a
/// Binder error against the real file - the one failure mode a fixture-based test suite cannot
/// otherwise see.
/// </remarks>
public class PlacesFixtureIntegrityTests
{
    private static readonly string Places = DuckDbSpatial.PathLiteral(RepoFixtures.SamplePlacesParquet);
    private static readonly string Counties = DuckDbSpatial.PathLiteral(RepoFixtures.CountiesHoustonParquet);

    [Fact]
    public void The_fixture_has_the_real_Overture_columns_and_types()
    {
        using var db = DuckDbSpatial.Open();

        var columns = Rows(db, $"DESCRIBE SELECT * FROM read_parquet('{Places}')")
            .ToDictionary(row => (string)row[0]!, row => (string)row[1]!, StringComparer.Ordinal);

        columns.Keys.Order(StringComparer.Ordinal).ToList().ShouldBe(
            ["addresses", "basic_category", "bbox", "confidence", "geometry", "id", "name", "phones", "taxonomy", "websites"],
            "technical-design §6.1's column list, no more and no less. In particular there is NO "
            + "'categories' column (Overture removed it) and no county column - the county comes from "
            + $"the spatial join in §6.3. Got: {string.Join(", ", columns.Keys)}");

        columns["taxonomy"].ShouldBe(
            "STRUCT(\"primary\" VARCHAR, hierarchy VARCHAR[], alternates VARCHAR[])",
            "taxonomy is a struct, not a string; 'primary' is a reserved word and stays quoted.");
        columns["addresses"].ShouldBe(
            "STRUCT(freeform VARCHAR, locality VARCHAR, postcode VARCHAR, region VARCHAR, country VARCHAR)[]",
            "addresses is a LIST of structs - the maximum length in Texas is 1, but the column is a list.");
        columns["websites"].ShouldBe("VARCHAR[]");
        columns["phones"].ShouldBe("VARCHAR[]");
        columns["confidence"].ShouldBe("DOUBLE");
        columns["bbox"].ShouldBe("STRUCT(xmin DOUBLE, xmax DOUBLE, ymin DOUBLE, ymax DOUBLE)");
    }

    [Fact]
    public void The_geometry_carries_the_OGC_CRS84_reference_system()
    {
        using var db = DuckDbSpatial.Open();

        DuckDbSpatial.Scalar(db, $"SELECT typeof(geometry) FROM read_parquet('{Places}') LIMIT 1")
            .ShouldBe(
                "GEOMETRY('OGC:CRS84')",
                "Overture publishes OGC:CRS84. The CRS travels in the column type through Parquet, and "
                + "it is what makes §6.3's ST_SetCRS necessary.");
    }

    [Fact]
    public void The_counties_fixture_carries_EPSG_4269_so_the_pairing_is_the_real_one()
    {
        using var db = DuckDbSpatial.Open();

        DuckDbSpatial.Scalar(db, $"SELECT typeof(geometry) FROM read_parquet('{Counties}') LIMIT 1")
            .ShouldBe(
                "GEOMETRY('EPSG:4269')",
                "the Census shapefile is NAD83. Two fixtures in the same CRS would make the tests pass "
                + "while real data threw.");
    }

    [Fact]
    public void Joining_the_two_without_ST_SetCRS_still_fails_the_way_real_data_does()
    {
        using var db = DuckDbSpatial.Open();

        var exception = Should.Throw<DuckDBException>(() => DuckDbSpatial.Scalar(
            db,
            $"""
             SELECT count(*)
             FROM read_parquet('{Places}') p
             JOIN read_parquet('{Counties}') c ON ST_Within(p.geometry, c.geometry)
             """));

        exception.Message.ShouldContain(
            "coordinate reference system",
            Case.Insensitive,
            "this is the trap technical-design §6.3 records: DuckDB refuses ST_Within across "
            + "mismatched CRS at bind time. If this test stops throwing, the fixtures have drifted "
            + $"into agreement and no longer reproduce real data. Got: {exception.Message}");
    }

    [Fact]
    public void Aligning_the_county_side_makes_the_join_work()
    {
        using var db = DuckDbSpatial.Open();

        var joined = Convert.ToInt32(
            DuckDbSpatial.Scalar(
                db,
                $"""
                 SELECT count(*)
                 FROM read_parquet('{Places}') p
                 JOIN (SELECT GEOID, ST_SetCRS(geometry, 'OGC:CRS84') AS geometry
                       FROM read_parquet('{Counties}')) c
                   ON ST_Within(p.geometry, c.geometry)
                 """),
            CultureInfo.InvariantCulture);

        joined.ShouldBe(
            SamplePlaces.All.Count,
            "every fixture place sits inside one of the eleven committed counties, so the aligned join "
            + "returns exactly one row each. Aligning the COUNTY side is the cheap fix (§6.3).");
    }

    [Fact]
    public void Every_point_falls_inside_the_county_it_claims()
    {
        // poc/fixtures/README.md: where the jittered point and the intended county disagree, move the
        // point, not the expectation. Ten points were moved in C4 for exactly this.
        using var db = DuckDbSpatial.Open();

        var joined = Rows(
                db,
                $"""
                 SELECT p.id, c.GEOID
                 FROM read_parquet('{Places}') p
                 JOIN (SELECT GEOID, ST_SetCRS(geometry, 'OGC:CRS84') AS geometry
                       FROM read_parquet('{Counties}')) c
                   ON ST_Within(p.geometry, c.geometry)
                 """)
            .ToDictionary(row => (string)row[0]!, row => (string)row[1]!, StringComparer.Ordinal);

        var wrong = SamplePlaces.All
            .Where(place => !joined.TryGetValue(place.Id, out var fips) || fips != place.CountyFips)
            .Select(place => $"{place.Id} says {place.CountyFips}, join says {joined.GetValueOrDefault(place.Id) ?? "nothing"}")
            .ToList();

        wrong.ShouldBeEmpty(
            "a row whose county_fips disagrees with the spatial join would make every scoping test "
            + "assert the wrong thing. Move the point and rebuild the Parquet with "
            + "src/tests/Fixtures/places/build-places-fixture.cs.");
    }

    [Fact]
    public void The_fixture_covers_the_Houston_metro_and_deliberately_overflows_into_Beaumont()
    {
        SamplePlaces.All.Count.ShouldBe(115);
        SamplePlaces.InHoustonCbsa.Count.ShouldBe(112);

        SamplePlaces.InJeffersonCounty.Select(place => place.Id).ToList().ShouldBe(
            ["fx_0101", "fx_0102", "fx_0103"],
            "Jefferson County 48245 is in CBSA 13140, not 26420, so these three prove a Houston scope "
            + "excludes rather than merely includes.");

        SamplePlaces.All
            .Select(place => place.CountyFips)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList()
            .ShouldBe(["48015", "48039", "48071", "48157", "48167", "48201", "48245", "48339", "48473"]);
    }

    [Fact]
    public void Five_rows_sit_below_the_confidence_floor()
    {
        SamplePlaces.All
            .Where(place => place.Confidence < 0.6)
            .Select(place => place.Id)
            .Order(StringComparer.Ordinal)
            .ToList()
            .ShouldBe(["fx_0076", "fx_0077", "fx_0078", "fx_0079", "fx_0080"]);

        SamplePlaces.All.Where(place => place.Confidence < 0.6).ShouldAllBe(
            place => SamplePlaces.HoustonCbsaCounties.Contains(place.CountyFips),
            "a low-confidence row outside the scope would be excluded for the wrong reason, so the "
            + "threshold test would prove nothing.");
    }

    [Fact]
    public void Every_taxonomy_value_in_the_fixture_is_a_real_one()
    {
        // This is the guard that the previous fixture needed and did not have: nine of its nineteen
        // categories did not exist, and because the sample profile shared the same invented strings the
        // two matched each other perfectly and real data never.
        var real = RealTaxonomy();

        foreach (var place in SamplePlaces.All)
        {
            real.ShouldContainKey(
                place.TaxonomyPrimary,
                $"{place.Id} uses taxonomy.primary '{place.TaxonomyPrimary}', which is not in "
                + "src/tests/Fixtures/places/tx_taxonomy_primary.csv - so it does not exist in Overture "
                + "release 2026-09-23.1 for Texas.");

            var (basicCategory, hierarchy) = real[place.TaxonomyPrimary];
            place.BasicCategory.ShouldBe(basicCategory, $"{place.Id}: basic_category must be the real rollup.");
            place.TaxonomyHierarchy.ToList().ShouldBe(hierarchy, $"{place.Id}: hierarchy must be the real one.");
        }
    }

    [Fact]
    public void Every_category_in_the_sample_search_profile_is_a_real_one()
    {
        var real = RealTaxonomy();
        var profile = System.Text.Json.JsonDocument.Parse(File.ReadAllText(RepoFixtures.SampleSearchProfile));
        var root = profile.RootElement;

        var categories = root.GetProperty("segments").EnumerateArray()
            .SelectMany(segment => segment.GetProperty("overtureCategories").EnumerateArray())
            .Concat(root.GetProperty("exclusions").GetProperty("overtureCategories").EnumerateArray())
            .Select(value => value.GetString() ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var invented = categories.Where(category => !real.ContainsKey(category)).ToList();

        invented.ShouldBeEmpty(
            "poc/fixtures/sample-search-profile.json must only name categories that exist in Overture. "
            + "A profile of invented categories matches a fixture of invented categories and nothing "
            + "else, which is how C4's test list came to warn about this.");
    }

    [Fact]
    public void Taxonomy_primary_is_always_the_last_element_of_the_hierarchy()
    {
        // Verified against the real data. It is what lets the category filter check the hierarchy and
        // the primary in one pass (§6.3) without missing a leaf.
        foreach (var place in SamplePlaces.All)
        {
            place.TaxonomyHierarchy[^1].ShouldBe(place.TaxonomyPrimary, place.Id);
        }
    }

    [Fact]
    public void Basic_category_is_often_not_the_leaf()
    {
        // The reason sites store both columns. If the fixture had basic_category == primary everywhere,
        // nothing would prove the two are different things.
        SamplePlaces.All
            .Count(place => place.BasicCategory != place.TaxonomyPrimary)
            .ShouldBeGreaterThan(60);
    }

    [Fact]
    public void Every_documented_row_still_plays_the_part_its_note_claims()
    {
        // The guard this fixture needed three times over. A row whose purpose is to exercise a path -
        // the missing freeform address, the generic host, the first-of-list extraction - stops
        // exercising it the moment a profile edit makes it no longer match, and nothing fails. The
        // suite stays green while testing one thing less, which is the worst way to lose coverage.
        //
        // It has happened three times in C4 alone: the dealer rows sat in an excluded category so C5's
        // suppression would have had nothing to remove; machine_and_tool_rental was left with no rows
        // at all; and dropping storage_facility from the profile disarmed fx_0111, the only row
        // covering a missing freeform address. Each was caught by eye, or by CI, rather than by a test.
        var profile = SampleProfile.Load();

        var wrong = FixtureTags.Tagged
            .Where(row => profile.Outcome(row.Place) != row.Expected)
            .Select(row =>
                $"  {row.Place.Id} is tagged [{row.Expected.ToString().ToLowerInvariant()}] but the "
                + $"profile would treat it as [{profile.Outcome(row.Place).ToString().ToLowerInvariant()}]: "
                + $"{profile.Explain(row.Place)}")
            .ToList();

        wrong.ShouldBeEmpty(
            $"""
             {wrong.Count} fixture row(s) no longer do what poc/fixtures/sample-places.csv says they do.

             {string.Join(Environment.NewLine, wrong)}

             Either the row drifted or the profile did. Fix the row - move the point, change the
             category - rather than the tag: the tag is the coverage this fixture exists to provide,
             and relabelling it to match reality is how the coverage disappears quietly.
             """);
    }

    [Fact]
    public void Every_behaviour_the_fixture_claims_to_cover_has_at_least_one_row()
    {
        // The other half of the same guard: the test above passes vacuously if the rows simply vanish.
        // Each tag class has to stay populated, so deleting the last row of a kind fails here instead
        // of quietly narrowing what the suite checks.
        foreach (var expectation in Enum.GetValues<FixtureExpectation>())
        {
            FixtureTags.Tagged.Count(row => row.Expected == expectation).ShouldBeGreaterThan(
                0,
                $"no fixture row is tagged [{expectation.ToString().ToLowerInvariant()}] any more, so "
                + "nothing exercises that outcome. See the row table in "
                + "src/tests/Fixtures/places/README.md.");
        }

        // And the named paths, by the row that carries each. These are the ones a profile edit has
        // actually broken before, so they are pinned by id rather than only by count.
        foreach (var (id, behaviour) in new[]
                 {
                     ("fx_0110", "first-of-list website and phone extraction"),
                     ("fx_0111", "a missing freeform address"),
                     ("fx_0045", "a missing freeform address and no website"),
                     ("fx_0104", "a generic-host website that must not form a domain key"),
                     ("fx_0106", "a category matched only through the hierarchy"),
                     ("fx_0065", "a ZIP+4 postcode on a ZIP that C5 routes on"),
                     ("fx_0112", "an awkwardly long name"),
                     ("fx_0013", "the domain-key duplicate"),
                     ("fx_0014", "the name-and-proximity duplicate"),
                     ("fx_0113", "the name-and-geohash duplicate"),
                 })
        {
            FixtureTags.Of(SamplePlaces.Row(id)).ShouldBe(
                FixtureExpectation.Candidate,
                $"{id} is what covers {behaviour}; it has to reach the campaign for that path to run.");
        }
    }

    [Fact]
    public void The_profile_still_selects_the_documented_number_of_rows()
    {
        // Pins the arithmetic the README and the find_candidates contract test both quote, in the fast
        // suite rather than only in the slow one. A profile edit shows up here first, naming the count
        // that moved, instead of as a bare 78-versus-76 in an MCP test two minutes later.
        var profile = SampleProfile.Load();

        var selected = SamplePlaces.All.Where(profile.Selects).ToList();
        var surviving = selected.Where(place => !profile.IsExcluded(place)).ToList();

        selected.Count.ShouldBe(84, "rows matching a category or keyword, in the ten counties, at confidence >= 0.6.");
        surviving.Count.ShouldBe(81, "after exclusions.overtureCategories and exclusions.keywords.");
        (surviving.Count - 3).ShouldBe(78, "three of those are duplicates, leaving the leads find_candidates stores.");
    }

    [Fact]
    public void The_suppression_targets_are_in_categories_a_search_actually_returns()
    {
        // These three - two dealers and a competitor - were originally machine_and_tool_rental, which
        // the sample profile excludes. A profile-driven search therefore never surfaced them, so C5's
        // dealer and competitor suppression would have had nothing to suppress and its test could not
        // have failed. A fixture that quietly guarantees a green test is worse than no fixture.
        string[] targets = ["fx_0015", "fx_0016", "fx_0020"];
        var profileCategories = ProfileCategories();
        var excluded = ProfileExclusions();

        foreach (var id in targets)
        {
            var place = SamplePlaces.Row(id);
            var hierarchy = place.TaxonomyHierarchy.ToHashSet(StringComparer.Ordinal);

            (profileCategories.Contains(place.TaxonomyPrimary) || hierarchy.Overlaps(profileCategories))
                .ShouldBeTrue($"{id} ('{place.Name}', {place.TaxonomyPrimary}) must be in a category the "
                    + "sample profile asks for, or C5 cannot prove suppression removes it.");

            excluded.Overlaps(hierarchy.Append(place.TaxonomyPrimary)).ShouldBeFalse(
                $"{id} must not also be in an excluded category, or it never reaches suppression.");

            SamplePlaces.HoustonCbsaCounties.ShouldContain(place.CountyFips, id);
            place.Confidence.ShouldBeGreaterThanOrEqualTo(0.6, id);
        }
    }

    [Fact]
    public void Both_halves_of_the_profile_exclusions_have_a_row_to_prove_them()
    {
        // fx_0075 and fx_0114 are pulled in by a name keyword and then dropped for their category;
        // fx_0115 is pulled in by its category and then dropped for a name keyword. Without the second
        // kind, exclusions.keywords would be a profile field nothing ever exercised.
        var excludedCategories = ProfileExclusions();

        foreach (var id in new[] { "fx_0075", "fx_0114" })
        {
            var place = SamplePlaces.Row(id);
            excludedCategories.Overlaps(place.TaxonomyHierarchy.Append(place.TaxonomyPrimary))
                .ShouldBeTrue($"{id} exists to be dropped by exclusions.overtureCategories.");
        }

        var byKeyword = SamplePlaces.Row("fx_0115");
        byKeyword.Name.ShouldContain(
            "Equipment Rental",
            Case.Insensitive,
            "the sample profile's exclusions.keywords holds 'equipment rental'.");
        ProfileCategories().ShouldContain(
            byKeyword.TaxonomyPrimary,
            "and its category must be a target, or the keyword exclusion would not be what removed it.");
    }

    [Fact]
    public void The_awkward_field_shapes_are_all_present()
    {
        SamplePlaces.All.Count(place => place.Websites.Count == 0).ShouldBe(23, "rows with no website.");
        SamplePlaces.All.Count(place => place.Websites.Count > 1).ShouldBe(1, "fx_0110 lists two websites.");
        SamplePlaces.All.Count(place => place.Phones.Count > 1).ShouldBe(1, "fx_0110 lists two phones.");

        SamplePlaces.All
            .Where(place => place.Freeform is null)
            .Select(place => place.Id)
            .Order(StringComparer.Ordinal)
            .ToList()
            .ShouldBe(["fx_0045", "fx_0111"], "99,004 real Texas rows have no freeform address.");

        SamplePlaces.All
            .Where(place => place.Postcode.Length > 5)
            .Select(place => place.Id)
            .Order(StringComparer.Ordinal)
            .ToList()
            .ShouldBe(["fx_0021", "fx_0049", "fx_0065", "fx_0110"], "ZIP+4 postcodes, as the real data has.");

        SamplePlaces.All.ShouldAllBe(place => place.Region == "TX", "the real region is 'TX', not 'US-TX'.");
        SamplePlaces.All.ShouldAllBe(place => place.Phones.Count == 0 || place.Phones[0].All(char.IsAsciiDigit),
            "real phones are bare digit strings, sometimes with a leading country 1.");
    }

    [Fact]
    public void The_longest_name_is_long_enough_to_overflow_a_headline() =>
        SamplePlaces.Row("fx_0112").Name.Length.ShouldBeGreaterThan(
            60,
            "C10 and C11 need a lead whose name does not fit, and it has to be a real candidate rather "
            + "than a one-off invented inside a test.");

    [Fact]
    public void The_parquet_is_small_enough_to_stay_committed() =>
        new FileInfo(RepoFixtures.SamplePlacesParquet).Length.ShouldBeLessThan(
            256 * 1024,
            "115 rows; a megabyte-sized fixture does not belong in git.");

    [Fact]
    public void Every_row_a_test_relies_on_explains_itself()
    {
        // An ordinary filler row may have a blank note; a row some test turns on may not, because
        // fixture_note is how the next chunk's author knows which rows they must not move.
        string[] loadBearing =
        [
            "fx_0001", "fx_0005", "fx_0007", "fx_0013", "fx_0014", "fx_0021", "fx_0025", "fx_0029",
            "fx_0045", "fx_0049", "fx_0065", "fx_0066", "fx_0074", "fx_0075", "fx_0076", "fx_0077",
            "fx_0078", "fx_0079", "fx_0080", "fx_0101", "fx_0102", "fx_0103", "fx_0104", "fx_0105",
            "fx_0106", "fx_0107", "fx_0108", "fx_0109", "fx_0110", "fx_0111", "fx_0112", "fx_0113",
            "fx_0015", "fx_0016", "fx_0020", "fx_0114", "fx_0115",
        ];

        var silent = loadBearing
            .Where(id => SamplePlaces.Row(id).FixtureNote.Length == 0)
            .ToList();

        silent.ShouldBeEmpty(
            "see the row-by-row table in src/tests/Fixtures/places/README.md; every id listed there "
            + "carries its reason in fixture_note too, so the CSV is readable on its own.");
    }

    /// <summary>Every category any segment of the sample profile asks for.</summary>
    private static HashSet<string> ProfileCategories() => Categories("segments");

    /// <summary>The sample profile's <c>exclusions.overtureCategories</c>.</summary>
    private static HashSet<string> ProfileExclusions() => Categories("exclusions");

    private static HashSet<string> Categories(string section)
    {
        using var profile = System.Text.Json.JsonDocument.Parse(File.ReadAllText(RepoFixtures.SampleSearchProfile));
        var root = profile.RootElement;

        var values = section == "segments"
            ? root.GetProperty("segments").EnumerateArray()
                .SelectMany(segment => segment.GetProperty("overtureCategories").EnumerateArray())
            : root.GetProperty("exclusions").GetProperty("overtureCategories").EnumerateArray();

        return [.. values.Select(value => value.GetString() ?? string.Empty)];
    }

    /// <summary>The real taxonomy table: primary to (basic_category, hierarchy).</summary>
    private static Dictionary<string, (string BasicCategory, List<string> Hierarchy)> RealTaxonomy()
    {
        var lines = File.ReadAllLines(RepoFixtures.TexasTaxonomyCsv);
        var table = new Dictionary<string, (string, List<string>)>(StringComparer.Ordinal);

        foreach (var fields in lines.Skip(1).Where(line => line.Length > 0).Select(line => line.Split(',')))
        {
            table[fields[0]] = (fields[1], [.. fields[2].Split('|')]);
        }

        return table;
    }

    private static List<object?[]> Rows(DuckDBConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }

            rows.Add(row);
        }

        return rows;
    }
}
