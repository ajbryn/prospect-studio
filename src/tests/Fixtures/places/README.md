# Overture Places test fixtures

Everything chunk C4's candidate search is tested against. No test downloads anything: the Parquet is
committed, and so is the script that builds it.

```pwsh
cd src/tests/Fixtures/places
dotnet run build-places-fixture.cs      # rebuilds sample_places.parquet from poc/fixtures/sample-places.csv
```

| File | What |
|---|---|
| `sample_places.parquet` | 115 places in the real Overture Places schema, built from [`poc/fixtures/sample-places.csv`](../../../../poc/fixtures/sample-places.csv) |
| `build-places-fixture.cs` | The committed DuckDB builder (a .NET 10 file-based app) |
| `tx_taxonomy_primary.csv` | The **real** `(taxonomy.primary, basic_category, hierarchy)` triples and Texas row counts for every category the fixtures and `sample-search-profile.json` use, read off release `2026-09-23.1`. `PlacesFixtureIntegrityTests` checks every fixture category against it, so an invented value cannot creep back in |
| `stac-catalog.json` | A recorded `https://stac.overturemaps.org/catalog.json` response, for the release-discovery test |

All company names, domains (`.example`), phone numbers (`555-01xx`) and street addresses are
**fictional** — only the taxonomy values, the address/website/phone *shapes* and the geography are
real. The fictional identities are deliberate (`poc/fixtures/README.md`); the real taxonomy is equally
deliberate, because the previous fixture's invented categories matched the profile's invented
categories perfectly and real data never.

## Schema (the single most important property)

```
id              VARCHAR
name            VARCHAR            -- technical-design §6.1 aliases names.primary AS name
basic_category  VARCHAR            -- a coarser rollup, often NOT the leaf
taxonomy        STRUCT("primary" VARCHAR, hierarchy VARCHAR[], alternates VARCHAR[])
confidence      DOUBLE
websites        VARCHAR[]
phones          VARCHAR[]
addresses       STRUCT(freeform, locality, postcode, region, country)[]
geometry        GEOMETRY('OGC:CRS84')
bbox            STRUCT(xmin, xmax, ymin, ymax)
```

There is **no `categories` column** and no `county_fips` column — Overture has neither, so a query
that reached for one would compile against the fixture and fail against real data. The expected
county is in the CSV, which the tests read separately.

**The CRS pairing is the point.** This fixture is `OGC:CRS84` and `../geo/counties_houston.parquet` is
`EPSG:4269`, exactly as the real files are, so `ST_Within` across them raises

> Binder Error: Cannot call function 'ST_Within' with geometries of different coordinate reference
> systems (CRS).

until `ST_SetCRS(c.geometry, 'OGC:CRS84')` aligns the **county** side (technical-design §6.3).
`PlacesFixtureIntegrityTests` asserts both halves — that the aligned join works *and* that the
unaligned one still throws — because a fixture that quietly lost the CRS would make every candidate
test pass while the real query threw.

## Facts the tests rely on

- **115 rows**: 112 inside the ten Houston CBSA counties, **3 in Jefferson County 48245** (Beaumont),
  which is in CBSA 13140 and must be excluded by a Houston scope.
- Every row's point falls inside the `county_fips` it declares, checked against the committed
  simplified county geometry. Ten of the original jittered points did not and were **moved** (the
  rule in `poc/fixtures/README.md` is to move the point, not the expectation).
- Counties present: 48015 ×3, 48039 ×3, 48071 ×9, 48157 ×12, 48167 ×5, 48201 ×68, 48339 ×9,
  48473 ×3, 48245 ×3. There is nothing in Liberty 48291 or San Jacinto 48407.
- With `sample-search-profile.json` (ten-county Houston scope, `minConfidence` 0.6, its 14 categories
  and 17 keywords): **84** rows match on category or keyword; the profile's
  `exclusions.overtureCategories` drops two (`fx_0075`, `fx_0114`) and its `exclusions.keywords` drops
  one (`fx_0115`), leaving **81**; dedupe then collapses three duplicates, leaving **78** leads.
- `byCategory` over those 78 leads: `warehouse` 15, `electrician` 8, `distribution_service` 7,
  `metal_fabricator` 6, `manufacturer` 6, `hvac_service` 6, `freight_and_cargo_service` 6,
  `property_management` 5, `sign_making` 5, `glass_and_mirror_sales_service` 4,
  `industrial_equipment_manufacturer` 3, `contractor` 2, `machine_shop` 2, `motor_freight_trucking` 2,
  `steel_fabricator` 1.

## Why the profile cannot name `storage_facility`

**Do not add `storage_facility` back to `sample-search-profile.json` for the apparent coverage.** It is
the *parent* of `self_storage_facility` — the real hierarchy is
`[services_and_business, storage_facility, self_storage_facility]` — so naming it drags all 6,301 Texas
consumer self-storage businesses in as aerial-lift prospects. The category filter matches interior
hierarchy nodes as well as leaves (that is what `fx_0106` exists to prove), and this is the cost of
that: a broad category takes every child with it. `technical-design.md` §6.3 makes the same point
generally — "prefer specific `taxonomy.primary` values over broad ones".

The two fixture rows that used to be `storage_facility` are industrial warehousing rather than consumer
self-storage, so both are now plain `warehouse`:

| Row | Was | Is | Why it matters |
|---|---|---|---|
| `fx_0012` Harris Ridge Cold Storage | `storage_facility` | `warehouse` | A mockup warehousing lead; industrial cold storage |
| `fx_0111` Chambers County Industrial Storage | `storage_facility` | `warehouse` | **The only row covering a missing `freeform` address.** When the profile dropped `storage_facility` this row silently stopped matching, and the extraction path stopped being tested while the suite stayed green |

## Which row proves which property

| Row(s) | Property |
|---|---|
| `fx_0101`, `fx_0102`, `fx_0103` | Beaumont / **Jefferson 48245** — a Houston CBSA scope must exclude them |
| `fx_0076`–`fx_0080` | `confidence` **below 0.6** (0.36–0.50), excluded by the threshold |
| `fx_0001` + `fx_0013` | **Dedupe by domain only**: same `bayoufulfillment.example`, different names (`bayou fulfillment` vs `brazos way fulfillment`, Jaro-Winkler 0.85) and **2.2 km** apart, so no other rule can collapse them. `fx_0001` (0.95) survives `fx_0013` (0.71) |
| `fx_0007` + `fx_0014` | **Dedupe by name + proximity**: identical `name_norm` `westpark metal fab`, **15 m** apart, and `fx_0014` has no website so the domain key cannot fire. Note the two points land in **different geohash-7 cells** (`9vk11mq` / `9vk11mw`) although they are 15 m apart, so §7.2's rule 2 misses this pair and rule 3 (Jaro-Winkler ≥ 0.92 within 200 m) is what catches it — §7.2 now records that rule 2 is a blocking key rather than a guarantee. `fx_0007` (0.90) survives `fx_0014` (0.69) |
| `fx_0025` + `fx_0113` | **Dedupe by name + geohash-7**: identical `name_norm` `pecan storage and distribution`, same cell `9vk5s6h`, 88 m apart, neither has a website. This is the pair §7.2's rule 2 must catch on its own. `fx_0025` (0.82) survives `fx_0113` (0.70) |
| `fx_0104` + `fx_0105` | **Generic host must be ignored**: both websites are on `facebook.com`, and they are different companies 25 km apart — they must **not** merge |
| `fx_0017` + `fx_0018` | Same `name_norm` `coastal crane and rigging` but **1.4 km** apart — beyond the 200 m guard, so **not** duplicates (they are C5 suppression targets instead) |
| `fx_0032` + `fx_0045` | **115 m** apart with unrelated names (Jaro-Winkler 0.62) — proximity alone must not merge anything |
| `fx_0065` + `fx_0066` | `summit signs` / `summit sign` — Jaro-Winkler **0.98** but ~60 km apart, so **not** duplicates |
| `fx_0097` + `fx_0100` | Identical `name_norm` `sabine auto repair` 55 km apart — **not** duplicates |
| `fx_0005`, `fx_0074` | **Keyword-only** matches (`racking` in the name); their category `contractor` is in no segment |
| `fx_0075` | **Keyword false positive**: `Warehouse Grill & Bar` matches the keyword `warehouse`, and its category `restaurant` is in the profile's `exclusions.overtureCategories` |
| `fx_0106` | **Hierarchy-only category match**: `taxonomy.primary` is `steel_fabricator`, which is in no segment, but its hierarchy holds `metal_fabricator`, which is. Only `list_has_any(taxonomy.hierarchy, …)` finds it |
| `fx_0107`, `fx_0108` | Target categories reached through `taxonomy.primary` (`motor_freight_trucking`, `industrial_equipment_manufacturer`) |
| `fx_0109` | An **excluded** category (`truck_rental_service`) that no keyword rescues |
| `fx_0114` | **Excluded category after a keyword hit**: `Northside Racking & Tool Rental` matches the keyword `racking`, and `machine_and_tool_rental` is excluded. It replaces the exclusion coverage `fx_0015`/`fx_0016`/`fx_0020` used to carry before they were moved into target categories |
| `fx_0115` | **Excluded keyword after a category hit**: `Coastal Equipment Rental Services` is a `distribution_service`, a target category, and its name holds the profile's excluded keyword `equipment rental`. It is the only row that exercises `exclusions.keywords` at all |
| `fx_0110` | **First-of-list extraction**: two `websites`, two `phones` (the first with a leading country `1`), and a **ZIP+4** postcode `77032-2514` |
| `fx_0065` | A **ZIP+4** postcode on a ZIP that C5 routes on (`77504-1877`), so truncating to five digits is load-bearing rather than cosmetic |
| `fx_0021`, `fx_0049` | Further **ZIP+4** postcodes |
| `fx_0012` | A mockup warehousing lead, `warehouse` since C4's manual check — see the `storage_facility` note above |
| `fx_0045` | **No `freeform`** and **no website** — 99,004 real Texas rows have no freeform |
| `fx_0111` | **No `freeform`**, but locality and postcode present, so nothing else may be lost with it. `warehouse`, not `storage_facility` — see the note above |
| `fx_0029`, and 22 rows in all | **No website** |
| `fx_0112` | `The Waller County Industrial Park Facilities Management Company, LLC` — 68 characters for the postcard overflow checks, and `The …`/`Company, LLC` for the name normalizer |
| `fx_0002` | `Gulf Coast Sign & Lighting` → `gulf coast sign and lighting`, the `&`→`and` normalizer case §7.1 names |
| `fx_0081`–`fx_0100` | Noise in categories no segment asks for: `cafe`, `dental_clinic`, `beauty_salon`, `christian_place_of_worship`, `elementary_school`, `automotive_repair` |

**Transitive dedupe** (§7.2 now says matches are edges and groups are connected components) is tested
with synthetic rows in `CandidateDedupeTests`, not with fixture rows: a three-row chain needs one
domain edge and one name edge that do not touch, and wiring that into the committed set would have
changed the lead arithmetic above for no gain.
| `fx_0015`, `fx_0016`, `fx_0020` | The two dealers and the competitor, **in target categories** (`industrial_equipment_manufacturer`, `distribution_service`, `industrial_equipment_manufacturer`) so C5 can prove suppression removes them. They were originally `machine_and_tool_rental`, which the profile **excludes** — so a profile-driven search never surfaced them and C5's suppression test could not have failed. A fixture that quietly guarantees a green test is worse than no fixture; `PlacesFixtureIntegrityTests` now asserts all three stay searchable |

## The `[tag]` on every load-bearing note

Each row whose job is to exercise a search path carries a machine-readable tag at the front of
`fixture_note`, and `PlacesFixtureIntegrityTests` checks every one of them against the live profile:

| Tag | Means | Count |
|---|---|---|
| `[candidate]` | The profile selects it and its exclusions keep it, so it reaches the campaign | 26 |
| `[excluded]` | The profile selects it and then an exclusion list removes it | 3 |
| `[unmatched]` | The profile must **not** select it — out of scope, below the confidence floor, or in no segment | 9 |

Untagged rows are ordinary filler and noise; nothing asserts on them.

This exists because a profile edit can silently disarm a row, and it did three times during C4: the
dealer rows sat in an excluded category so C5's suppression would have had nothing to remove,
`machine_and_tool_rental` was left with no rows at all, and dropping `storage_facility` disarmed
`fx_0111`. Every one of those left the suite green while testing one thing less.

`Every_documented_row_still_plays_the_part_its_note_claims` names the row, the category and the reason
the moment that happens. `Every_behaviour_the_fixture_claims_to_cover_has_at_least_one_row` stops the
first test passing vacuously because the rows were deleted instead. **Fix the row, not the tag** — the
tag is the coverage, so relabelling it to match reality is how the coverage disappears.

## The CSV's columns

`poc/fixtures/sample-places.csv` was rebuilt in C4 to the real Overture shape, so the old
`website` / `phone` / `address` / `city` / `state` / `zip` columns are gone:

| Column | Meaning |
|---|---|
| `id`, `name`, `confidence` | As in Overture |
| `taxonomy_primary`, `taxonomy_hierarchy`, `basic_category` | Real values; the hierarchy is `\|`-separated and its last element is always `taxonomy_primary` |
| `websites`, `phones` | `\|`-separated lists, empty when the place has none; phones are bare digits, sometimes with a leading `1` |
| `freeform`, `locality`, `region`, `postcode`, `country` | The members of one `addresses` struct. `region` is plain `TX`, never `US-TX`; `postcode` is sometimes ZIP+4; `freeform` is sometimes empty |
| `county_fips` | The county the point really falls in, checked by `PlacesFixtureIntegrityTests` |
| `lat`, `lon` | Note `ST_Point` takes **(lon, lat)** |
| `fixture_note` | Which property this row exists to prove |
