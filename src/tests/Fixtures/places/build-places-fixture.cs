#:package DuckDB.NET.Data.Full@1.5.6

// Builds the committed Overture Places test fixture next to this file, from the shared
// poc/fixtures/sample-places.csv. Run it after editing that CSV:
//
//   cd src/tests/Fixtures/places
//   dotnet run build-places-fixture.cs
//   dotnet run build-places-fixture.cs -- --out . --csv ../../../../poc/fixtures/sample-places.csv
//
// Output: sample_places.parquet, with the real Overture Places schema (verified against release
// 2026-09-23.1 in chunk C4 - see technical-design §6.1):
//
//   id              VARCHAR
//   name            VARCHAR            -- §6.1's `names.primary AS name`
//   basic_category  VARCHAR            -- a coarser rollup, often NOT the leaf
//   taxonomy        STRUCT(primary VARCHAR, hierarchy VARCHAR[], alternates VARCHAR[])
//   confidence      DOUBLE
//   websites        VARCHAR[]          -- frequently empty
//   phones          VARCHAR[]          -- bare digits, sometimes with a leading country 1
//   addresses       STRUCT(freeform, locality, postcode, region, country)[]
//   geometry        GEOMETRY('OGC:CRS84')
//   bbox            STRUCT(xmin, xmax, ymin, ymax)
//
// The CRS matters more than anything else here. Overture publishes OGC:CRS84 while the Census
// counties Parquet from chunk C2 is EPSG:4269, and DuckDB raises a Binder error on ST_Within across
// mismatched CRS (technical-design §6.3). The production query fixes that with
// ST_SetCRS(c.geometry, 'OGC:CRS84') on the *county* side - so this fixture has to carry CRS84
// geometry, or the tests would pass against a fixture while the real file throws.
// PlacesFixtureIntegrityTests asserts both halves of that: the aligned join works, and the
// unaligned one still fails.
//
// Needs the DuckDB `spatial` extension in the local cache; `dotnet run --project
// src/ProspectStudio.Mcp -- doctor` primes it. No network otherwise: the CSV is committed.

using System.Globalization;
using DuckDB.NET.Data;

var outDir = Path.GetFullPath(Argument("--out") ?? ".");
var csv = Path.GetFullPath(
    Argument("--csv") ?? Path.Combine(outDir, "..", "..", "..", "..", "poc", "fixtures", "sample-places.csv"));

if (!File.Exists(csv))
{
    Console.Error.WriteLine($"No sample-places.csv at '{csv}'. Pass --csv <path>.");
    return 1;
}

Directory.CreateDirectory(outDir);
var output = Path.Combine(outDir, "sample_places.parquet");

using var db = new DuckDBConnection("Data Source=:memory:");
db.Open();
Exec("INSTALL spatial");
Exec("LOAD spatial");   // required on every new connection, not just at install time (C2 · V7)

// read_csv with explicit types: confidence must be DOUBLE and every other column VARCHAR, so an
// all-digits phone or a 5-digit FIPS never loses its leading zero or its shape.
Exec($$"""
      CREATE TABLE places AS
      SELECT * FROM read_csv('{{Literal(csv)}}',
        header = true,
        types = {
          'id': 'VARCHAR', 'name': 'VARCHAR', 'taxonomy_primary': 'VARCHAR',
          'taxonomy_hierarchy': 'VARCHAR', 'basic_category': 'VARCHAR', 'confidence': 'DOUBLE',
          'websites': 'VARCHAR', 'phones': 'VARCHAR', 'freeform': 'VARCHAR', 'locality': 'VARCHAR',
          'region': 'VARCHAR', 'postcode': 'VARCHAR', 'country': 'VARCHAR', 'county_fips': 'VARCHAR',
          'lat': 'DOUBLE', 'lon': 'DOUBLE', 'fixture_note': 'VARCHAR'
        })
      """);

// `county_fips` and `fixture_note` are deliberately NOT written: the real Overture file has no such
// columns, so a production query that reached for one would compile against the fixture and then
// fail against real data. The expected county comes from the CSV, which the tests read separately.
Exec($$"""
      COPY (
        SELECT
          id,
          name,
          basic_category,
          {
            'primary':    taxonomy_primary,
            'hierarchy':  str_split(taxonomy_hierarchy, '|'),
            'alternates': []::VARCHAR[]
          } AS taxonomy,
          confidence,
          CASE WHEN websites IS NULL OR websites = '' THEN []::VARCHAR[] ELSE str_split(websites, '|') END AS websites,
          CASE WHEN phones   IS NULL OR phones   = '' THEN []::VARCHAR[] ELSE str_split(phones,   '|') END AS phones,
          [{
            'freeform': CASE WHEN freeform IS NULL OR freeform = '' THEN NULL ELSE freeform END,
            'locality': locality,
            'postcode': postcode,
            'region':   region,
            'country':  country
          }] AS addresses,
          ST_SetCRS(ST_Point(lon, lat)::GEOMETRY, 'OGC:CRS84') AS geometry,
          { 'xmin': lon, 'xmax': lon, 'ymin': lat, 'ymax': lat } AS bbox
        FROM places
        ORDER BY id
      ) TO '{{Literal(output)}}' (FORMAT PARQUET, COMPRESSION ZSTD)
      """);

var rows = Convert.ToInt64(Scalar($"SELECT count(*) FROM read_parquet('{Literal(output)}')"), CultureInfo.InvariantCulture);
// The CRS travels in the column's *type*, which is how technical-design §6.3's Binder error arises.
var crs = Scalar($"SELECT typeof(geometry) FROM read_parquet('{Literal(output)}') LIMIT 1");

Console.WriteLine($"wrote {output} ({new FileInfo(output).Length:N0} bytes, {rows:N0} rows)");
Console.WriteLine($"geometry type: {crs}");
return 0;

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

static string Literal(string path) => path.Replace('\\', '/').Replace("'", "''", StringComparison.Ordinal);

string? Argument(string name)
{
    var argv = Environment.GetCommandLineArgs();
    var index = Array.IndexOf(argv, name);
    return index >= 0 && index + 1 < argv.Length ? argv[index + 1] : null;
}
