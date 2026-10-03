using System.Text;

namespace ProspectStudio.Tests.Shared;

/// <summary>
/// One row of <c>poc/fixtures/sample-places.csv</c>, which is the source of truth the Parquet fixture
/// is built from. Tests read expectations from here rather than repeating 113 rows of data inline.
/// </summary>
/// <param name="CountyFips">
/// The county the point really falls in. <c>PlacesFixtureIntegrityTests</c> proves it against the
/// committed county geometry, so a test may rely on it.
/// </param>
internal sealed record SamplePlace(
    string Id,
    string Name,
    string TaxonomyPrimary,
    IReadOnlyList<string> TaxonomyHierarchy,
    string BasicCategory,
    double Confidence,
    IReadOnlyList<string> Websites,
    IReadOnlyList<string> Phones,
    string? Freeform,
    string Locality,
    string Region,
    string Postcode,
    string Country,
    string CountyFips,
    double Lat,
    double Lon,
    string FixtureNote);

/// <summary>
/// The shared places fixture, plus the few facts about Houston geography the candidate tests scope
/// themselves to. Everything here is checked by <c>PlacesFixtureIntegrityTests</c>.
/// </summary>
internal static class SamplePlaces
{
    /// <summary>The ten counties of CBSA 26420, verified against the real delineation file in C2.</summary>
    public static IReadOnlyList<string> HoustonCbsaCounties { get; } =
        ["48015", "48039", "48071", "48157", "48167", "48201", "48291", "48339", "48407", "48473"];

    /// <summary>Jefferson County (Beaumont), CBSA 13140 - deliberately outside the Houston scope.</summary>
    public const string JeffersonCountyFips = "48245";

    public static IReadOnlyList<SamplePlace> All { get; } = Read();

    /// <summary>One row by its fixture id, e.g. <c>fx_0001</c>.</summary>
    public static SamplePlace Row(string id) =>
        All.SingleOrDefault(place => place.Id == id)
        ?? throw new KeyNotFoundException($"No row '{id}' in {RepoFixtures.SamplePlacesCsv}.");

    /// <summary>The rows inside the Houston CBSA's ten counties.</summary>
    public static IReadOnlyList<SamplePlace> InHoustonCbsa { get; } =
        [.. All.Where(place => HoustonCbsaCounties.Contains(place.CountyFips))];

    /// <summary>The Beaumont rows a Houston scope must exclude.</summary>
    public static IReadOnlyList<SamplePlace> InJeffersonCounty { get; } =
        [.. All.Where(place => place.CountyFips == JeffersonCountyFips)];

    private static IReadOnlyList<SamplePlace> Read()
    {
        var lines = File.ReadAllLines(RepoFixtures.SamplePlacesCsv);
        var header = Split(lines[0]);
        var places = new List<SamplePlace>(lines.Length - 1);

        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var fields = Split(line);
            string Field(string name) => fields[Array.IndexOf(header, name)];

            places.Add(new SamplePlace(
                Field("id"),
                Field("name"),
                Field("taxonomy_primary"),
                Field("taxonomy_hierarchy").Split('|'),
                Field("basic_category"),
                double.Parse(Field("confidence"), System.Globalization.CultureInfo.InvariantCulture),
                List(Field("websites")),
                List(Field("phones")),
                Field("freeform") is { Length: > 0 } freeform ? freeform : null,
                Field("locality"),
                Field("region"),
                Field("postcode"),
                Field("country"),
                Field("county_fips"),
                double.Parse(Field("lat"), System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(Field("lon"), System.Globalization.CultureInfo.InvariantCulture),
                Field("fixture_note")));
        }

        return places;
    }

    private static IReadOnlyList<string> List(string value) =>
        value.Length == 0 ? [] : value.Split('|');

    /// <summary>
    /// Enough CSV for this file: the only quoted fields are names and notes containing a comma, and
    /// neither ever contains a quote of its own.
    /// </summary>
    private static string[] Split(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
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
