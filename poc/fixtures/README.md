# Test fixtures

All companies, people, phone numbers, domains and records here are **fictional**. Domains use the reserved `.example` TLD, and phone numbers use the 555-01xx fictional range. City coordinates and ZIP codes are real, so geography logic can be tested.

| File | Used in | What it contains |
|---|---|---|
| `sample-search-profile.json` | C1, C4, C6, C9 | The Houston lifts profile; valid against `../schemas/search-profile.schema.json` |
| `sample-places.csv` | C4, C5, C6 | **115 rows** — 112 around Houston in the ten CBSA counties, 3 in Beaumont (Jefferson 48245, outside the CBSA, so exclusion is provable). Rebuilt in C4 from real Overture data: every `taxonomy_primary`, `taxonomy_hierarchy` and `basic_category` is a genuine value from the Texas dataset, and the columns follow the real Overture shape (`websites`/`phones` as `\|`-separated lists, `freeform`/`locality`/`region`/`postcode`/`country`, `region` = `TX`, some ZIP+4 postcodes). Names, domains and phone numbers stay fictional. `fixture_note` explains each special row: the three duplicate pairs, suppression targets, keyword-only matches, a keyword false positive, low-confidence rows, noise categories, a row with no website, a row with no `freeform`, and a deliberately long name. Expected arithmetic: **84 match the profile → 81 after exclusions → 78 leads, 3 duplicates** |
| `dealers.csv` | C5 | 3 fictional dealers, one branch each, with coordinates |
| `territories.csv` | C5 | County-level defaults for the 9 Houston CBSA counties + ZIP-level overrides (east Harris → Bayport, north Harris → Pineland). Jefferson County (Beaumont) is deliberately **not** covered, to test coverage gaps |
| `suppression.csv` | C5 | Dealers, customers, a do-not-contact entry and a competitor |
| `sample-research-valid.json` / `sample-research-invalid.json` | C6 | One valid research record; six invalid cases with the expected failure |
| `sample-leads.json` | C8, C10, C11, C13 | The 12 leads from the mockup, with dealer, score, tier, signals and personal lines. Two have long names that trigger headline overflow |
| `sample-postcard-spec.json` | C10, C11 | "Bold hero" spec using the `hero-bold-left` and `letter-address` layouts |
| `brand-kit/` | C1 (seed), C10 | Placeholder brand, logo, product cutout (SVG) and a **screen-only** asset for license-blocking tests; fonts are added in C10 |
| `warranty-sample.csv` | C13 | 7 registrations. Assume mail date 2026-10-15 and code `K7Q3MX` for L0001. Expected: exact 1, strong 2, fuzzy 1, 1 ignored (before mail date), 2 no match (`fixture_expectation` column) |

## Things to fix up during implementation

- **Overture category names** in `sample-places.csv` and `sample-search-profile.json` are placeholders. In **C4**, after inspecting the real taxonomy (`DESCRIBE` + category counts for TX), replace them with real values and record the mapping in the decisions log.
- **`county_fips`** in `sample-places.csv` is the intended county. Points were jittered up to ~3 km, so a few may fall across a county line in the real boundary file. In **C4**, compare it with the spatial join and **move the point** (not the expected result) where they disagree.
- Build `tests/Fixtures/places/sample_places.parquet` from the CSV with DuckDB (`ST_Point(lon, lat)` as geometry, a `taxonomy` struct from the pipe-separated hierarchy). Commit the script and the Parquet file.

## Workbook fixtures created by hand (chunk C8)

The import must handle files re-saved by other apps, which tests can't produce automatically. Create these once and commit them under `src/tests/ProspectStudio.Infrastructure.Tests/Fixtures/workbooks/`:

1. Export the fixture campaign's `leads.xlsx` (12 sample leads).
2. **Google Sheets:** open it from Google Drive and edit it *as .xlsx* (don't convert it). Set L0005 → Approve, L0007 → Reject, L0011 → `approved` (lower case), and L0006's Dealer → `Bayport Aerial Supply`. Add the note "call first" on L0002. Drag the `Notes` column to a different position. Add a new column "My score". File → Download → Microsoft Excel. Save as `leads_saved_by_google_sheets.xlsx`, and the same edits exported via File → Download → CSV as `leads_saved_by_google_sheets.csv`.
3. **LibreOffice** (optional): make the same edits and save as `leads_saved_by_libreoffice.xlsx`.
4. Expected import result for each: approved +2, rejected +1, dealer overrides 1, notes 1, unknown columns ignored, no errors.
