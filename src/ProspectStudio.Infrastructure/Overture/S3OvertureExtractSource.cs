using System.Globalization;
using ProspectStudio.Core.Candidates;
using ProspectStudio.Core.Geography;
using ProspectStudio.Core.Reference;
using ProspectStudio.Infrastructure.Reference;

namespace ProspectStudio.Infrastructure.Overture;

/// <summary>
/// Overture Places from anonymous S3 in <c>us-west-2</c>, via DuckDB's <c>httpfs</c> and
/// <c>spatial</c> extensions (technical-design §6.1). No credentials are needed.
/// </summary>
/// <remarks>
/// Two things about this query are measured rather than guessed. The <c>bbox</c> pre-filter is what
/// keeps a state extract to about 70 seconds instead of a full-planet scan. And the state is narrowed
/// with <c>addresses[1].region</c> rather than by clipping to the state polygon: §6.1 timed the
/// polygon clip at 149 s against 1 s for the region filter, for a rounding difference in the row
/// count, and the county join at query time (§6.3) does the precise spatial work anyway.
/// </remarks>
public sealed class S3OvertureExtractSource(
    IGeographyReference geography,
    HttpClient http) : IOvertureExtractSource
{
    private const string Bucket = "s3://overturemaps-us-west-2/release";

    public async Task<string?> FindLatestReleaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            var catalog = await http
                .GetStringAsync(OvertureReleaseCatalog.CatalogUrl, cancellationToken)
                .ConfigureAwait(false);

            return OvertureReleaseCatalog.ReadLatest(catalog);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Release discovery is advisory: the configured PS_OVERTURE_RELEASE is what the extract is
            // written under either way, so a catalog that will not answer must not fail the step.
            return null;
        }
    }

    public async Task<int> ExtractAsync(
        string state,
        string release,
        string destination,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var abbreviation = UsStates.Find(state)?.Abbreviation
            ?? throw new ArgumentOutOfRangeException(nameof(state), state, "Not a US state.");

        var bounds = await BoundsAsync(abbreviation, cancellationToken).ConfigureAwait(false);

        return await Task.Run(
            () => Extract(abbreviation, release, destination, bounds),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<GeoBounds> BoundsAsync(string abbreviation, CancellationToken cancellationToken)
    {
        var state = UsStates.Find(abbreviation)!;
        var counties = await geography.GetCountiesAsync(cancellationToken).ConfigureAwait(false);

        var fips = counties
            .Where(county => county.StateFips == state.Fips)
            .Select(county => county.Fips)
            .ToList();

        return await geography.GetBoundsAsync(fips, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No county geometry for {abbreviation}, so the Overture bbox pre-filter cannot be built. "
                + "Run the counties step of prepare_data first.");
    }

    private static int Extract(string abbreviation, string release, string destination, GeoBounds bounds)
    {
        var source = $"{Bucket}/{release}/theme=places/type=place/*";
        var output = DuckDbSpatial.PathLiteral(destination);

        using var db = DuckDbSpatial.Open(allowInstall: true);
        DuckDbSpatial.Execute(db, "INSTALL httpfs");
        DuckDbSpatial.Execute(db, "LOAD httpfs");
        DuckDbSpatial.Execute(db, "SET s3_region='us-west-2'");

        var copy = string.Create(
            CultureInfo.InvariantCulture,
            $"""
             COPY (
               SELECT id, names.primary AS name, basic_category, taxonomy, confidence,
                      websites, phones, addresses, geometry, bbox
               FROM read_parquet('{source}', hive_partitioning = 1)
               WHERE bbox.xmin BETWEEN {bounds.MinLon} AND {bounds.MaxLon}
                 AND bbox.ymin BETWEEN {bounds.MinLat} AND {bounds.MaxLat}
                 AND len(addresses) > 0
                 AND addresses[1].region = '{abbreviation}'
             ) TO '{output}' (FORMAT PARQUET)
             """);

        DuckDbSpatial.Execute(db, copy);

        return Convert.ToInt32(
            DuckDbSpatial.Scalar(db, $"SELECT count(*) FROM read_parquet('{output}')"),
            CultureInfo.InvariantCulture);
    }
}
