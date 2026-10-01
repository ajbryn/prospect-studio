using System.Globalization;
using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using ProspectStudio.Core.Reference;

namespace ProspectStudio.Infrastructure.Reference;

/// <summary>
/// The reference-data half of the setup pipeline (technical-design §6.1): take each source file from
/// <see cref="IReferenceFileSource"/>, parse it into <c>refdata\</c>, and record what it did in
/// <c>manifest.json</c>. A step whose output is already there is skipped unless <c>force</c> is set.
/// </summary>
/// <remarks>
/// The source comes through a seam so the parsing - the GDAL virtual path into a zip, an xlsx with its
/// header on row 3 and notes after the data, a pipe-delimited file with a BOM and empty-ZCTA rows - is
/// tested against committed trimmed sources rather than only against the live Census site.
/// </remarks>
public sealed class ReferenceDataPreparer
{
    /// <summary>Rows 1-2 of the CBSA delineation workbook are a title; the header is row 3.</summary>
    private const int CbsaHeaderRow = 3;

    /// <summary>
    /// Every output is written here first and then moved into place, because a step decides it has
    /// nothing to do by seeing a non-empty file: a run that dies mid-write would otherwise leave a
    /// truncated parquet or CSV that every later run skips as finished.
    /// </summary>
    private const string PartialSuffix = ".part";

    private const string CbsaCodeColumn = "CBSA Code";
    private const string CbsaTitleColumn = "CBSA Title";
    private const string CbsaAreaTypeColumn = "Metropolitan/Micropolitan Statistical Area";
    private const string CbsaCountyColumn = "County/County Equivalent";
    private const string CbsaStateNameColumn = "State Name";
    private const string CbsaStateFipsColumn = "FIPS State Code";
    private const string CbsaCountyFipsColumn = "FIPS County Code";
    private const string CbsaCentralOutlyingColumn = "Central/Outlying County";

    private const string ZctaColumn = "GEOID_ZCTA5_20";
    private const string ZctaCountyColumn = "GEOID_COUNTY_20";

    private readonly ReferenceDataFiles _files;
    private readonly IReferenceFileSource _fileSource;
    private readonly TimeProvider _time;

    public ReferenceDataPreparer(string referenceDataDirectory, IReferenceFileSource fileSource)
        : this(referenceDataDirectory, fileSource, TimeProvider.System)
    {
    }

    public ReferenceDataPreparer(
        string referenceDataDirectory,
        IReferenceFileSource fileSource,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceDataDirectory);
        ArgumentNullException.ThrowIfNull(fileSource);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _files = new ReferenceDataFiles(referenceDataDirectory);
        _fileSource = fileSource;
        _time = timeProvider;
    }

    public Task<ReferenceDataResult> PrepareAsync(bool force, CancellationToken cancellationToken) =>
        PrepareAsync(force, report: null, cancellationToken);

    public async Task<ReferenceDataResult> PrepareAsync(
        bool force,
        ReferenceStepReporter? report,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_files.Directory);

        var recorded = await ReferenceManifest.ReadAsync(_files.Manifest, cancellationToken).ConfigureAwait(false);
        var results = new List<ReferenceStepResult>(ReferenceSteps.All.Count);
        var wroteSomething = false;
        var done = 0;

        foreach (var step in ReferenceSteps.All)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReferenceStepResult result;

            // A step counts as done only when its output *and* its manifest entry are there. Skipping on
            // the file alone would rewrite the manifest without the source URL and vintage, losing the
            // provenance POC-2 asks for, permanently.
            if (!force && _files.IsStepComplete(step) && recorded.Find(step) is not null)
            {
                result = Skipped(step, recorded.Find(step));
            }
            else
            {
                var source = await _fileSource.GetAsync(step, cancellationToken).ConfigureAwait(false);
                var rows = await RunAsync(step, source, cancellationToken).ConfigureAwait(false);

                result = new ReferenceStepResult(
                    step,
                    ReferenceSteps.FileFor(step),
                    Skipped: false,
                    rows,
                    source.SourceUrl,
                    source.RetrievedAt);

                wroteSomething = true;
            }

            results.Add(result);
            done++;

            if (report is not null)
            {
                await report(result, (double)done / ReferenceSteps.All.Count, cancellationToken).ConfigureAwait(false);
            }
        }

        // A run that changed nothing must not rewrite the manifest either: that is what makes the whole
        // pipeline idempotent down to the file timestamps.
        if (wroteSomething || !File.Exists(_files.Manifest))
        {
            await ReferenceManifest
                .WriteAsync(_files.Manifest, _time.GetUtcNow(), results, cancellationToken)
                .ConfigureAwait(false);
        }

        return new ReferenceDataResult(results, _files.Manifest);
    }

    private static ReferenceStepResult Skipped(string step, ReferenceManifestStep? recorded) => new(
        step,
        ReferenceSteps.FileFor(step),
        Skipped: true,
        recorded?.Rows ?? 0,
        recorded?.SourceUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed : null,
        recorded?.RetrievedAt);

    private Task<int> RunAsync(string step, ReferenceFile source, CancellationToken cancellationToken) => step switch
    {
        ReferenceSteps.Counties => WriteCountiesAsync(source.Path, cancellationToken),
        ReferenceSteps.Cbsa => WriteCbsaAsync(source.Path, cancellationToken),
        ReferenceSteps.Zcta => WriteZctaAsync(source.Path, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Not a reference-data step."),
    };

    private Task<int> WriteCountiesAsync(string sourcePath, CancellationToken cancellationToken) => Task.Run(
        () =>
        {
            var partial = _files.CountiesParquet + PartialSuffix;
            var output = DuckDbSpatial.PathLiteral(partial);
            var dataset = DuckDbSpatial.PathLiteral(DatasetPath(sourcePath));
            int rows;

            // The connection is closed before the move, so DuckDB is not still holding the file.
            using (var duckdb = DuckDbSpatial.Open(allowInstall: true))
            {
                // The geometry column out of ST_Read is 'geom' (lowercase) while the attributes are
                // uppercase; it round-trips through Parquet as WKB and reads back as GEOMETRY('EPSG:4269').
                DuckDbSpatial.Execute(
                    duckdb,
                    $"COPY (SELECT GEOID, NAME, STATEFP, geom AS geometry FROM ST_Read('{dataset}')) "
                    + $"TO '{output}' (FORMAT PARQUET)");

                rows = Convert.ToInt32(
                    DuckDbSpatial.Scalar(duckdb, $"SELECT count(*) FROM read_parquet('{output}')"),
                    CultureInfo.InvariantCulture);
            }

            Publish(partial, _files.CountiesParquet);
            return rows;
        },
        cancellationToken);

    /// <summary>
    /// Moves a finished <c>.part</c> file over the real output, which is one atomic step.
    /// Retries briefly because on Windows an overwriting move fails while something else holds the
    /// destination open, and a concurrent reader (a <c>resolve_geography</c> call mid-run) would
    /// otherwise lose every byte of an already-completed download at the last moment.
    /// </summary>
    private static void Publish(string partial, string path)
    {
        const int attempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(partial, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(attempt * 150);
            }
            catch (IOException exception)
            {
                throw new IOException(
                    $"Could not replace '{path}' because something else has it open. "
                    + "Close anything reading the reference data and run the step again.",
                    exception);
            }
        }
    }

    /// <summary>
    /// ST_Read cannot open a zip directly, so a zipped shapefile is reached through GDAL's
    /// <c>/vsizip/</c> virtual filesystem - which needs forward slashes even on Windows.
    /// </summary>
    private static string DatasetPath(string sourcePath)
    {
        if (!sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return sourcePath;
        }

        using var archive = ZipFile.OpenRead(sourcePath);
        var shapefile = archive.Entries
            .Select(entry => entry.FullName)
            .FirstOrDefault(name => name.EndsWith(".shp", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"'{sourcePath}' holds no .shp entry.");

        return $"/vsizip/{sourcePath.Replace('\\', '/')}/{shapefile}";
    }

    private async Task<int> WriteCbsaAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var rows = await Task.Run(() => ReadCbsaRows(sourcePath), cancellationToken).ConfigureAwait(false);

        var lines = new List<string>(rows.Count + 1)
        {
            DelimitedText.Row(
                "cbsa_code",
                "cbsa_title",
                "area_type",
                "county_geoid",
                "county_name",
                "state_name",
                "central_outlying"),
        };

        lines.AddRange(rows.Select(row => DelimitedText.Row(
            row.CbsaCode,
            row.CbsaTitle,
            row.AreaType,
            row.CountyFips,
            row.CountyName,
            row.StateName,
            row.CentralOutlying)));

        await WriteLinesAsync(_files.CbsaCsv, lines, cancellationToken).ConfigureAwait(false);
        return rows.Count;
    }

    private static List<CbsaRow> ReadCbsaRows(string sourcePath)
    {
        using var workbook = new XLWorkbook(sourcePath);
        var sheet = workbook.Worksheet(1);

        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in sheet.Row(CbsaHeaderRow).CellsUsed())
        {
            columns[cell.GetString().Trim()] = cell.Address.ColumnNumber;
        }

        var rows = new List<CbsaRow>();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? CbsaHeaderRow;

        for (var number = CbsaHeaderRow + 1; number <= lastRow; number++)
        {
            var row = sheet.Row(number);
            var code = Text(row, columns, CbsaCodeColumn);
            var stateFips = Text(row, columns, CbsaStateFipsColumn);
            var countyFips = Text(row, columns, CbsaCountyFipsColumn);

            // Trailing rows are a blank and two notes, which carry no CBSA code or FIPS pair.
            if (code.Length == 0 || stateFips.Length == 0 || countyFips.Length == 0)
            {
                continue;
            }

            rows.Add(new CbsaRow(
                code,
                Text(row, columns, CbsaTitleColumn),
                Text(row, columns, CbsaAreaTypeColumn),
                // Separate string columns: '48' and '015' are concatenated, never added, and never
                // parsed as numbers - which would turn '015' into 15.
                stateFips.PadLeft(2, '0') + countyFips.PadLeft(3, '0'),
                Text(row, columns, CbsaCountyColumn),
                Text(row, columns, CbsaStateNameColumn),
                Text(row, columns, CbsaCentralOutlyingColumn)));
        }

        return rows;
    }

    private static string Text(IXLRow row, Dictionary<string, int> columns, string column) =>
        columns.TryGetValue(column, out var number) ? row.Cell(number).GetString().Trim() : string.Empty;

    private async Task<int> WriteZctaAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var lines = new List<string> { DelimitedText.Row("zcta5", "county_geoid") };
        var rows = 0;

        // The source is pipe-delimited with a UTF-8 BOM; rows for a county that contains no ZCTA have an
        // empty ZCTA and must be dropped, or every ZIP lookup matches the blank.
        using var reader = new StreamReader(sourcePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var header = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"'{sourcePath}' is empty.");

        var columns = DelimitedText.Split(header, '|');
        var zctaIndex = IndexOf(columns, ZctaColumn, sourcePath);
        var countyIndex = IndexOf(columns, ZctaCountyColumn, sourcePath);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var fields = DelimitedText.Split(line, '|');
            if (fields.Length <= Math.Max(zctaIndex, countyIndex))
            {
                continue;
            }

            var zcta = fields[zctaIndex].Trim();
            var county = fields[countyIndex].Trim();
            if (zcta.Length == 0 || county.Length == 0)
            {
                continue;
            }

            lines.Add(DelimitedText.Row(zcta.PadLeft(5, '0'), county.PadLeft(5, '0')));
            rows++;
        }

        await WriteLinesAsync(_files.ZctaCsv, lines, cancellationToken).ConfigureAwait(false);
        return rows;
    }

    private static async Task WriteLinesAsync(
        string path,
        IEnumerable<string> lines,
        CancellationToken cancellationToken)
    {
        var partial = path + PartialSuffix;
        await File.WriteAllLinesAsync(partial, lines, cancellationToken).ConfigureAwait(false);
        Publish(partial, path);
    }

    private static int IndexOf(string[] columns, string name, string sourcePath)
    {
        var index = Array.FindIndex(columns, column =>
            string.Equals(column.Trim(), name, StringComparison.OrdinalIgnoreCase));

        return index >= 0
            ? index
            : throw new InvalidOperationException(
                $"'{sourcePath}' has no '{name}' column; it holds: {string.Join(", ", columns)}");
    }

    private sealed record CbsaRow(
        string CbsaCode,
        string CbsaTitle,
        string AreaType,
        string CountyFips,
        string CountyName,
        string StateName,
        string CentralOutlying);
}
