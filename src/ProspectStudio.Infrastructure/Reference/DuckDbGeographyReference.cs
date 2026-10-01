using DuckDB.NET.Data;
using ProspectStudio.Core.Geography;

namespace ProspectStudio.Infrastructure.Reference;

/// <summary>
/// Reads the prepared reference data: counties out of <c>counties.parquet</c> through DuckDB with the
/// <c>spatial</c> extension, and the CBSA and ZCTA tables out of their CSVs (technical-design §6.1-6.2).
/// </summary>
/// <remarks>
/// <para>
/// Each table is cached against its file's size and last-write time, so repeated lookups do not re-read
/// 47,000 ZCTA rows and a fresh <c>prepare_data</c> is still picked up.
/// </para>
/// <para>
/// The <c>Task.Run(…, cancellationToken)</c> calls below keep a query from <em>starting</em> after
/// cancellation; they cannot stop one that is already running, because DuckDB.NET's synchronous reader
/// takes no token. Acceptable for the POC: these queries read a few megabytes of local Parquet and
/// finish in milliseconds. A long-running spatial query would need DuckDB's own interrupt instead.
/// </para>
/// </remarks>
public sealed class DuckDbGeographyReference : IGeographyReference, IDisposable
{
    private readonly ReferenceDataFiles _files;
    private readonly FileCache<IReadOnlyList<CountyRecord>> _counties;
    private readonly FileCache<IReadOnlyList<CbsaCountyRecord>> _cbsa;
    private readonly FileCache<IReadOnlyList<ZctaCountyRecord>> _zcta;

    public DuckDbGeographyReference(ReferenceDataFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);

        _files = files;
        _counties = new FileCache<IReadOnlyList<CountyRecord>>(files.CountiesParquet, ReadCountiesAsync);
        _cbsa = new FileCache<IReadOnlyList<CbsaCountyRecord>>(files.CbsaCsv, ReadCbsaAsync);
        _zcta = new FileCache<IReadOnlyList<ZctaCountyRecord>>(files.ZctaCsv, ReadZctaAsync);
    }

    public bool IsReady => _files.IsComplete;

    public Task<IReadOnlyList<CountyRecord>> GetCountiesAsync(CancellationToken cancellationToken) =>
        _counties.GetAsync(cancellationToken);

    public Task<IReadOnlyList<CbsaCountyRecord>> GetCbsaCountiesAsync(CancellationToken cancellationToken) =>
        _cbsa.GetAsync(cancellationToken);

    public Task<IReadOnlyList<ZctaCountyRecord>> GetZctaCountiesAsync(CancellationToken cancellationToken) =>
        _zcta.GetAsync(cancellationToken);

    public Task<GeoBounds?> GetBoundsAsync(
        IReadOnlyCollection<string> countyFips,
        CancellationToken cancellationToken)
    {
        if (countyFips.Count == 0)
        {
            return Task.FromResult<GeoBounds?>(null);
        }

        return Task.Run(
            () => Spatial<GeoBounds?>(
                "SELECT min(ST_XMin(geometry)), min(ST_YMin(geometry)), "
                + "max(ST_XMax(geometry)), max(ST_YMax(geometry)) "
                + $"FROM read_parquet('{DuckDbSpatial.PathLiteral(_files.CountiesParquet)}') "
                + $"WHERE GEOID IN ({DuckDbSpatial.StringList(countyFips)})",
                reader => !reader.Read() || reader.IsDBNull(0)
                    ? null
                    : new GeoBounds(
                        reader.GetDouble(0),
                        reader.GetDouble(1),
                        reader.GetDouble(2),
                        reader.GetDouble(3))),
            cancellationToken);
    }

    public Task<IReadOnlyList<string>> FindCountiesIntersectingAsync(
        IReadOnlyList<GeoPoint> boundary,
        CancellationToken cancellationToken) => Task.Run(
        () => Spatial<IReadOnlyList<string>>(
            $"SELECT GEOID FROM read_parquet('{DuckDbSpatial.PathLiteral(_files.CountiesParquet)}') "
            + $"WHERE ST_Intersects(geometry, ST_GeomFromText('{DuckDbSpatial.PolygonWkt(boundary)}')) "
            + "ORDER BY GEOID",
            reader =>
            {
                var fips = new List<string>();
                while (reader.Read())
                {
                    fips.Add(reader.GetString(0));
                }

                return fips;
            }),
        cancellationToken);

    public void Dispose()
    {
        _counties.Dispose();
        _cbsa.Dispose();
        _zcta.Dispose();
    }

    private Task<IReadOnlyList<CountyRecord>> ReadCountiesAsync(CancellationToken cancellationToken) =>
        Task.Run(
            () => Spatial<IReadOnlyList<CountyRecord>>(
                "SELECT GEOID, NAME, STATEFP "
                + $"FROM read_parquet('{DuckDbSpatial.PathLiteral(_files.CountiesParquet)}') ORDER BY GEOID",
                reader =>
                {
                    var counties = new List<CountyRecord>();
                    while (reader.Read())
                    {
                        counties.Add(new CountyRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                    }

                    return counties;
                }),
            cancellationToken);

    /// <summary>
    /// Runs one spatial query. A missing <c>spatial</c> extension is a prerequisite the user can install,
    /// so it becomes <c>NOT_READY</c> with its own hint rather than an unexplained internal failure.
    /// </summary>
    private static T Spatial<T>(string sql, Func<DuckDBDataReader, T> read)
    {
        try
        {
            using var duckdb = DuckDbSpatial.Open();
            using var command = duckdb.CreateCommand();
            command.CommandText = sql;

            using var reader = command.ExecuteReader();
            return read(reader);
        }
        catch (DuckDbExtensionException exception)
        {
            throw new GeographyNotReadyException(exception.Message, DuckDbSpatial.ExtensionHint);
        }
    }

    private async Task<IReadOnlyList<CbsaCountyRecord>> ReadCbsaAsync(CancellationToken cancellationToken)
    {
        var rows = new List<CbsaCountyRecord>();

        await foreach (var fields in ReadCsvAsync(_files.CbsaCsv, cancellationToken).ConfigureAwait(false))
        {
            if (fields.Length < 7)
            {
                continue;
            }

            rows.Add(new CbsaCountyRecord(
                fields[0],
                fields[1],
                fields[2],
                fields[3],
                fields[4],
                fields[5],
                fields[6]));
        }

        return rows;
    }

    private async Task<IReadOnlyList<ZctaCountyRecord>> ReadZctaAsync(CancellationToken cancellationToken)
    {
        var rows = new List<ZctaCountyRecord>();

        await foreach (var fields in ReadCsvAsync(_files.ZctaCsv, cancellationToken).ConfigureAwait(false))
        {
            if (fields.Length >= 2)
            {
                rows.Add(new ZctaCountyRecord(fields[0], fields[1]));
            }
        }

        return rows;
    }

    /// <summary>Every data row of a reference CSV, header skipped.</summary>
    private static async IAsyncEnumerable<string[]> ReadCsvAsync(
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(path);
        _ = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length > 0)
            {
                yield return DelimitedText.Split(line, ',');
            }
        }
    }

    /// <summary>
    /// A table held in memory for as long as its file is unchanged. The stamp is the file's length and
    /// last-write time, so a <c>prepare_data</c> run invalidates it without any notification.
    /// </summary>
    private sealed class FileCache<T>(string path, Func<CancellationToken, Task<T>> load) : IDisposable
        where T : class
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private (DateTime WriteUtc, long Length)? _stamp;
        private T? _value;

        public async Task<T> GetAsync(CancellationToken cancellationToken)
        {
            if (Current() is { } stamp && _stamp == stamp && _value is { } cached)
            {
                return cached;
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var latest = Current();
                if (latest is not null && _stamp == latest && _value is { } raced)
                {
                    return raced;
                }

                var loaded = await load(cancellationToken).ConfigureAwait(false);
                _value = loaded;
                _stamp = latest;
                return loaded;
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose() => _gate.Dispose();

        private (DateTime WriteUtc, long Length)? Current()
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        }
    }
}
