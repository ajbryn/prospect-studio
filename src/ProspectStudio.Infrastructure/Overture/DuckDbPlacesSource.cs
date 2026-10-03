using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using ProspectStudio.Core.Candidates;
using ProspectStudio.Core.Reference;
using ProspectStudio.Infrastructure.Reference;

namespace ProspectStudio.Infrastructure.Overture;

/// <summary>
/// Candidate search over the local Overture Parquet with DuckDB (technical-design §6.3). Two things
/// about that query are easy to get wrong and expensive to discover later:
/// <list type="number">
/// <item><c>LOAD spatial</c> is required on <strong>every</strong> new connection - a fresh connection
/// can read a geometry column and then still fail on <c>ST_Within</c>.</item>
/// <item>Overture geometry is <c>OGC:CRS84</c> and the Census counties Parquet is <c>EPSG:4269</c>, so
/// <c>ST_Within</c> across them raises a Binder error. Align the <em>county</em> side with
/// <c>ST_SetCRS(c.geometry, 'OGC:CRS84')</c>.</item>
/// </list>
/// </summary>
/// <remarks>
/// Struct and list columns come back through <c>to_json</c> rather than as native DuckDB values: the
/// shapes (<c>taxonomy</c>, <c>addresses[]</c>, <c>websites[]</c>) then round-trip exactly, and a URL
/// holding whatever character a separator would have used cannot corrupt a row.
/// </remarks>
public sealed class DuckDbPlacesSource(
    IOvertureDataInventory overture,
    ReferenceDataFiles reference) : IPlacesSource
{
    /// <summary>
    /// Category counts per extract, keyed by the file's identity rather than by the state alone: a
    /// <c>prepare_data --force</c> inside a running server replaces the Parquet, and a state-only key
    /// would go on serving the pre-force counts for the rest of the process's life.
    /// </summary>
    private readonly ConcurrentDictionary<ExtractKey, IReadOnlyList<OvertureCategoryCount>> _categories = new();

    public bool IsReady(string state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        return overture.HasState(state) && File.Exists(reference.CountiesParquet);
    }

    public Task<IReadOnlyList<OverturePlace>> FindAsync(
        string state,
        CandidateQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentNullException.ThrowIfNull(query);

        if (query.CountyFips.Count == 0 || (query.Categories.Count == 0 && query.Keywords.Count == 0))
        {
            return Task.FromResult<IReadOnlyList<OverturePlace>>([]);
        }

        return Task.Run(() => Find(state, query, cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<OvertureCategoryCount>> CountCategoriesAsync(
        string state,
        string? query,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        var key = ExtractKey.For(overture.PlacesParquet(state));
        if (!_categories.TryGetValue(key, out var all))
        {
            // A full scan of a 205 MB extract is not something a skill can call freely, so the whole
            // table is cached per extract and the query and limit are applied in memory.
            all = await Task.Run(() => CountCategories(state, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            _categories[key] = all;
        }

        var matches = string.IsNullOrWhiteSpace(query)
            ? all
            : [.. all.Where(row => Matches(row, query.Trim()))];

        return limit <= 0 ? matches : [.. matches.Take(limit)];
    }

    private static bool Matches(OvertureCategoryCount row, string query) =>
        row.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
        || row.Path.Any(segment => segment.Contains(query, StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<OverturePlace> Find(
        string state,
        CandidateQuery query,
        CancellationToken cancellationToken)
    {
        using var db = DuckDbSpatial.Open();

        using var command = db.CreateCommand();
        command.CommandText = FindSql(state, query);

        // Task.Run's token only stops the work from starting; a scan already under way has to be told
        // to stop, and DuckDB's Cancel aborts it with OperationCanceledException.
        using var cancellation = Cancelling(command, cancellationToken);
        using var reader = command.ExecuteReader();

        var places = new List<OverturePlace>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            places.Add(new OverturePlace(
                reader.GetString(0),
                Text(reader, 1),
                Text(reader, 2),
                new PlaceTaxonomy(Text(reader, 3), Strings(reader, 4), Strings(reader, 5)),
                reader.GetDouble(6),
                Strings(reader, 7),
                Strings(reader, 8),
                Addresses(reader, 9),
                reader.GetDouble(11),
                reader.GetDouble(10),
                reader.GetString(12)));
        }

        return places;
    }

    private string FindSql(string state, CandidateQuery query)
    {
        var places = DuckDbSpatial.PathLiteral(overture.PlacesParquet(state));
        var counties = DuckDbSpatial.PathLiteral(reference.CountiesParquet);

        var filters = new List<string>
        {
            $"c.GEOID IN ({DuckDbSpatial.StringList(query.CountyFips)})",
            string.Create(CultureInfo.InvariantCulture, $"p.confidence >= {query.MinConfidence}"),
            Any(query.Categories, query.Keywords),
        };

        if (query.ExcludedCategories.Count > 0)
        {
            filters.Add($"NOT ({CategoryMatch(query.ExcludedCategories)})");
        }

        if (query.ExcludedKeywords.Count > 0)
        {
            filters.Add($"NOT ({KeywordMatch(query.ExcludedKeywords)})");
        }

        var limit = query.Limit > 0
            ? string.Create(CultureInfo.InvariantCulture, $"LIMIT {query.Limit}")
            : string.Empty;

        return $"""
            SELECT p.id,
                   p.name,
                   p.basic_category,
                   p.taxonomy.primary,
                   to_json(p.taxonomy.hierarchy),
                   to_json(p.taxonomy.alternates),
                   p.confidence,
                   to_json(p.websites),
                   to_json(p.phones),
                   to_json(p.addresses),
                   ST_X(p.geometry),
                   ST_Y(p.geometry),
                   c.GEOID
            FROM read_parquet('{places}') p
            JOIN (SELECT GEOID, ST_SetCRS(geometry, 'OGC:CRS84') AS geometry
                  FROM read_parquet('{counties}')) c
              ON ST_Within(p.geometry, c.geometry)
            WHERE {string.Join($"{Environment.NewLine}  AND ", filters)}
            ORDER BY p.id
            {limit}
            """;
    }

    private IReadOnlyList<OvertureCategoryCount> CountCategories(string state, CancellationToken cancellationToken)
    {
        using var db = DuckDbSpatial.Open();
        var places = DuckDbSpatial.PathLiteral(overture.PlacesParquet(state));

        using var command = db.CreateCommand();
        command.CommandText = $"""
            SELECT p.taxonomy.primary AS category,
                   any_value(to_json(p.taxonomy.hierarchy)) AS path,
                   count(*) AS places
            FROM read_parquet('{places}') p
            WHERE p.taxonomy.primary IS NOT NULL
            GROUP BY category
            ORDER BY places DESC, category
            """;

        using var cancellation = Cancelling(command, cancellationToken);
        using var reader = command.ExecuteReader();

        var counts = new List<OvertureCategoryCount>();
        while (reader.Read())
        {
            counts.Add(new OvertureCategoryCount(
                reader.GetString(0),
                Strings(reader, 1),
                (int)reader.GetInt64(2)));
        }

        return counts;
    }

    /// <summary>§6.3's three-way OR: the hierarchy, the leaf, or the name.</summary>
    private static string Any(IReadOnlyList<string> categories, IReadOnlyList<string> keywords)
    {
        var tests = new List<string>(2);
        if (categories.Count > 0)
        {
            tests.Add(CategoryMatch(categories));
        }

        if (keywords.Count > 0)
        {
            tests.Add(KeywordMatch(keywords));
        }

        return $"({string.Join(" OR ", tests)})";
    }

    private static string CategoryMatch(IReadOnlyList<string> categories) =>
        $"list_has_any(p.taxonomy.hierarchy, [{DuckDbSpatial.StringList(categories)}]) "
        + $"OR p.taxonomy.primary IN ({DuckDbSpatial.StringList(categories)})";

    private static string KeywordMatch(IReadOnlyList<string> keywords)
    {
        var pattern = string.Join('|', keywords.Select(keyword => Regex.Escape(keyword.Trim().ToLowerInvariant())));
        return $"regexp_matches(lower(p.name), '{pattern.Replace("'", "''", StringComparison.Ordinal)}')";
    }

    /// <summary>
    /// Ties <paramref name="cancellationToken"/> to the running statement. Dispose the registration
    /// before the command, so a token cancelled later cannot touch a disposed command.
    /// </summary>
    private static CancellationTokenRegistration Cancelling(
        DuckDBCommand command,
        CancellationToken cancellationToken) =>
        cancellationToken.Register(static state => ((DuckDBCommand)state!).Cancel(), command);

    private static string? Text(DuckDBDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);

    private static IReadOnlyList<string> Strings(DuckDBDataReader reader, int index)
    {
        if (Text(reader, index) is not { } json)
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? [.. document.RootElement.EnumerateArray()
                .Select(value => value.GetString())
                .OfType<string>()]
            : [];
    }

    private static IReadOnlyList<PlaceAddress> Addresses(DuckDBDataReader reader, int index)
    {
        if (Text(reader, index) is not { } json)
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. document.RootElement.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.Object)
                .Select(element => new PlaceAddress(
                    Member(element, "freeform"),
                    Member(element, "locality"),
                    Member(element, "postcode"),
                    Member(element, "region"),
                    Member(element, "country"))),
        ];
    }

    private static string? Member(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Which extract a cached answer came from: the file's path, length and last-write time.
    /// Re-extracting a state changes the length or the timestamp, so the cache misses and rescans.
    /// </summary>
    /// <remarks>
    /// The path is part of the identity because length and last-write time alone do not distinguish
    /// two copies of the same extract - <c>File.Copy</c> carries the timestamp over, so every data
    /// folder seeded from one file agrees on both.
    /// </remarks>
    private readonly record struct ExtractKey(string Path, long Length, DateTime LastWriteUtc)
    {
        public static ExtractKey For(string path)
        {
            var file = new FileInfo(path);
            return file.Exists
                ? new ExtractKey(file.FullName, file.Length, file.LastWriteTimeUtc)
                : new ExtractKey(Path: System.IO.Path.GetFullPath(path), 0, default);
        }
    }
}
