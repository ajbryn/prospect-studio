using System.Globalization;
using System.Text;

namespace ProspectStudio.Tests.Shared;

/// <summary>One row of <c>poc/fixtures/dealers.csv</c>: a dealer and its single branch.</summary>
internal sealed record SampleDealer(
    string DealerId,
    string DealerName,
    string Website,
    string AlertEmail,
    string BranchId,
    string BranchName,
    string Address,
    string City,
    string State,
    string Zip,
    double Lat,
    double Lon,
    string Phone,
    string TrackingPhone);

/// <summary>One row of <c>poc/fixtures/territories.csv</c> (technical-design §7.4).</summary>
/// <param name="Level"><c>zip</c> or <c>county</c>; nothing else is a territory level in §5.2.</param>
/// <param name="Code">A ZIP5 or a county FIPS, depending on <paramref name="Level"/>.</param>
internal sealed record SampleTerritory(
    string DealerId,
    string BranchId,
    string Level,
    string Code,
    int Priority);

/// <summary>
/// One row of <c>poc/fixtures/suppression.csv</c>. <paramref name="Domain"/> is empty on the
/// <c>dnc</c> row, which is the only row that can exercise §7.3's name path.
/// </summary>
internal sealed record SampleSuppression(
    int Row,
    string CompanyName,
    string Domain,
    string Address,
    string City,
    string State,
    string Zip,
    string Reason);

/// <summary>
/// The three business lists chunk C5 imports, read from the spec pack rather than copied into tests.
/// <c>DealerFixtureIntegrityTests</c> proves the facts the C5 tests rely on - that every territory
/// names a known dealer, that San Jacinto is the one Houston county with no coverage, and that every
/// suppression row has a places row a profile-driven search actually returns.
/// </summary>
/// <remarks>
/// The same lesson as <see cref="SampleProfile"/>: a fixture that is self-consistent but has no
/// subject silently guarantees a green test. Three of C4's fixture rows sat in an excluded category so
/// C5's suppression would have had nothing to remove, and C5 added five more rows because
/// <c>assignment=gap</c>, the <c>dnc</c> reason and §7.3's name and fuzzy paths had no subject at all.
/// </remarks>
internal static class SampleDealers
{
    /// <summary>The ten counties of CBSA 26420, so a coverage test can name the uncovered one.</summary>
    public static IReadOnlyList<string> HoustonCbsaCounties => SamplePlaces.HoustonCbsaCounties;

    /// <summary>
    /// San Jacinto County: a genuine Houston CBSA county that <c>territories.csv</c> deliberately does
    /// not cover, so a lead there is the only one that can reach <c>assignment=gap</c>.
    /// </summary>
    public const string UncoveredCountyFips = "48407";

    /// <summary>The fixture row in <see cref="UncoveredCountyFips"/>, added in C5 for the gap case.</summary>
    public const string CoverageGapPlaceId = "fx_0116";

    /// <summary>The <c>dnc</c> row's places counterpart: §7.3's exact <c>name_norm</c> + ZIP path.</summary>
    public const string NameMatchPlaceId = "fx_0117";

    /// <summary>The third dealer's places counterpart, so all three dealers can be suppressed.</summary>
    public const string ThirdDealerPlaceId = "fx_0118";

    /// <summary>The fuzzy subject: ≥ 0.92 against a suppression row at the same ZIP, but not equal.</summary>
    public const string FuzzyMatchPlaceId = "fx_0119";

    /// <summary>The negative control: just under 0.92 at the same ZIP, so it must not be suppressed.</summary>
    public const string FuzzyControlPlaceId = "fx_0120";

    public static IReadOnlyList<SampleDealer> Dealers { get; } = ReadDealers();

    public static IReadOnlyList<SampleTerritory> Territories { get; } = ReadTerritories();

    public static IReadOnlyList<SampleSuppression> Suppression { get; } = ReadSuppression();

    public static SampleDealer Dealer(string dealerId) =>
        Dealers.SingleOrDefault(dealer => dealer.DealerId == dealerId)
        ?? throw new KeyNotFoundException($"No dealer '{dealerId}' in {RepoFixtures.DealersCsv}.");

    public static SampleSuppression SuppressionFor(string companyName) =>
        Suppression.SingleOrDefault(row => row.CompanyName == companyName)
        ?? throw new KeyNotFoundException($"No suppression row '{companyName}' in {RepoFixtures.SuppressionCsv}.");

    /// <summary>Territory rows at <c>level=zip</c>, keyed by ZIP5.</summary>
    public static IReadOnlyList<SampleTerritory> ZipRules { get; } =
        [.. Territories.Where(row => row.Level == "zip")];

    /// <summary>Territory rows at <c>level=county</c>, keyed by county FIPS.</summary>
    public static IReadOnlyList<SampleTerritory> CountyRules { get; } =
        [.. Territories.Where(row => row.Level == "county")];

    /// <summary>The Houston CBSA counties no territory row covers.</summary>
    public static IReadOnlyList<string> UncoveredHoustonCounties { get; } =
    [
        .. HoustonCbsaCounties
            .Where(county => CountyRules.All(rule => rule.Code != county))
            .Order(StringComparer.Ordinal),
    ];

    private static IReadOnlyList<SampleDealer> ReadDealers() =>
    [
        .. Rows(RepoFixtures.DealersCsv).Select(row => new SampleDealer(
            row["dealer_id"],
            row["dealer_name"],
            row["website"],
            row["alert_email"],
            row["branch_id"],
            row["branch_name"],
            row["address"],
            row["city"],
            row["state"],
            row["zip"],
            Number(row["lat"]),
            Number(row["lon"]),
            row["phone"],
            row["tracking_phone"])),
    ];

    private static IReadOnlyList<SampleTerritory> ReadTerritories() =>
    [
        .. Rows(RepoFixtures.TerritoriesCsv).Select(row => new SampleTerritory(
            row["dealer_id"],
            row["branch_id"],
            row["level"],
            row["code"],
            int.Parse(row["priority"], CultureInfo.InvariantCulture))),
    ];

    private static IReadOnlyList<SampleSuppression> ReadSuppression() =>
    [
        .. Rows(RepoFixtures.SuppressionCsv).Select((row, index) => new SampleSuppression(
            // The spreadsheet row: the header is row 1, so the first data row is row 2. import_list
            // reports row numbers the same way, because that is the number the user sees in the file.
            index + 2,
            row["company_name"],
            row["domain"],
            row["address"],
            row["city"],
            row["state"],
            row["zip"],
            row["reason"])),
    ];

    private static double Number(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    private static List<Record> Rows(string path)
    {
        var lines = File.ReadAllLines(path);
        var header = Split(lines[0]);

        return [.. lines.Skip(1).Where(line => line.Trim().Length > 0).Select(line => new Record(header, Split(line)))];
    }

    /// <summary>
    /// Enough CSV for these three files: the only quoted fields would be addresses containing a comma,
    /// and none ever contains a quote of its own.
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

    private sealed class Record(string[] header, string[] fields)
    {
        public string this[string column]
        {
            get
            {
                var index = Array.IndexOf(header, column);
                return index < 0
                    ? throw new KeyNotFoundException(
                        $"No column '{column}'; the file has {string.Join(", ", header)}.")
                    : fields[index].Trim();
            }
        }
    }
}
