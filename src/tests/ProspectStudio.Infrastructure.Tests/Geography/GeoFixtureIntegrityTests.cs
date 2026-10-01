using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Infrastructure.Tests.Geography;

/// <summary>
/// The committed geography excerpts still say what the geography tests assume. These are facts about
/// the real Census files, verified in C2; if a regeneration ever silently changes one, the failure
/// should point here rather than at <c>resolve_geography</c>.
/// </summary>
public class GeoFixtureIntegrityTests
{
    [Fact]
    public void The_CBSA_excerpt_has_the_documented_columns()
    {
        Header(RepoFixtures.CbsaExcerptCsv).ShouldBe(
            ["cbsa_code", "cbsa_title", "area_type", "county_geoid", "county_name", "state_name", "central_outlying"]);
    }

    [Fact]
    public void CBSA_26420_is_the_Houston_metro_with_ten_counties()
    {
        var houston = Rows(RepoFixtures.CbsaExcerptCsv).Where(row => row[0] == "26420").ToList();

        houston.Select(row => row[3]).Order(StringComparer.Ordinal).ShouldBe(
            ["48015", "48039", "48071", "48157", "48167", "48201", "48291", "48339", "48407", "48473"]);

        houston.ShouldAllBe(row => row[1].Contains("Houston", StringComparison.Ordinal));
    }

    [Fact]
    public void Jefferson_county_is_outside_the_Houston_metro_so_C4_can_prove_exclusion()
    {
        var jefferson = Rows(RepoFixtures.CbsaExcerptCsv).Where(row => row[3] == "48245").ToList();

        jefferson.ShouldNotBeEmpty("Jefferson County must be in the excerpt, just not in CBSA 26420.");
        jefferson.ShouldAllBe(row => row[0] != "26420");
    }

    [Fact]
    public void Four_different_Springfields_make_that_query_genuinely_ambiguous()
    {
        Rows(RepoFixtures.CbsaExcerptCsv)
            .Where(row => row[1].StartsWith("Springfield,", StringComparison.Ordinal))
            .Select(row => row[0])
            .Distinct()
            .Count()
            .ShouldBe(4);
    }

    [Fact]
    public void The_ZCTA_excerpt_has_the_documented_columns_and_no_empty_ZCTAs()
    {
        Header(RepoFixtures.ZctaCountyExcerptCsv).ShouldBe(["zcta5", "county_geoid"]);

        Rows(RepoFixtures.ZctaCountyExcerptCsv).ShouldAllBe(row => row[0].Length == 5 && row[1].Length == 5);
    }

    [Fact]
    public void ZIP_77494_spans_three_counties()
    {
        Rows(RepoFixtures.ZctaCountyExcerptCsv)
            .Where(row => row[0] == "77494")
            .Select(row => row[1])
            .Order(StringComparer.Ordinal)
            .ShouldBe(["48157", "48201", "48473"]);
    }

    [Fact]
    public void The_county_parquet_is_committed_and_small_enough_to_stay_committed()
    {
        var parquet = new FileInfo(RepoFixtures.CountiesHoustonParquet);

        parquet.Exists.ShouldBeTrue();
        parquet.Length.ShouldBeLessThan(
            256 * 1024,
            "geometry is simplified on purpose; a megabyte-sized fixture does not belong in git.");
    }

    private static string[] Header(string path) => Split(File.ReadLines(path).First());

    private static List<string[]> Rows(string path) => [.. File.ReadLines(path).Skip(1).Select(Split)];

    /// <summary>
    /// Enough CSV for these two files: the only quoted field is the CBSA title, which never contains a
    /// quote of its own.
    /// </summary>
    private static string[] Split(string line)
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
}
