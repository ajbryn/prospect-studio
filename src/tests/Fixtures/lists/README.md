# `import_list` test fixtures (chunk C5)

The XLSX halves of `import_list`, plus the deliberately broken territory file. Nothing is downloaded:
the CSVs in `poc/fixtures` are the source of truth and the generator is committed.

```pwsh
cd src/tests/Fixtures/lists
dotnet run build-list-fixtures.cs      # rebuilds every file below
```

| File | What |
|---|---|
| `dealers.xlsx` | [`poc/fixtures/dealers.csv`](../../../../poc/fixtures/dealers.csv) with the same headers on the first sheet |
| `territories.xlsx` | [`poc/fixtures/territories.csv`](../../../../poc/fixtures/territories.csv), 35 rows |
| `suppression.xlsx` | [`poc/fixtures/suppression.csv`](../../../../poc/fixtures/suppression.csv), 7 rows |
| `territories_bad_dealer.csv` | Five territory rows; **row 4** names `gulff`, which is not a dealer id |
| `territories_bad_dealer.xlsx` | The same five rows, so the reported row number is proven in both formats |
| `territories_bad_code.csv` / `.xlsx` | Five territory rows; **row 4**'s code is `7494` — a ZIP a spreadsheet stored as a number, so the leading zero is gone |
| `territories_mixed_case_branch.csv` | The real 35 territory rows with every `branch_id` upper-cased, for the re-import idempotency case |
| `build-list-fixtures.cs` | The committed generator (a .NET 10 file-based app using ClosedXML, the library the server reads XLSX with) |

## Things these files are shaped to prove

- **Same headers, first sheet.** mcp-tools.md §`import_list`: "XLSX accepted with the same headers on
  the first sheet." Each workbook carries a second sheet (`Notes`) after `Sheet1`, so an importer that
  reads *the only* sheet rather than *the first* sheet fails here instead of on a real user file.
- **Every cell is text.** A county FIPS (`48015`), a ZIP (`77506`) and a priority (`2`) all have to
  survive the round trip. Excel turns `48015` into a number and `07030` into `7030` given the chance,
  and a FIPS that lost a digit routes a lead to the wrong dealer rather than failing loudly.
- **Row numbers are spreadsheet row numbers.** The header is row 1, so the first data row is row 2 and
  the broken row in `territories_bad_dealer.*` is **row 4**. That is the number the user sees when they
  open the file, which is the only number an error message can usefully name. The plan's test is "an
  import with a bad `dealer_id` in territories reports the row number and imports the rest", so the
  other four rows must still land.
- **Small and purpose-built, not a copy with one edit.** Five rows keep "row 4" readable; an edited
  copy of the 35-row territory file would make an off-by-one invisible.

## Why the damaged code has to be written out by hand

`territories.code` must be exactly **five ASCII digits**, and the failure that rule exists to catch is
a spreadsheet storing the column as a number and dropping a leading zero. **No fixture value can
reach it**: every ZIP in these lists starts with `7` and every county FIPS with `48`, so none of them
loses a digit however the file is saved. `territories_bad_code.*` carries the damaged value
explicitly, and `ListRowTests` covers the rest of the rule — six digits, a decimal point, an embedded
space, and non-ASCII digits — as unit cases, because a rule that only ever runs against data which
cannot break it is a rule nothing tests.

A code that reaches the database damaged does not fail: it matches no lead, so every business in the
real ZIP is reported as a **coverage gap**, which is indistinguishable from a territory the dealer
genuinely does not cover.

The dealers, branches and companies in all of these are **fictional** — see
[`poc/fixtures/README.md`](../../../../poc/fixtures/README.md). Only the ZIPs, county FIPS and
coordinates are real, so territory routing can be tested.
