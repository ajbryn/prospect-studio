using System.Globalization;
using ClosedXML.Excel;
using ProspectStudio.Core.Dealers;
using ProspectStudio.Infrastructure.Reference;

namespace ProspectStudio.Infrastructure.Lists;

/// <summary>A list file read into memory: its header and its data rows.</summary>
internal sealed record ListFile(IReadOnlyList<string> Columns, IReadOnlyList<ListRow> Rows);

/// <summary>
/// Reads one of the three business lists off disk. mcp-tools.md §import_list: "Formats: see
/// <c>poc/fixtures/*.csv</c> headers. XLSX accepted with the same headers on the first sheet."
/// </summary>
internal static class ListFileReader
{
    /// <summary>The extensions <c>import_list</c> and its workspace default accept.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".csv", ".xlsx"];

    public static bool IsSpreadsheet(string path) =>
        Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".xlsm", StringComparison.OrdinalIgnoreCase);

    public static async Task<ListFile> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var rows = IsSpreadsheet(path)
            ? ReadWorkbook(path, cancellationToken)
            : await ReadDelimitedAsync(path, cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0)
        {
            throw new ImportListRequestException(
                $"'{Path.GetFileName(path)}' has no header row.",
                "The first row names the columns; see poc/fixtures for the headers of each list.");
        }

        var columns = rows[0].Cells;

        return new ListFile(
            columns,
            [
                .. rows.Skip(1)
                    .Where(row => row.Cells.Any(cell => cell.Length > 0))
                    .Select(row => new ListRow(row.Number, Named(columns, row.Cells))),
            ]);
    }

    /// <summary>
    /// A cell by column name. A duplicated header keeps its first column, which is the one a person
    /// reading the file would point at.
    /// </summary>
    private static Dictionary<string, string> Named(IReadOnlyList<string> columns, IReadOnlyList<string> cells)
    {
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < columns.Count; index++)
        {
            if (columns[index].Length > 0)
            {
                named.TryAdd(columns[index], index < cells.Count ? cells[index] : string.Empty);
            }
        }

        return named;
    }

    private static async Task<List<RawRow>> ReadDelimitedAsync(string path, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);

        // The row number is the line number, so the header is row 1 and the first data row is row 2 -
        // the numbers the user sees when they open the file (mcp-tools.md §import_list).
        return
        [
            .. lines
                .Select((line, index) => new RawRow(index + 1, Cells(DelimitedText.Split(line, ','))))
                .Where(row => row.Number == 1 || row.Cells.Any(cell => cell.Length > 0)),
        ];
    }

    private static List<RawRow> ReadWorkbook(string path, CancellationToken cancellationToken)
    {
        // ClosedXML has no asynchronous API; the file is opened read-only so a list the user still has
        // open in a spreadsheet can be imported.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var workbook = new XLWorkbook(stream);

        // "the same headers on the first sheet": the first sheet by position, not the only sheet.
        var sheet = workbook.Worksheets.FirstOrDefault()
            ?? throw new ImportListRequestException(
                $"'{Path.GetFileName(path)}' has no worksheets.",
                "Put the list on the first sheet, with the column names in row 1.");

        var used = sheet.RangeUsed();
        if (used is null)
        {
            return [];
        }

        var columnCount = used.ColumnCount();
        var rows = new List<RawRow>();

        foreach (var row in used.Rows())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cells = new List<string>(columnCount);
            for (var column = 1; column <= columnCount; column++)
            {
                cells.Add(Text(row.Cell(column)));
            }

            // The worksheet row number, so the header is row 1 and an error names the row the user sees.
            rows.Add(new RawRow(row.WorksheetRow().RowNumber(), Cells(cells)));
        }

        return rows;
    }

    /// <summary>
    /// A cell as text. The number case is explicit and invariant: a ZIP or a county FIPS that a
    /// spreadsheet stored as a number must not come back with a thousands separator or a decimal point,
    /// and the current culture has no business deciding what <c>29.7858</c> means.
    /// </summary>
    private static string Text(IXLCell cell)
    {
        var value = cell.Value;

        return value.Type switch
        {
            XLDataType.Blank => string.Empty,
            XLDataType.Text => value.GetText(),
            XLDataType.Number => value.GetNumber().ToString("0.##########", CultureInfo.InvariantCulture),
            XLDataType.Boolean => value.GetBoolean() ? "true" : "false",
            XLDataType.DateTime => value.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            XLDataType.TimeSpan => value.GetTimeSpan().ToString("c", CultureInfo.InvariantCulture),
            _ => string.Empty,
        };
    }

    private static List<string> Cells(IEnumerable<string> cells) => [.. cells.Select(cell => cell.Trim())];

    private sealed record RawRow(int Number, List<string> Cells);
}
