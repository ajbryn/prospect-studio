#:package DuckDB.NET.Data.Full@1.5.6
#:package ClosedXML@0.105.1

// Regenerates the committed geography fixtures next to this file, from the real Census sources
// verified in chunk C2 (implementation-plan "Decisions made during implementation", 2026-09-30 · V5).
//
//   dotnet run build-geo-fixtures.cs                      # downloads into %TEMP%\prospect-studio-census
//   dotnet run build-geo-fixtures.cs -- --cache D:\census  # reuse an existing download folder
//   dotnet run build-geo-fixtures.cs -- --out .            # where the fixtures are written
//
// Needs network on first run (the downloads, and DuckDB's spatial/excel extensions). The tests
// themselves never download anything: that is the whole point of committing the outputs.
//
// Outputs (see README.md in this folder for the column contracts):
//   counties_houston.parquet   the ten Houston CBSA counties + Jefferson 48245, simplified geometry
//   cbsa_excerpt.csv           every Texas CBSA row + every "Springfield, *" CBSA row
//   zcta_county_excerpt.csv    ZCTA-to-county rows for the eleven counties
//   sources\*                  trimmed copies of the three raw sources, keeping every awkward shape
//                              the parser has to cope with, so the download-and-parse direction is
//                              covered offline

using System.Globalization;
using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using DuckDB.NET.Data;

var cache = Argument("--cache") ?? Path.Combine(Path.GetTempPath(), "prospect-studio-census");
var outDir = Path.GetFullPath(Argument("--out") ?? ".");   // run it from this folder, or pass --out
Directory.CreateDirectory(cache);
Directory.CreateDirectory(outDir);

// The eleven counties: the ten in the Houston-Pasadena-The Woodlands CBSA (26420) plus Jefferson
// (48245, Beaumont), which is deliberately outside it so C4 can prove that scoping excludes it.
string[] counties =
[
    "48015", // Austin
    "48039", // Brazoria
    "48071", // Chambers
    "48157", // Fort Bend
    "48167", // Galveston
    "48201", // Harris
    "48291", // Liberty
    "48339", // Montgomery
    "48407", // San Jacinto
    "48473", // Waller
    "48245", // Jefferson - NOT in CBSA 26420
];

// 0.001 degrees is roughly 110 m: small enough that the county borders are still recognisable and
// a point-in-polygon test lands in the right county, and it takes the file from 75 KB to 33 KB.
const double SimplifyTolerance = 0.001;

var sources = new (string Name, string Url)[]
{
    ("cb_2025_us_county_500k.zip", "https://www2.census.gov/geo/tiger/GENZ2025/shp/cb_2025_us_county_500k.zip"),
    ("list1_2023.xlsx", "https://www2.census.gov/programs-surveys/metro-micro/geographies/reference-files/2023/delineation-files/list1_2023.xlsx"),
    ("tab20_zcta520_county20_natl.txt", "https://www2.census.gov/geo/docs/maps-data/data/rel2020/zcta520/tab20_zcta520_county20_natl.txt"),
};

foreach (var (name, url) in sources)
{
    var path = Path.Combine(cache, name);
    if (File.Exists(path))
    {
        Console.WriteLine($"cached  {name} ({new FileInfo(path).Length:N0} bytes)");
        continue;
    }

    Console.WriteLine($"GET     {url}");
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("ProspectStudio-fixture-builder/0.1 (+https://github.com/ajbryn)");
    await using var stream = await http.GetStreamAsync(url);
    await using var file = File.Create(path);
    await stream.CopyToAsync(file);
    Console.WriteLine($"saved   {name} ({file.Length:N0} bytes)");
}

using var db = new DuckDBConnection("Data Source=:memory:");
db.Open();
Exec("INSTALL spatial");
Exec("LOAD spatial");   // required on every connection, not just at install time (C2 · V7)
Exec("INSTALL excel");
Exec("LOAD excel");

var countyList = string.Join(",", counties.Select(id => $"'{id}'"));
var zipShapefile = $"/vsizip/{Forward(Path.Combine(cache, "cb_2025_us_county_500k.zip"))}/cb_2025_us_county_500k.shp";
var countiesOut = Forward(Path.Combine(outDir, "counties_houston.parquet"));

// ST_Read's geometry column is `geom` (lowercase); the attributes are uppercase VARCHAR. The
// production pipeline (technical-design §6.1) aliases it to `geometry`, so the fixture does too.
Exec($"""
      COPY (
        SELECT GEOID, NAME, STATEFP,
               ST_SimplifyPreserveTopology(geom, {SimplifyTolerance.ToString(CultureInfo.InvariantCulture)}) AS geometry
        FROM ST_Read('{zipShapefile}')
        WHERE GEOID IN ({countyList})
        ORDER BY GEOID
      ) TO '{countiesOut}' (FORMAT PARQUET, COMPRESSION ZSTD)
      """);

var cbsaXlsx = Forward(Path.Combine(cache, "list1_2023.xlsx"));
var cbsaOut = Forward(Path.Combine(outDir, "cbsa_excerpt.csv"));

// Rows 1-2 are a title, row 3 is the header, and the sheet ends in notes and blanks - hence the
// explicit range and the "CBSA Code is five digits" filter. State and county FIPS are separate
// string columns that have to be concatenated into a 5-digit GEOID.
Exec($"""
      COPY (
        SELECT "CBSA Code"                                 AS cbsa_code,
               "CBSA Title"                                AS cbsa_title,
               "Metropolitan/Micropolitan Statistical Area" AS area_type,
               "FIPS State Code" || "FIPS County Code"      AS county_geoid,
               "County/County Equivalent"                  AS county_name,
               "State Name"                                AS state_name,
               "Central/Outlying County"                   AS central_outlying
        FROM read_xlsx('{cbsaXlsx}', header = true, all_varchar = true, range = 'A3:L5000')
        WHERE regexp_matches("CBSA Code", '^[0-9][0-9][0-9][0-9][0-9]$')
          AND ("State Name" = 'Texas' OR "CBSA Title" LIKE 'Springfield,%')
        ORDER BY cbsa_code, county_geoid
      ) TO '{cbsaOut}' (FORMAT CSV, HEADER)
      """);

var zctaTxt = Forward(Path.Combine(cache, "tab20_zcta520_county20_natl.txt"));
var zctaOut = Forward(Path.Combine(outDir, "zcta_county_excerpt.csv"));

// Pipe-delimited with a UTF-8 BOM, and rows whose ZCTA is empty (counties that contain none) must
// be filtered out or a ZIP lookup matches the blank.
Exec($"""
      COPY (
        SELECT GEOID_ZCTA5_20 AS zcta5, GEOID_COUNTY_20 AS county_geoid
        FROM read_csv('{zctaTxt}', delim = '|', header = true, all_varchar = true)
        WHERE GEOID_ZCTA5_20 IS NOT NULL AND GEOID_ZCTA5_20 <> ''
          AND GEOID_COUNTY_20 IN ({countyList})
        ORDER BY zcta5, county_geoid
      ) TO '{zctaOut}' (FORMAT CSV, HEADER)
      """);

// ── Trimmed raw sources ──────────────────────────────────────────────────────────────────────────
// These exist so the *download-and-parse* direction is testable offline. Each keeps the awkward shape
// of its real counterpart: the GDAL virtual path into a zip, an xlsx with two title rows above a
// row-3 header and trailing notes below the data, and a pipe-delimited file with a UTF-8 BOM and
// rows whose ZCTA is empty.
var sourcesDir = Path.Combine(outDir, "sources");
Directory.CreateDirectory(sourcesDir);

// 1. One county (Harris 48201), with every attribute column ST_Read returns, zipped under the same
//    names the real archive uses so '/vsizip/<zip>/<stem>.shp' works unchanged.
var shapefileStem = "cb_2025_us_county_500k";
var staging = Path.Combine(Path.GetTempPath(), "prospect-studio-shp-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(staging);

Exec($"""
      COPY (
        SELECT STATEFP, COUNTYFP, COUNTYNS, GEOIDFQ, GEOID, NAME, NAMELSAD, STUSPS, STATE_NAME,
               LSAD, ALAND, AWATER, geom
        FROM ST_Read('{zipShapefile}')
        WHERE GEOID = '48201'
      ) TO '{Forward(Path.Combine(staging, shapefileStem + ".shp"))}'
        WITH (FORMAT GDAL, DRIVER 'ESRI Shapefile')
      """);

var countyZip = Path.Combine(sourcesDir, shapefileStem + ".zip");
File.Delete(countyZip);
using (var archive = ZipFile.Open(countyZip, ZipArchiveMode.Create))
{
    foreach (var part in Directory.EnumerateFiles(staging).Order(StringComparer.Ordinal))
    {
        archive.CreateEntryFromFile(part, Path.GetFileName(part), CompressionLevel.SmallestSize);
    }
}

Directory.Delete(staging, recursive: true);

// 2. A cut-down list1 workbook: rows 1-2 title, row 3 header, three CBSAs of data, a blank row and
//    the two trailing note rows. Cell values are copied with their original types, which are all text
//    in the real file - including the separate FIPS columns ('48' and '015').
string[] keptCbsas = ["13140", "26420", "44180"];
using (var real = new XLWorkbook(Path.Combine(cache, "list1_2023.xlsx")))
{
    var source = real.Worksheets.First();
    var lastRow = source.LastRowUsed()!.RowNumber();
    var lastColumn = source.LastColumnUsed()!.ColumnNumber();

    var keptRows = new List<int> { 1, 2, 3 };
    keptRows.AddRange(
        Enumerable.Range(4, lastRow - 3)
            .Where(row => keptCbsas.Contains(source.Cell(row, 1).GetString().Trim())));

    // Everything after the data block: the blank separator and the Note/Source lines.
    var firstNote = Enumerable.Range(4, lastRow - 3)
        .Last(row => source.Cell(row, 1).GetString().Trim().Length == 5
                     && source.Cell(row, 1).GetString().Trim().All(char.IsDigit)) + 1;
    keptRows.AddRange(Enumerable.Range(firstNote, lastRow - firstNote + 1));

    using var trimmed = new XLWorkbook();
    var target = trimmed.AddWorksheet(source.Name);
    var written = 0;
    foreach (var row in keptRows)
    {
        written++;
        for (var column = 1; column <= lastColumn; column++)
        {
            var cell = source.Cell(row, column);
            if (!cell.IsEmpty())
            {
                target.Cell(written, column).Value = cell.Value;
            }
        }
    }

    trimmed.SaveAs(Path.Combine(sourcesDir, "list1_2023.xlsx"));
    Console.WriteLine($"trimmed list1  {written} rows (3 of header/title, {keptRows.Count - 3 - (lastRow - firstNote + 1)} of data)");
}

// 3. A short ZCTA extract: the real header, one row whose ZCTA is empty (a county that contains
//    none - these must be filtered out), then the ZIPs the geography and dealer tests use. Written
//    with a UTF-8 BOM, like the real file.
var zctaLines = File.ReadAllLines(Path.Combine(cache, "tab20_zcta520_county20_natl.txt"));
var header = zctaLines[0].TrimStart('﻿');
var zctaColumns = header.Split('|');
var zctaIndex = Array.IndexOf(zctaColumns, "GEOID_ZCTA5_20");
var countyIndex = Array.IndexOf(zctaColumns, "GEOID_COUNTY_20");
string[] keptZips = ["77494", "77506", "77449", "77301", "77550", "77701"];

var kept = new List<string> { header };
kept.AddRange(zctaLines.Skip(1).Where(line => line.Split('|')[zctaIndex].Length == 0).Take(2));
kept.AddRange(zctaLines.Skip(1).Where(line =>
{
    var fields = line.Split('|');
    return keptZips.Contains(fields[zctaIndex]) && counties.Contains(fields[countyIndex]);
}));

File.WriteAllText(
    Path.Combine(sourcesDir, "tab20_zcta520_county20_natl.txt"),
    string.Join("\r\n", kept) + "\r\n",
    new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

foreach (var name in new[] { "counties_houston.parquet", "cbsa_excerpt.csv", "zcta_county_excerpt.csv" })
{
    var path = Path.Combine(outDir, name);
    Console.WriteLine($"wrote   {name} ({new FileInfo(path).Length:N0} bytes)");
}

foreach (var path in Directory.EnumerateFiles(sourcesDir).Order(StringComparer.Ordinal))
{
    Console.WriteLine($"wrote   sources/{Path.GetFileName(path)} ({new FileInfo(path).Length:N0} bytes)");
}

Console.WriteLine($"counties       {Scalar($"SELECT count(*) FROM read_parquet('{countiesOut}')")}");
Console.WriteLine($"cbsa rows      {Scalar($"SELECT count(*) FROM read_csv('{cbsaOut}')")}");
Console.WriteLine($"houston 26420  {Scalar($"SELECT string_agg(county_geoid, ',' ORDER BY county_geoid) FROM read_csv('{cbsaOut}') WHERE cbsa_code = '26420'")}");
Console.WriteLine($"springfields   {Scalar($"SELECT string_agg(DISTINCT cbsa_title, ' / ') FROM read_csv('{cbsaOut}') WHERE cbsa_title LIKE 'Springfield,%'")}");
Console.WriteLine($"zcta rows      {Scalar($"SELECT count(*) FROM read_csv('{zctaOut}')")}");
Console.WriteLine($"zip 77494      {Scalar($"SELECT string_agg(county_geoid, ',' ORDER BY county_geoid) FROM read_csv('{zctaOut}') WHERE zcta5 = '77494'")}");
Console.WriteLine($"downtown       {Scalar($"SELECT GEOID FROM read_parquet('{countiesOut}') WHERE ST_Within(ST_Point(-95.3698, 29.7604), geometry)")}");

void Exec(string sql)
{
    using var command = db.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

object? Scalar(string sql)
{
    using var command = db.CreateCommand();
    command.CommandText = sql;
    return command.ExecuteScalar();
}

static string Forward(string path) => path.Replace('\\', '/');

string? Argument(string name)
{
    var argv = Environment.GetCommandLineArgs();
    var index = Array.IndexOf(argv, name);
    return index >= 0 && index + 1 < argv.Length ? argv[index + 1] : null;
}
