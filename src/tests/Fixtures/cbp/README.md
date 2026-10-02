# Census CBP test fixtures

Recorded response bodies from `https://api.census.gov/data/2023/cbp`, so no C3 test touches the
network. Regenerate with [`build-cbp-fixtures.cs`](build-cbp-fixtures.cs) (a .NET 10 file-based app):

```pwsh
cd src/tests/Fixtures/cbp
dotnet run build-cbp-fixtures.cs                      # copy/trim from spikes/census-cbp/samples
dotnet run build-cbp-fixtures.cs -- --samples D:\cbp  # from another folder of recorded bodies
dotnet run build-cbp-fixtures.cs -- --record          # re-record live; needs CENSUS_API_KEY
```

**No file here contains a key, and none contains a request URL.** The queries are documented below so
a fixture can be reproduced by hand; `--record` appends `&key=$env:CENSUS_API_KEY` to the URI and
writes the response body only.

## The bodies

Every query below is relative to `https://api.census.gov/data` and omits `&key=…`.
`get=…` is always `ESTAB,EMPSZES,EMPSZES_LABEL,NAICS2017`, plus what a row says.

| File | Query | HTTP | What it is for |
|---|---|---|---|
| `houston10_4931.json` | `/2023/cbp?…&for=county:015,039,071,157,167,201,291,339,407,473&in=state:48&NAICS2017=4931` | 200 | The main case. **Suppression by absent rows**, and a nested detail band (`263` inside `260`) |
| `houston10_238210.json` | same counties, `NAICS2017=238210` | 200 | The second, disjoint NAICS branch; all ten counties answer; **no** detail bands |
| `harris_4931.json` | `…&for=county:201&in=state:48&NAICS2017=4931` | 200 | Parent code, one county, fully banded |
| `harris_49311.json` | `…&NAICS2017=49311` | 200 | The descendant of 4931, to prove the overlap is dropped and not added |
| `harris_238210.json` | `…&NAICS2017=238210` | 200 | A **complete** county: bands sum exactly to `001`, so "no suppression note" is testable |
| `harris_00_allbands.json` | `…&for=county:201&in=state:48&NAICS2017=00` | 200 | **The body that settles the nesting rule.** Carries `262`, `263`, `271` and `273` alongside `260`, two of them open-ended, and its naive band sum *exceeds* `001` |
| `tx_counties_4931_duplicate_column.json` | `…&for=county:157,201,473&in=state:48&NAICS2017=4931&EMPSZES=001` | 200 | Filtering on `EMPSZES` **and** naming it in `get=` duplicates the header column, at index 5 rather than beside the first one |
| `tx_counties_238210_with_flags.json` | `get=…,EMP,ESTAB_F,EMP_F&for=county:*&in=state:48&NAICS2017=238210` | 200 | `EMP` is `"0"` with `EMP_F` `"N"` on band rows, and `ESTAB_F`/`EMP_F` arrive as JSON `null`, not strings |
| `cross_state_error.txt` | `…&for=county:201,001&in=state:48,22&NAICS2017=4931` | **400** | `error: wildcard mismatch in geography hierarchy` — why there is one request per state |
| `not_found_404.html` | `/2024/cbp/variables.json` | **404** | The Tomcat 404 page. The same body comes back for a *keyed* data query against a missing year |
| `cbp_variables_2023.json` | `/2023/cbp/variables.json` | 200 | The year probe's success response, **trimmed** (see below) |

## The one derived file

`cbp_variables_2023.json` is the real metadata document cut down from 540 KB:

- only the variables the client reads — `for`, `in`, `ucgid`, `ESTAB`, `EMP`, `EMPSZES`, `NAICS2017`,
  `YEAR` — are kept;
- `NAICS2017.values.item` keeps 6 of its 6,694 codes (the ones the tests use). The real object
  **repeats some keys** (`111` appears twice), which is why the generator writes it with
  `Utf8JsonWriter` instead of `JsonNode`;
- nothing else is edited.

Two things it is kept for: `EMPSZES` has **no `values` list** (only `attributes: EMPSZES_LABEL`), so the
band set cannot be learned from the metadata and has to be read off each data response; and the NAICS
variable is **`NAICS2017`**, with no `NAICS2022` anywhere.

## The band rule these fixtures exist to pin

All verified against the live API (see the C3 rows in the decisions table of
`poc/implementation-plan.md`). Every value in a data response is a **string**, in an array-of-arrays
with a header row, and `state` and `county` are the trailing columns.

Nine bands **partition** the total exactly:

| Code | Range | | Code | Range |
|---|---|---|---|---|
| `210` | under 5 | | `251` | 100–249 |
| `220` | 5–9 | | `252` | 250–499 |
| `230` | 10–19 | | `254` | 500–999 |
| `241` | 20–49 | | `260` | 1,000 and over |
| `242` | 50–99 | | | |

A response may *also* carry **detail bands that subdivide `260`** — `262` (1,000–1,499),
`263` (1,500–2,499), `271` (2,500–4,999), `273` (5,000+). Adding those to `260` counts the same
establishments twice. So: parse `[lower, upper]` from each `EMPSZES_LABEL`, **discard any band whose
range is contained within another band's range**, then take every surviving band whose lower bound is at
or above the threshold. Which detail bands appear varies by query, so no band list may be hard-coded.

## Facts the tests rely on

### `harris_00_allbands.json` — Harris County, all sectors

The arithmetic that defines the rule, in one body:

| | |
|---|---|
| `001` All establishments | **111,215** |
| the nine standard bands (`210`…`260`) | **111,215** — they partition the total exactly |
| every non-`001` row added naively | **111,350** — **135 too many** |
| `260` "1,000 employees or more" | **135** |
| `262` 51 + `263` 51 + `271` 21 + `273` 12 | **135** — exactly `260`, which it subdivides |

So `260`'s label is **accurate**, not misleading. Two of the bands here — `260` (1,000+) and `273`
(5,000+) — are **both open-ended**, so containment cannot be decided by "has an upper bound": a missing
upper bound is infinity, and `273` is still inside `260`. And `262` shares `260`'s lower bound of 1,000,
differing only in its upper bound, so the **wider** band is the one that has to survive.

A naive sum here *exceeds* `001`, which is impossible for disjoint bands. That is the one signal that
catches this class of bug, and the reason a band gap must never be clamped to zero.

Its header is `["ESTAB","EMPSZES","EMPSZES_LABEL","NAICS2017","NAICS2017","state","county"]`:
**`NAICS2017` appears twice**, because it was named in `get=` as well as filtered on.

### `houston10_4931.json` — NAICS 4931, ten counties requested

- **Eight** counties come back. 48291 (Liberty) and 48407 (San Jacinto) are **absent entirely**, which
  means *unknown*, never zero.
- `001` totals **462**; the nine standard bands total **439**, so **23** establishments have no
  published band.
- `withMinEmployees: 20` is **152** — bands `241`, `242`, `251`, `252`, `254` and `260`.
  `withMinEmployees: 50` is **78**.
- County 48157 publishes `263` = 3 **alongside** `260` = 3. Adding both counts those 3 twice, which is
  exactly what turns 439 into 442, 23 into 20 and 152 into 155. All three wrong numbers look plausible.
- 48157 is the trap in miniature: its naive band sum is **32**, precisely its `001` of 32, so a naive
  sum makes a county that is really 3 short look complete.
- Per county, `001` against the **standard** bands: 48015 3/**0**, 48039 12/9, 48071 21/19,
  48157 32/29, 48167 9/6, 48201 360/360, 48339 17/13, 48473 8/3.
- Bands present: `001`, `210`, `220`, `230`, `241`, `242`, `251`, `252`, `254`, `260`, `263`.

### `houston10_238210.json` — NAICS 238210, same ten counties

- All ten counties answer. `001` totals **1310**, standard bands **1293** (a shortfall of 17).
- `withMinEmployees: 20` is **217**; `withMinEmployees: 50` is **102**.
- 48071 reports 4 establishments and **no band rows at all**; this response carries **no** detail bands,
  which is why one worked example cannot validate the nesting rule.

### Harris County (48201) alone

| NAICS | `001` | standard bands | ≥ 20 employees |
|---|---|---|---|
| `4931` | 360 | 360 | 126 |
| `49311` = `493110` | 256 | 256 | 86 |
| `23821` = `238210` | 833 | 833 | 182 |
| `00` | 111,215 | 111,215 | 18,897 |

A NAICS code already includes its descendants, so `4931` + `49311` double-counts 256 and
`4931` + `238210` does not overlap at all.
