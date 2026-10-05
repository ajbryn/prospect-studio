#:package ClosedXML@0.105.1

// Builds the committed import_list test fixtures next to this file, from the three business lists in
// poc/fixtures. Run it after editing any of them:
//
//   cd src/tests/Fixtures/lists
//   dotnet run build-list-fixtures.cs
//   dotnet run build-list-fixtures.cs -- --out . --fixtures ../../../../poc/fixtures
//
// Output:
//
//   dealers.xlsx                  poc/fixtures/dealers.csv, same headers, on the first sheet
//   territories.xlsx              poc/fixtures/territories.csv
//   suppression.xlsx              poc/fixtures/suppression.csv
//   territories_bad_dealer.csv    five territory rows, one naming a dealer that does not exist
//   territories_bad_dealer.xlsx   the same five rows, so the row number is proven in both formats
//   territories_bad_code.csv      five territory rows, one whose code lost its leading zero
//   territories_bad_code.xlsx     the same five rows
//   territories_mixed_case_branch.csv  the real 35 rows with every branch_id upper-cased
//
// mcp-tools.md §import_list: "XLSX accepted with the same headers on the first sheet." These files
// exist so that half of the contract is covered without a test having to write a spreadsheet at run
// time, and so the committed artefact is the thing under test. ClosedXML is the library the server
// uses (CLAUDE.md §Stack), so the files are shaped the way the server will read them.
//
// The bad-dealer files are deliberately small and purpose-built rather than a copy of
// territories.csv with one row edited: the point is that the reported row number is unambiguous, and
// five rows make "row 4" readable at a glance. The header is row 1, so the broken row is row 4.
// No network: the CSVs are committed.

using ClosedXML.Excel;

var outDir = Path.GetFullPath(Argument("--out") ?? ".");
var fixtures = Path.GetFullPath(
    Argument("--fixtures") ?? Path.Combine(outDir, "..", "..", "..", "..", "poc", "fixtures"));

Directory.CreateDirectory(outDir);

foreach (var name in new[] { "dealers", "territories", "suppression" })
{
    var csv = Path.Combine(fixtures, $"{name}.csv");
    if (!File.Exists(csv))
    {
        Console.Error.WriteLine($"No {name}.csv at '{csv}'. Pass --fixtures <poc/fixtures>.");
        return 1;
    }

    var rows = ReadCsv(csv);
    var path = Path.Combine(outDir, $"{name}.xlsx");
    Write(path, rows);
    Console.WriteLine($"wrote {path} ({rows.Count - 1} data rows)");
}

// Row 4 names 'gulff', which is not a dealer id. Rows 2, 3, 5 and 6 are good and must still import.
List<string[]> bad =
[
    ["dealer_id", "branch_id", "level", "code", "priority"],
    ["gulf", "gulf-west", "county", "48201", "2"],
    ["gulf", "gulf-west", "county", "48157", "1"],
    ["gulff", "gulf-west", "county", "48015", "1"],
    ["bay", "bay-pas", "zip", "77506", "1"],
    ["pine", "pine-north", "county", "48339", "1"],
];

var badCsv = Path.Combine(outDir, "territories_bad_dealer.csv");
await File.WriteAllLinesAsync(badCsv, bad.Select(row => string.Join(',', row)));
Console.WriteLine($"wrote {badCsv} ({bad.Count - 1} data rows, row 4 is broken)");

var badXlsx = Path.Combine(outDir, "territories_bad_dealer.xlsx");
Write(badXlsx, bad);
Console.WriteLine($"wrote {badXlsx} ({bad.Count - 1} data rows, row 4 is broken)");

// Row 4's code is '7494': what a spreadsheet does to a ZIP it stored as a number. No Texas ZIP or
// FIPS can exercise the dropped-leading-zero case (they all start 7 and 48), so the only way to reach
// the five-digit rule from a file is to write the damaged value out by hand.
List<string[]> badCode =
[
    ["dealer_id", "branch_id", "level", "code", "priority"],
    ["gulf", "gulf-west", "county", "48201", "2"],
    ["gulf", "gulf-west", "county", "48157", "1"],
    ["bay", "bay-pas", "zip", "7494", "1"],
    ["bay", "bay-pas", "zip", "77507", "1"],
    ["pine", "pine-north", "county", "48339", "1"],
];

var badCodeCsv = Path.Combine(outDir, "territories_bad_code.csv");
await File.WriteAllLinesAsync(badCodeCsv, badCode.Select(row => string.Join(',', row)));
Console.WriteLine($"wrote {badCodeCsv} ({badCode.Count - 1} data rows, row 4 is broken)");

var badCodeXlsx = Path.Combine(outDir, "territories_bad_code.xlsx");
Write(badCodeXlsx, badCode);
Console.WriteLine($"wrote {badCodeXlsx} ({badCode.Count - 1} data rows, row 4 is broken)");

// The committed territory list with every branch_id upper-cased. DealerIds.Territory hashes the raw
// branch_id while the column is stored lower-cased, so a re-import of this file must still recognize
// all 35 rows instead of minting a second copy of each.
var territories = ReadCsv(Path.Combine(fixtures, "territories.csv"));
var branchColumn = Array.IndexOf(territories[0], "branch_id");
List<string[]> upperCased = [territories[0]];
foreach (var row in territories.Skip(1))
{
    var copy = (string[])row.Clone();
    copy[branchColumn] = copy[branchColumn].ToUpperInvariant();
    upperCased.Add(copy);
}

var mixedCase = Path.Combine(outDir, "territories_mixed_case_branch.csv");
await File.WriteAllLinesAsync(mixedCase, upperCased.Select(row => string.Join(',', row)));
Console.WriteLine($"wrote {mixedCase} ({upperCased.Count - 1} data rows, branch_id upper-cased)");

return 0;

static void Write(string path, List<string[]> rows)
{
    using var workbook = new XLWorkbook();

    // mcp-tools.md: the headers go on the FIRST sheet. A second sheet after it proves the importer
    // reads the first one rather than the only one.
    var sheet = workbook.AddWorksheet("Sheet1");

    for (var row = 0; row < rows.Count; row++)
    {
        for (var column = 0; column < rows[row].Length; column++)
        {
            // Everything as text: a ZIP, a county FIPS and a priority must all survive the round trip,
            // and Excel would otherwise turn 48015 into a number and 07030 into 7030.
            sheet.Cell(row + 1, column + 1).SetValue(rows[row][column]);
        }
    }

    workbook.AddWorksheet("Notes").Cell(1, 1).SetValue("Generated by build-list-fixtures.cs - do not edit by hand.");

    workbook.SaveAs(path);
}

static List<string[]> ReadCsv(string path)
{
    var rows = new List<string[]>();

    foreach (var line in File.ReadAllLines(path).Where(line => line.Trim().Length > 0))
    {
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;

        foreach (var character in line)
        {
            if (character == '"')
            {
                quoted = !quoted;
            }
            else if (character == ',' && !quoted)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(character);
            }
        }

        fields.Add(field.ToString());
        rows.Add([.. fields]);
    }

    return rows;
}

static string? Argument(string name)
{
    var args = Environment.GetCommandLineArgs();
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
