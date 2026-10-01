using System.Globalization;
using DuckDB.NET.Data;
using ProspectStudio.Core.Geography;

namespace ProspectStudio.Infrastructure.Reference;

/// <summary>
/// DuckDB could not load the extension a query needs, which is a setup problem rather than a bug.
/// </summary>
public sealed class DuckDbExtensionException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Opening DuckDB with the <c>spatial</c> extension, and the few SQL fragments the reference pipeline
/// and geography resolution share.
/// </summary>
public static class DuckDbSpatial
{
    /// <summary>What to tell the user when <c>spatial</c> is not on disk and may not be fetched.</summary>
    public const string ExtensionHint =
        "DuckDB's 'spatial' extension is not in the local extension cache "
        + "(%USERPROFILE%\\.duckdb\\extensions). Run 'ProspectStudio.Mcp doctor' once with network access "
        + "to install it.";

    /// <summary>
    /// An in-memory DuckDB connection with <c>spatial</c> loaded.
    /// </summary>
    /// <param name="allowInstall">
    /// True only on a path the user asked for and that may use the network (<c>doctor</c>,
    /// <c>prepare_data</c>). False turns a missing extension into an error instead of a silent download,
    /// which is what keeps a unit test off the network.
    /// </param>
    /// <remarks>
    /// <c>LOAD spatial</c> is required on <strong>every</strong> new connection, not just at install
    /// time: a fresh connection can read a geometry column and then still fail on <c>ST_Within</c>.
    /// </remarks>
    public static DuckDBConnection Open(bool allowInstall = false)
    {
        var connection = new DuckDBConnection("Data Source=:memory:");

        try
        {
            connection.Open();

            if (allowInstall)
            {
                Execute(connection, "INSTALL spatial");
            }
            else
            {
                Execute(connection, "SET autoinstall_known_extensions = false");
            }

            Execute(connection, "LOAD spatial");
            return connection;
        }
        catch (Exception exception) when (exception is DuckDBException or InvalidOperationException)
        {
            connection.Dispose();
            throw new DuckDbExtensionException($"{exception.Message} {ExtensionHint}", exception);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Installs the extensions the POC needs, so the cache is primed for later offline runs.</summary>
    public static void InstallExtensions(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, $"INSTALL {Identifier(extension)}");
        Execute(connection, $"LOAD {Identifier(extension)}");
    }

    public static void Execute(DuckDBConnection connection, string sql)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static object? Scalar(DuckDBConnection connection, string sql)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    /// <summary>
    /// A path as DuckDB wants to see it inside a single-quoted SQL literal: forward slashes, and any
    /// quote doubled.
    /// </summary>
    public static string PathLiteral(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal);
    }

    /// <summary>A closed ring as WKT, which <c>ST_GeomFromText</c> reads. WKT is <c>lon lat</c>.</summary>
    public static string PolygonWkt(IReadOnlyList<GeoPoint> boundary)
    {
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentOutOfRangeException.ThrowIfLessThan(boundary.Count, 4);

        var points = boundary.Select(point => string.Create(
            CultureInfo.InvariantCulture,
            $"{point.Lon:0.######} {point.Lat:0.######}"));

        return $"POLYGON(({string.Join(", ", points)}))";
    }

    /// <summary>A SQL list of string literals, for an <c>IN</c> over GEOIDs.</summary>
    public static string StringList(IEnumerable<string> values) =>
        string.Join(", ", values.Select(value => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'"));

    private static string Identifier(string name) =>
        name.All(char.IsAsciiLetterLower)
            ? name
            : throw new ArgumentOutOfRangeException(nameof(name), name, "Not a DuckDB extension name.");
}
