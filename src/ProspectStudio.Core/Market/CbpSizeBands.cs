using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProspectStudio.Core.Market;

/// <summary>
/// The <c>EMPSZES</c> band rules of technical-design §4 / implementation-plan C3. Nothing here may
/// hard-code a band list: the set varies by query (sixteen Texas counties publish <c>263</c>, the state
/// level publishes none) and the 2023 metadata has no value list for <c>EMPSZES</c>, so the bands and
/// their bounds are whatever a given response's <c>EMPSZES_LABEL</c> column says.
/// </summary>
/// <remarks>
/// The nine standard bands (<c>210</c>, <c>220</c>, <c>230</c>, <c>241</c>, <c>242</c>, <c>251</c>,
/// <c>252</c>, <c>254</c>, <c>260</c>) partition the total exactly. A response may also carry detail
/// bands nested inside <c>260</c> — in Harris County / NAICS 00, <c>262</c> + <c>263</c> + <c>271</c> +
/// <c>273</c> = <c>260</c> — so a band whose range sits inside another's is dropped before anything is
/// summed. Including <c>263</c> alongside <c>260</c> silently inflates Houston 4931's 20+ count from 152
/// to 155.
/// </remarks>
public static partial class CbpSizeBands
{
    /// <summary>
    /// "All establishments". It equals the sum of the nine standard bands exactly, so adding it to them
    /// doubles the count — and it has no lower bound, so it never satisfies a threshold.
    /// </summary>
    public const string AllEstablishmentsCode = "001";

    /// <summary>
    /// Reads a band's range out of its <c>EMPSZES_LABEL</c>: "Establishments with 20 to 49 employees" is
    /// 20–49, "with less than 5 employees" is 0–4, "with 1,000 employees or more" is 1,000 upwards.
    /// Numbers carry thousands separators.
    /// </summary>
    public static CbpSizeBand Parse(string code, string label)
    {
        ArgumentNullException.ThrowIfNull(code);

        var text = label ?? string.Empty;
        if (code == AllEstablishmentsCode)
        {
            return new CbpSizeBand(code, text, null, null);
        }

        var (lower, upper) = Bounds(text);
        return new CbpSizeBand(code, text, lower, upper);
    }

    /// <summary>
    /// Every band whose lower bound is at or above <paramref name="minEmployees"/>, from the bands this
    /// response actually contains, after dropping the ones nested inside another. A threshold that falls
    /// inside a band rounds up to the next edge, and <see cref="AllEstablishmentsCode"/> is never
    /// selected.
    /// </summary>
    public static SizeBandSelection Select(IEnumerable<CbpSizeBand> bands, int minEmployees)
    {
        ArgumentNullException.ThrowIfNull(bands);

        var selected = Partition(bands).Where(band => band.MinEmployees >= minEmployees).ToList();

        return new SizeBandSelection(
            minEmployees,
            selected.Count == 0 ? minEmployees : selected[0].MinEmployees!.Value,
            [.. selected.Select(band => band.Code)]);
    }

    /// <summary>
    /// The bands that partition the total: each one deduplicated, ordered by lower bound, with every band
    /// whose range lies inside another's left out. Those are detail bands that subdivide a broader one, so
    /// counting both reports the same establishments twice.
    /// </summary>
    public static IReadOnlyList<CbpSizeBand> Partition(IEnumerable<CbpSizeBand> bands)
    {
        ArgumentNullException.ThrowIfNull(bands);

        var comparable = bands
            .Where(band => !band.IsAllEstablishments && band.MinEmployees is not null)
            .GroupBy(band => band.Code, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(band => band.MinEmployees!.Value)
            .ThenBy(band => band.Code, StringComparer.Ordinal)
            .ToList();

        return [.. comparable.Where(band => !comparable.Any(other => Contains(other, band)))];
    }

    /// <summary>
    /// True when <paramref name="outer"/>'s range strictly encloses <paramref name="inner"/>'s. A missing
    /// upper bound is infinity, not "the top band": <c>260</c> (1,000+) encloses <c>273</c> (5,000+),
    /// while <c>273</c> encloses nothing.
    /// </summary>
    private static bool Contains(CbpSizeBand outer, CbpSizeBand inner)
    {
        var outerLower = outer.MinEmployees!.Value;
        var outerUpper = outer.MaxEmployees ?? int.MaxValue;
        var innerLower = inner.MinEmployees!.Value;
        var innerUpper = inner.MaxEmployees ?? int.MaxValue;

        return outerLower <= innerLower
            && outerUpper >= innerUpper
            && (outerLower < innerLower || outerUpper > innerUpper);
    }

    private static (int? Lower, int? Upper) Bounds(string label)
    {
        var numbers = Numbers().Matches(label)
            .Select(match => int.Parse(
                match.Value.Replace(",", string.Empty, StringComparison.Ordinal),
                CultureInfo.InvariantCulture))
            .ToList();

        if (numbers.Count == 0)
        {
            return (null, null);
        }

        // "Establishments with less than 5 employees" is the bottom band: 0 to 4, not 5 upwards.
        if (label.Contains("less than", StringComparison.OrdinalIgnoreCase))
        {
            return (0, numbers[0] - 1);
        }

        if (numbers.Count >= 2)
        {
            return (numbers[0], numbers[1]);
        }

        return label.Contains("more", StringComparison.OrdinalIgnoreCase)
            ? (numbers[0], null)
            : (numbers[0], numbers[0]);
    }

    [GeneratedRegex(@"\d[\d,]*")]
    private static partial Regex Numbers();
}

/// <summary>
/// Parses a Census data API body: an array of arrays whose first element is the header row, every value
/// a string, with <c>state</c> and <c>county</c> as the trailing columns.
/// </summary>
public static class CbpTable
{
    private const string EstablishmentsColumn = "ESTAB";
    private const string SizeBandColumn = "EMPSZES";
    private const string SizeBandLabelColumn = "EMPSZES_LABEL";
    private const string NaicsColumnPrefix = "NAICS";
    private const string StateColumn = "state";
    private const string CountyColumn = "county";

    /// <summary>
    /// The establishment rows of <paramref name="json"/>.
    /// </summary>
    /// <remarks>
    /// Columns are located by name once and then read by index, because a variable named in <c>get=</c>
    /// and filtered on appears <strong>twice</strong> in the header. The NAICS column is found by its
    /// <c>NAICS</c> prefix rather than by vintage (<c>NAICS2017</c> in 2023, something else later), and
    /// columns the caller asked for but the service never reads — <c>EMP</c>, <c>ESTAB_F</c>,
    /// <c>EMP_F</c> — may be JSON <c>null</c> rather than strings.
    /// </remarks>
    public static IReadOnlyList<CbpEstablishmentRow> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = ParseDocument(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            throw new MarketExternalException(
                "The Census API answered with something that is not a CBP table: the body should be an "
                + "array of arrays whose first element is the header row.");
        }

        var header = Header(root[0]);
        var establishments = Column(header, name => name == EstablishmentsColumn, EstablishmentsColumn);
        var sizeBand = Column(header, name => name == SizeBandColumn, SizeBandColumn);
        var sizeBandLabel = Column(header, name => name == SizeBandLabelColumn, SizeBandLabelColumn);
        var naics = Column(
            header,
            name => name.StartsWith(NaicsColumnPrefix, StringComparison.Ordinal),
            $"{NaicsColumnPrefix}<vintage>");
        var state = Column(header, name => name == StateColumn, StateColumn);
        var county = Column(header, name => name == CountyColumn, CountyColumn);

        List<CbpEstablishmentRow> rows = [];
        for (var index = 1; index < root.GetArrayLength(); index++)
        {
            var row = root[index];

            rows.Add(new CbpEstablishmentRow(
                Text(row, naics),
                Text(row, state) + Text(row, county),
                Text(row, sizeBand),
                Text(row, sizeBandLabel),
                Establishments(Text(row, establishments))));
        }

        return rows;
    }

    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new MarketExternalException(
                $"The Census API answered with something that is not JSON: {exception.Message}");
        }
    }

    private static List<string> Header(JsonElement row) =>
        [.. row.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty)];

    /// <summary>
    /// The <strong>first</strong> matching column. A variable both named in <c>get=</c> and filtered on
    /// is repeated, so a map built from the last occurrence reads a different column than it names.
    /// </summary>
    private static int Column(List<string> header, Func<string, bool> matches, string wanted)
    {
        var index = header.FindIndex(name => matches(name));

        return index >= 0
            ? index
            : throw new MarketExternalException(
                $"The CBP response has no '{wanted}' column. Header: {string.Join(", ", header)}.");
    }

    private static string Text(JsonElement row, int index)
    {
        if (index >= row.GetArrayLength())
        {
            throw new MarketExternalException(
                $"A CBP row has {row.GetArrayLength()} values but the header names at least {index + 1}.");
        }

        var value = row[index];
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    }

    private static int Establishments(string raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new MarketExternalException(
                $"ESTAB came back as '{raw}', which is not a count. Census suppresses by leaving rows out, "
                + "so a blank establishment count means the response shape changed.");
}

/// <summary>
/// The CBP NAICS table is hierarchical: a parent row already contains its children, so summing
/// <c>4931</c> together with <c>49311</c> counts the warehouses twice.
/// </summary>
public static class NaicsCodeSet
{
    /// <summary>
    /// <paramref name="codes"/> with every code dropped that is a prefix-descendant of another code in
    /// the same list, and with duplicates removed. Codes on different branches are disjoint and both
    /// stay. Order follows the first appearance of each kept code.
    /// </summary>
    public static IReadOnlyList<string> DropDescendants(IReadOnlyList<string> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);

        List<string> distinct = [];
        foreach (var code in codes)
        {
            var trimmed = code?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && !distinct.Contains(trimmed, StringComparer.Ordinal))
            {
                distinct.Add(trimmed);
            }
        }

        return [.. distinct.Where(code => AncestorOf(code, distinct) is null)];
    }

    /// <summary>
    /// The code in <paramref name="codes"/> that already contains <paramref name="code"/>, or null when
    /// it is outermost. The shortest ancestor, so the note names the code whose number is being reported.
    /// </summary>
    public static string? AncestorOf(string code, IReadOnlyList<string> codes)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(codes);

        return codes
            .Where(other => other.Length < code.Length && code.StartsWith(other, StringComparison.Ordinal))
            .OrderBy(other => other.Length)
            .FirstOrDefault();
    }
}
