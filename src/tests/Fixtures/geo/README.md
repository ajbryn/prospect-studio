# Geography test fixtures

Small, committed excerpts of the real Census reference data, so no test ever downloads anything.
Regenerate with [`build-geo-fixtures.cs`](build-geo-fixtures.cs) (a .NET 10 file-based app):

```pwsh
cd src/tests/Fixtures/geo
dotnet run build-geo-fixtures.cs                        # downloads to %TEMP%\prospect-studio-census
dotnet run build-geo-fixtures.cs -- --cache D:\census    # reuse an existing download folder
```

Sources are the URLs verified in chunk C2 (see the decisions table in `poc/implementation-plan.md`).

## Prepared outputs

These stand in for a `refdata\` folder that `prepare_data` has already filled, so a server under test
knows US geography. `ReferenceDataFixture` copies them in under their production names.

| File | Rows | What |
|---|---|---|
| `counties_houston.parquet` | 11 | The ten counties of CBSA 26420 plus **Jefferson 48245** (Beaumont), which is deliberately outside it so C4 can prove scoping excludes it |
| `cbsa_excerpt.csv` | 142 | Every Texas CBSA↔county row, plus every `Springfield, *` row (four of them: IL, MA, MO, OH) for the ambiguous-query case |
| `zcta_county_excerpt.csv` | 321 | ZCTA↔county rows for those eleven counties, including ZIP **77494**, which spans 48157, 48201 and 48473 |

## Trimmed raw sources (`sources\`)

Cut-down copies of the three real downloads, each keeping the shape that is easy to parse wrongly, so
the *download-and-parse* direction is covered offline through `IReferenceFileSource` instead of only in
the opt-in network tests. `ReferenceDataPreparerTests` asserts what each one must turn into.

| File | What it keeps | Expected output |
|---|---|---|
| `cb_2025_us_county_500k.zip` | One county (Harris 48201) with every attribute column `ST_Read` returns, zipped under the real names — so it is reachable only through `/vsizip/<zip>/cb_2025_us_county_500k.shp`, with forward slashes | 1 row; `ST_Within(ST_Point(-95.3698, 29.7604), geometry)` is true |
| `list1_2023.xlsx` | Sheet `List 1`: rows 1–2 title, **row 3 header**, 18 data rows (Beaumont 3, Houston 10, Springfield MO 5), a blank row, then the two trailing note rows. All cells are text, including the separate `FIPS State Code` / `FIPS County Code` columns | 18 rows; `48` + `015` concatenated to `48015` |
| `tab20_zcta520_county20_natl.txt` | Pipe-delimited with a **UTF-8 BOM**, two rows whose ZCTA is empty, then the ZIPs the geography and dealer tests use | 8 rows; the empty-ZCTA rows dropped; no BOM left on the first field |

## Column contracts

These are the shapes the tests expect the setup pipeline (technical-design §6.1) to produce in
`refdata\`. `ReferenceDataFixture` copies each file to the production name shown here.

**`counties_houston.parquet` → `refdata\counties.parquet`**
`GEOID`, `NAME`, `STATEFP` (uppercase `VARCHAR`, as `ST_Read` returns them) and `geometry`
(`GEOMETRY`, WKB in the file, EPSG:4269). Geometry is simplified with
`ST_SimplifyPreserveTopology(geom, 0.001)` — about 110 m — which takes the file from 75 KB to 33 KB
and still puts a point-in-polygon test in the right county. `LOAD spatial` is required on **every**
connection before any `ST_*` call, even to read this file's geometry.

**`cbsa_excerpt.csv` → `refdata\cbsa.csv`**
`cbsa_code`, `cbsa_title`, `area_type`, `county_geoid`, `county_name`, `state_name`,
`central_outlying`. One row per CBSA↔county pair. `county_geoid` is the 5-digit concatenation of the
source file's separate `FIPS State Code` and `FIPS County Code` columns.

**`zcta_county_excerpt.csv` → `refdata\zcta_county.csv`**
`zcta5`, `county_geoid`. One row per pair; the source's empty-ZCTA rows are already filtered out.

## Facts the tests rely on

- CBSA **26420** is "Houston-Pasadena-The Woodlands, TX" and has **ten** counties:
  48015, 48039, 48071, 48157, 48167, 48201, 48291, 48339, **48407**, 48473.
- Jefferson County **48245** belongs to CBSA 13140 (Beaumont-Port Arthur, TX), not 26420.
- ZIP **77494** spans three counties: 48157, 48201, 48473.
- `ST_Point(-95.3698, 29.7604)` — note **(lon, lat)** — falls inside 48201.
