# MCP Tool Contracts: `prospect-studio` server

**Version:** 0.1 · 2026-09-29

Conventions:
- Tool names are `snake_case`; parameters and fields are `camelCase` JSON.
- Every tool description (the `[Description]` text) says **when** to use it and what it returns, in one or two sentences. Claude picks tools from those descriptions.
- Default responses stay under ~4,000 tokens. Lists page with `limit` / `offset` and always return `total`.
- Long-running tools return `{ "jobId": "...", "status": "queued" }`. Poll with `get_job`.
- The **Chunk** column shows when a tool first appears. Tools may gain fields in later chunks, but must never break earlier ones.

---

## Summary

| Group | Tool | Purpose | Chunk |
|---|---|---|---|
| Setup | `get_status` | Server version, paths, data readiness, configured keys | C0 (+C2, C4) |
| Setup | `prepare_data` | Download reference data; extract Overture places for states (job) | C2 (+C4) |
| Jobs | `get_job`, `list_jobs`, `cancel_job` | Background job control | C2 |
| Campaign | `create_campaign`, `list_campaigns`, `get_campaign` | Campaign lifecycle | C1 |
| Campaign | `save_search_profile` | Validate and store the search profile | C1 |
| Lookup | `lookup_naics` | NAICS codes by keyword | C2 |
| Lookup | `resolve_geography` | Text/structured area → GeoScope | C2 |
| Lookup | `lookup_overture_categories` | Overture taxonomy categories by keyword, with counts | C4 |
| Market | `estimate_market` | Census CBP establishment counts | C3 |
| Lists | `import_list`, `list_dealers` | Dealers, territories, suppression, warranty import | C5 (+C13) |
| Candidates | `find_candidates` | Search the local extract, dedupe, store | C4 (+C5) |
| Candidates | `assign_dealers`, `apply_suppression` | Routing and suppression (re-runnable) | C5 |
| Leads | `list_leads`, `get_lead`, `update_leads` | Browse and edit leads | C6 |
| Leads | `score_leads`, `save_research` | Scoring and Claude's research | C6 |
| Enrichment | `prefetch_websites` | Fetch websites, derive features (job) | C7 |
| Workbook | `export_leads_workbook`, `import_leads_workbook` | Spreadsheet round-trip (.xlsx/.csv; Google Sheets compatible) | C8 |
| Workbook | `connect_google`, `publish_leads_sheet`, `pull_leads_sheet` | Native Google Sheets (optional) | C8b |
| Postcards | `get_brand_kit`, `list_layouts`, `render_postcard_preview` | Design loop | C10 |
| Postcards | `fetch_street_image` | Street View / Mapillary site photo, screen-only reference | C10 |
| Production | `save_template`, `list_templates`, `get_template`, `set_lead_overrides` | Templates and per-lead tweaks | C11 |
| Production | `assign_tracking_codes`, `render_campaign`, `build_dealer_packets`, `export_mailing_manifest` | Batch outputs | C11 |
| Measure | `assign_cohorts`, `run_matchback` | Measurement groundwork | C13 |

---

## Setup & jobs

### `get_status`
Input: `{}`
Output:
```json
{ "version": "0.1.0", "home": "C:\\Users\\a\\Documents\\Prospect Studio", "data": "C:\\Users\\a\\AppData\\Local\\ProspectStudio",
  "ready": { "referenceData": true, "overture": { "release": "2026-09-23.1", "states": ["TX"] }, "brandKit": true, "dealers": 3, "territories": 64, "suppression": 7 },
  "keys": { "census": false, "openai": false, "gemini": false, "googleMaps": false, "hubspot": false },
  "trackingBaseUrl": "https://example.com/lp?code={code}",
  "warnings": ["No CENSUS_API_KEY: estimate_market cannot run. Get a free key at https://api.census.gov/data/key_signup.html"] }
```

### `prepare_data` (job)
Input: `{ "states": ["TX"], "force": false }` → `{ "jobId": "job_ab12cd", "status": "queued" }`.
Steps: counties → CBSA → ZCTA → Overture extract per state. Skips completed steps unless `force`. (NAICS needs no step — the table is embedded in the assembly.)

`states` is validated against the US state list; a bogus code is `VALIDATION_FAILED`. **Until C4, `states` affects nothing** — the reference files are national and only the Overture extract is per-state — so the job message says so rather than implying state data was prepared.

Errors: **`JOB_RUNNING`** when a `prepare_data` job is already active (hint names it). Two concurrent runs would write the same `counties.parquet` and leave a corrupt file that completeness checks then report as done.

### `get_job` / `list_jobs` / `cancel_job`
`get_job { jobId }` →
```json
{ "jobId": "job_ab12cd", "kind": "prefetch_websites", "status": "running", "progress": 0.42,
  "message": "Fetched 336/800 sites (12 blocked by robots.txt)", "result": null }
```
`list_jobs { campaignId?, status? }` → `{ "jobs": [...] }`, newest first.

`cancel_job { jobId }` → `{ "status": "cancelled" }`. On a job that has **already finished** it is **idempotent and returns that job's actual terminal status** (`succeeded`, `failed`, `interrupted`) rather than erroring — a skill polling a job it just asked to cancel shouldn't have to handle a race as an exception. Unknown `jobId` → `NOT_FOUND`.

**On server start, both `running` and `queued` rows become `interrupted`.** The queue lives in memory, so after a restart a `queued` row has nothing left to run it and would otherwise claim `queued` forever. technical-design §8 mentions only the `running` transition; this closes that gap. Every job is safe to re-run, so the recovery is always "run it again".

## Campaign

### `create_campaign`
Input: `{ "name": "Houston Scissor & Boom Lifts Q4", "product": "Scissor & boom lifts", "notes": "" }`
Output: `{ "campaignId": "cmp_7Q3KXM", "folder": "...\\Campaigns\\2026-10 Houston Scissor & Boom Lifts Q4" }`

`campaignId` is `cmp_` plus 6 characters from the same unambiguous alphabet as tracking codes (`23456789ABCDEFGHJKLMNPQRSTUVWXYZ`). The folder is `yyyy-MM <Name>` under `Campaigns\`, with characters Windows forbids removed. Duplicate detection is on the normalized slug and is **case-insensitive**, so "Houston Test" and "houston test" collide → `CONFLICT`.

### `list_campaigns`
Input: `{ "limit": 100, "offset": 0 }` (both optional)
Output:
```json
{ "total": 3, "campaigns": [
  { "campaignId": "cmp_7Q3KXM", "name": "Houston Scissor & Boom Lifts Q4", "folder": "...",
    "status": "draft", "product": "Scissor & boom lifts", "createdAt": "2026-09-30T14:02:11Z", "leads": 0 } ] }
```

### `get_campaign`
Input: `{ "campaignId": "..." }`
Output:
```json
{ "campaignId": "cmp_7Q3KXM", "name": "Houston Scissor & Boom Lifts Q4", "folder": "...",
  "status": "draft", "product": "Scissor & boom lifts",
  "profile": { "name": "…", "segments": 4, "geographyQuery": "Houston metro", "savedAt": "…" },
  "geoLabel": null,
  "counts": { "byStatus": {}, "byTier": {}, "byDealer": [] },
  "lastExportAt": null, "lastRenderAt": null }
```
`profile` is `null` until `save_search_profile` runs. **`geoLabel` stays `null` until C4** — it is read from the campaign's stored `geo_json`, which `find_candidates` is the first thing to write. `resolve_geography` exists from C2, but `save_search_profile` deliberately does not call it: that would make saving a profile fail when reference data is missing, for a display-only field. Until then the raw query is available under `profile.geographyQuery`. Counts are empty objects/arrays for a new campaign, not absent. Errors: `NOT_FOUND`.

### `save_search_profile`
Input: `{ "campaignId": "...", "profile": { /* schemas/search-profile.schema.json */ } }`
Output: `{ "saved": true, "path": ".../search-profile.json", "warnings": ["Segment 'Facilities' has no overtureCategories; keywords only"] }`
Errors: `VALIDATION_FAILED`, with `details[]` **inside the error object**, each entry `{ "pointer": "<RFC 6901 JSON pointer>", "message": "…" }`:

```json
{ "error": { "code": "VALIDATION_FAILED", "message": "The search profile is not valid.",
  "hint": "Fix the 2 problems listed in details and call save_search_profile again.",
  "details": [ { "pointer": "/segments/0/naics/0", "message": "'23A' is not a valid NAICS code." } ] } }
```

`details` is omitted entirely when there is nothing to report, so the envelope stays `{code, message, hint}` for every other error. A rejected save must leave any previously stored profile untouched.

**Only `pointer` is contractual; `message` is advisory** — message wording may change freely and must not be asserted on or parsed. Pointer rules:

| Case | Pointer | Note |
|---|---|---|
| Missing required property | the property itself (`/segments`) | Synthesize it: JSON Schema evaluation natively reports the *parent*, which is useless to the caller |
| Failed `anyOf` / `oneOf` alternatives | **one** detail at the parent (`/geography`) | Never one per branch. Two details each saying "required" reads as two requirements, and the caller would satisfy both when the schema wants exactly one |
| `scoringWeights` not summing to 1.0 ± 0.001 | `/scoringWeights` | A **code-level** rule, not in the schema, which only requires the six keys exist |
| Anything else | the offending value's own location | e.g. `/segments/0/naics/0` |

No detail may point at a value that is actually valid.

Also returns `FILE_LOCKED` when `search-profile.json` is held open by another application, with the hint naming the path. Detected by probing whether the existing file can be opened exclusively, not by inspecting platform error codes — a write that failed for any other reason (disk full, permissions) stays `INTERNAL`. A locked file leaves the previously stored profile intact, `savedAt` included.

## Lookups

### `lookup_naics`
Input: `{ "query": "electrical contractor", "limit": 10 }`
Output: `{ "results": [ { "code": "238210", "title": "Electrical Contractors and Other Wiring Installation Contractors", "level": 6 } ] }`

### `resolve_geography`
Input (any one form):
```json
{ "query": "Houston metro" }
{ "type": "counties", "values": ["Harris County, TX", "Fort Bend County, TX"] }
{ "type": "zips", "values": ["77494", "77449"] }
{ "type": "radius", "center": { "address": "1200 Main St, Houston, TX" }, "radiusMiles": 25 }
{ "type": "dealer", "values": ["gulf"] }
```
Output: a `GeoScope` (see [technical-design §6.2](technical-design.md#62-geography-resolution)), **flat**, with `alternatives[]` as a sibling key present only when the query was ambiguous:

```json
{ "type": "cbsa", "label": "Houston-Pasadena-The Woodlands, TX", "cbsa": "26420",
  "states": ["TX"], "countyFips": ["48015","…","48473"], "zips": [], "radius": null,
  "bbox": [-96.6, 28.8, -94.3, 30.7] }
```

- **Every `GeoScope` key is always present**, using `[]` or `null` where it doesn't apply — same principle as `get_campaign`'s counts, so a caller never has to distinguish "absent" from "empty". `alternatives` is the one exception, omitted when empty, like `details[]` on errors.
- `radius` is `{ "lat": …, "lon": …, "miles": … }` when `type` is `radius`, matching `$defs.geoScope` in [`schemas/search-profile.schema.json`](schemas/search-profile.schema.json), which is the authoritative shape.
- **Multi-value queries union, they never discard.** `{type:"counties"|"zips"|"cbsa", values:[…]}` with more than one value returns the **union** of all of them (label e.g. "2 metro areas"). `alternatives` is only for a genuinely ambiguous **single** value. Silently dropping something the caller explicitly asked for is worse than refusing it.
- **`warnings[]`**, a sibling key omitted when empty, names input that resolved to nothing — unresolvable ZIPs, for instance. A mostly-good list of fifty ZIPs is not blocked by two typos, but what was dropped is always reported.
- `alternatives[]` entries are `{ "type", "label", "cbsa", "countyFips" }`.
- Errors: **`NOT_FOUND`** when a place name matches nothing (hint: suggest a more specific form such as "Harris County, TX"); `VALIDATION_FAILED` when no recognizable input was supplied at all; `NOT_READY` when reference data is missing (hint: run `prepare_data`); `UNSUPPORTED` for `type: "dealer"` until C5; `EXTERNAL_API` when the Census geocoder fails.
- Input is **lenient on purpose**: a `type` with no `values` falls back to reading `query` as that type, and `county`, `states`, `zip`, `zipcode`, `zipcodes`, `zip_codes`, `metro` and `msa` are accepted as aliases. The enum in `$defs.geoScope` remains authoritative for *output*.
- Type `dealer` arrives in C5.

### `lookup_overture_categories`
Input: `{ "query": "warehouse", "state": "TX", "limit": 15 }`
Output: `{ "results": [ { "category": "warehouse", "path": ["...","warehouse"], "countInState": 5123 } ] }`

## Market

### `estimate_market`
Input: `{ "naics": ["4931","238210"], "geo": { "query": "Houston metro" }, "minEmployees": 20 }` (or `"geo": <GeoScope>`)

`naics` and `geo` are **required** — unlike `find_candidates`, this tool takes no `campaignId`, so there is no profile to default the geography from. `minEmployees` is optional; when it is absent `withMinEmployees` is **`null`** (present but null, like every other nullable field), not a copy of `establishments`.

Errors: `VALIDATION_FAILED` for a bogus NAICS code, **or for a geography that resolves to no counties** — reporting a market of zero for an empty scope is the dangerous reading, so refuse instead. `NOT_READY` when `CENSUS_API_KEY` is absent (hint names the variable), when the key is rejected, or when no CBP year is published — three distinct messages, and the year case must *not* blame the key. `EXTERNAL_API` after retries. Note that error bodies are **not** reliably JSON: the cross-state 400 returns plain text.
Output (**real verified 2023 figures** for the ten-county Houston CBSA, not placeholders):
```json
{ "cbpYear": 2023, "geoLabel": "Houston-Pasadena-The Woodlands, TX",
  "byNaics": [
    { "naics": "4931", "title": "Warehousing and Storage", "establishments": 462, "withMinEmployees": 152 },
    { "naics": "238210", "title": "Electrical Contractors and Other Wiring Installation Contractors",
      "establishments": 1310, "withMinEmployees": 217 } ],
  "total": { "establishments": 1772, "withMinEmployees": 369 },
  "notes": ["4931: 23 of 462 establishments have no size band published, and Liberty and San Jacinto counties are absent entirely, so withMinEmployees is a lower bound."] }
```

**Suppression does not look how you would expect, and this drives the whole design.** Census does not blank a cell or use a sentinel — `ESTAB` is never empty, null or negative. Instead **whole rows are simply absent**:
- Band rows go missing while the `001` "All establishments" row remains, so `sum(bands) < 001`. In the example above, **439 of 462** are banded and **23** are unaccounted for; Austin County reports 3 establishments and **no band rows at all**.
- **Entire counties can be missing** from the response. Two of the ten requested came back with nothing — which means *unknown*, never zero.

So always request the `001` row, compute `001_total − bands_sum`, and treat any shortfall or absent county as making `withMinEmployees` a **lower bound**, saying so in `notes` with the size of the gap. `EMP` on a band row comes back `"0"` with `EMP_F = "N"` and is not a real zero — don't read it.

Other contract-shaping facts, all verified against the live API:
- **Never sum the `001` band** — it equals the sum of the others exactly, so including it doubles every count.
- **Some bands are *nested inside* others, and summing both double-counts.** The nine standard bands — `210` (<5), `220`, `230`, `241`, `242`, `251`, `252`, `254`, `260` (1,000+) — partition the total exactly. Alongside them a response may also carry **detail bands that subdivide `260`**: in Harris County / NAICS 00, `262` (1,000–1,499) = 51, `263` (1,500–2,499) = 51, `271` (2,500–4,999) = 21 and `273` (5,000+) = 12 sum to exactly `260` = 135. `260`'s label "1,000 employees or more" is **accurate**, not misleading.
- So the selection rule is: parse `[lower, upper]` from each `EMPSZES_LABEL`, **discard any band whose range is contained within another band's range**, then take every surviving band whose lower bound is ≥ `minEmployees`. Never hard-code a band list — which detail bands appear varies by query (Texas state level publishes none; sixteen Texas counties publish `263`).
- Getting this wrong is silent: including `263` alongside `260` inflates Houston 4931's `withMinEmployees` from 152 to 155, and its suppression shortfall is likewise 23, not 20. Both still look like plausible answers.
- **One request per state.** `for=county:…&in=state:48,22` returns HTTP 400 "wildcard mismatch in geography hierarchy".
- **A NAICS code already includes its descendants** (`4931` = 360 in Harris, `49311` = `493110` = 256). Drop any supplied code that is a prefix-descendant of another supplied code, or the overlap is double-counted. Different branches (`4931` + `238210`) are disjoint and safe to add.
- Values are **all strings**, in an array-of-arrays with a header row; `state` and `county` are trailing columns. Naming a variable in `get=` *and* filtering on it **duplicates that header column** — parse by index and tolerate duplicates, or leave filtered variables out of `get=`.
- With a key, a missing year is a plain **404**; a **302** always means a key problem (`missing_key.html` vs `invalid_key.html`). No rate-limit headers are published, so don't claim a number.
- **A `204 No Content` is indistinguishable between "that NAICS code is not published in this vintage" and "this market is genuinely empty."** The API returns the same empty response for both, so the tool reports zero establishments with every requested county listed as absent, and hedges the total. This is an API limitation, not something code can resolve — don't add a heuristic that guesses which case it is.
- **A `minEmployees` above the top published band cannot be answered.** The highest band is open-ended ("1,000 or more"), so a threshold of 2,000 has no band to sum and `withMinEmployees` is **`null`** with a note naming that top band — never `0`, which would read as "no establishments that large" when such establishments almost certainly exist inside the open band.
- **One tool call is capped at 10 Census requests** (NAICS codes × states, counted after overlapping codes are dropped), because the per-domain rate limit of 1 req/s makes a larger fan-out collide with the 20 s response budget. Exceeding it is `VALIDATION_FAILED` asking for fewer codes or a smaller area — never a silently truncated market.

## Lists

### `import_list`
Input: `{ "kind": "dealers" | "territories" | "suppression" | "warranty", "path": "optional; defaults to the workspace folder file", "reason": "customer" }`
Output: `{ "imported": 64, "updated": 0, "errors": [ { "row": 12, "message": "Unknown dealerId 'gulff'" } ] }`
Formats: see `poc/fixtures/*.csv` headers. XLSX accepted with the same headers on the first sheet.

### `list_dealers`
Output: `{ "dealers": [ { "id": "gulf", "name": "Gulf Lift Equipment", "branches": 1, "territoryRows": 30 } ] }`

## Candidates

### `find_candidates`
Input: `{ "campaignId": "...", "geo": <optional; defaults to profile geography>, "categories": [...], "keywords": [...], "minConfidence": 0.6, "limit": 5000, "replace": false }` (categories/keywords default to the profile's)
Output:
```json
{ "found": 3120, "stored": 2890, "duplicates": 230, "suppressed": { "customer": 14, "dealer": 3, "dnc": 2 },
  "coverageGaps": 41, "byCategory": [ { "category": "warehouse", "count": 640 } ],
  "byDealer": [ { "dealer": "Gulf Lift Equipment", "count": 1510 } ], "sample": [ /* 10 compact leads */ ] }
```
Re-running with the same inputs is idempotent. `replace: true` clears candidates without research first.

- **Exclusions come from the saved search profile** (`exclusions.overtureCategories` and `exclusions.keywords`) and are applied here — §6.3's SQL omitted them and this input list never mentioned them, which left the only consumer of a declared profile field undefined. An optional `excludedCategories` parameter overrides the profile's, the same way `categories` and `keywords` do.
- **`found` = `stored` + `duplicates`**, matching the example's own arithmetic (2890 + 230 = 3120). `suppressed` counts are reported separately per reason and are **not** part of `stored`.
- The first address, website and phone are taken from their lists; a ZIP+4 postcode is truncated to 5 digits; a phone is stored **verbatim** (formats are inconsistent — `7137477411`, `17136884530` — and normalization is not needed until matching in C13).
- `get_campaign.geoLabel` starts being populated here, because this is the first tool to write the campaign's `geo_json`. `get_status.ready.overture` likewise reports the real release and extracted states from C4 onward, rather than the hardcoded `{release: null, states: []}` placeholder C0 shipped.

### `assign_dealers` / `apply_suppression`
Input: `{ "campaignId": "..." }` → counts changed. Manual overrides are preserved.

## Leads

### `list_leads`
Input: `{ "campaignId": "...", "status": ["review","approved"], "tier": ["A","B"], "dealerId": null, "minScore": 0, "researchStatus": null, "sort": "score_desc", "limit": 25, "offset": 0 }`
Output (compact rows):
```json
{ "total": 812, "rows": [
  { "id": "L0001", "name": "Bayou Fulfillment Co.", "city": "Katy", "segment": "Warehousing & 3PL",
    "score": 92, "tier": "A", "dealer": "Gulf Lift", "status": "approved", "research": "saved", "topSignal": "Permit: 180k sq ft addition (2026-07)" } ] }
```

### `get_lead`
Input: `{ "campaignId": "...", "leadId": "L0001", "includeWebExcerpt": false }`
Output: full lead: site fields, provenance, features, `scoreBreakdown[]`, research, signals, dealer/branch, code, cohort, notes. If requested, a web excerpt (≤ 1,500 chars).

### `update_leads`
Input: `{ "campaignId": "...", "updates": [ { "leadId": "L0007", "status": "approved", "dealerId": "bay", "notes": "Call first" } ] }`
Output: `{ "updated": 1, "errors": [] }`

### `score_leads`
Input: `{ "campaignId": "...", "weights": null }` → `{ "scored": 2890, "tiers": { "A": 12, "B": 140, "C": 2738 }, "weights": { ... } }`

### `save_research`
Input: `{ "campaignId": "...", "leadId": "L0001", "research": { /* schemas/research.schema.json */ } }`
Output: `{ "saved": true, "score": 92, "tier": "A", "delta": +17 }`
Validation: every signal has `url` and `date`; `personalLine` ≤ 180 chars; `llmAdjustment` in −15…15; `status: "no_signal"` allowed with an empty `signals`.

## Enrichment

### `prefetch_websites` (job)
Input: `{ "campaignId": "...", "scope": "all" | "top" | "ids", "topN": 800, "leadIds": [], "refetch": false }`
Result (in `get_job.result`): `{ "fetched": 760, "robotsBlocked": 12, "errors": 28, "noWebsite": 204 }`. Features are updated. Run `score_leads` afterwards (or the job triggers it; record which choice you made).

## Workbook

### `export_leads_workbook`
Input: `{ "campaignId": "...", "statuses": ["review","approved","hold","rejected"], "minScore": 0 }`
Output: `{ "path": ".../leads.xlsx", "rows": 812 }`

### `import_leads_workbook`
Input: `{ "campaignId": "...", "path": null }`
Output: `{ "changes": { "status": { "approved": 15, "rejected": 40 }, "dealerOverrides": 2, "notes": 6, "contacts": 3 }, "unknownLeadIds": [], "warnings": [] }`
`path` may point to an `.xlsx` (from any app) or a `.csv` export of the Leads sheet. Columns are matched by header.
Errors: `FILE_LOCKED` ("Close leads.xlsx in the other app and try again").

### `connect_google` / `publish_leads_sheet` / `pull_leads_sheet` (C8b, optional)
`connect_google {}` → opens the browser consent (OAuth desktop flow, scope `drive.file`) → `{ "connected": true, "account": "user@example.com" }`.
`publish_leads_sheet { campaignId, statuses?, minScore? }` → `{ "spreadsheetId": "…", "url": "https://docs.google.com/spreadsheets/d/…", "rows": 812 }` (creates the Sheet or updates it in place).
`pull_leads_sheet { campaignId }` → same output as `import_leads_workbook`.
Errors: `NOT_READY` (not connected / no client secret), `EXTERNAL_API` (Google API error, including an expired token with the hint "Run connect_google again").

## Postcards

### `get_brand_kit`
Output: brand name, colors, fonts (found/missing), logo, products `[ { id, name, image, allowedUses } ]`, default copy (tagline, benefits, incentive, CTA), offer prefix, warnings.

### `list_layouts`
Input: `{ "side": "front" }` → `{ "layouts": [ { "id": "hero-bold-left", "side": "front", "description": "...", "slots": [ { "id": "headline", "type": "text", "maxChars": 60 } ] } ] }`

### `fetch_street_image`
Fetches a street-level photo of a lead's site as **on-screen reference only**, so the user can see the building and judge whether to commission a real photograph.

Input: `{ "campaignId": "...", "leadId": "L0001", "provider": "streetview" | "mapillary" | null, "heading": null }`
Output: **image content** (the photo) plus text JSON:
```json
{ "provider": "google-streetview", "saved": ".../reference/L0001_streetview.jpg",
  "sidecar": ".../reference/L0001_streetview.jpg.asset.json",
  "attribution": "© Google", "allowedUses": ["screen"],
  "coordinates": { "lat": 29.7604, "lon": -95.3698, "heading": 210 },
  "warnings": ["Reference only: this image cannot be rendered onto print or email output."] }
```
`provider` defaults to `PS_IMAGERY_PROVIDER` (default `streetview`), falling back to the other configured provider when the preferred one has no coverage. Errors: `NOT_READY` (no provider key configured), `NOT_FOUND` (unknown lead, or no imagery within range from either provider), `UNSUPPORTED` (`PS_IMAGERY_PROVIDER=none`), `EXTERNAL_API`, `RATE_LIMITED`.

The saved sidecar always carries `allowedUses: ["screen"]`, so [§10.5](technical-design.md#105-qa-checks-run-in-the-page-via-js-returned-as-qafinding)'s `asset-license` check blocks it from print and email. See [§10.6](technical-design.md#106-street-level-reference-imagery-istreetimageryprovider).

### `render_postcard_preview`
Input: `{ "campaignId": "...", "spec": { /* schemas/postcard-spec.schema.json */ }, "leadId": "L0001", "sides": ["front","back"], "width": 1200 }`
Output: **image content** (one PNG per side) plus text JSON:
```json
{ "saved": [".../previews/L0001_front_20260929T1502.png"], "qa": [ { "check": "overflow", "slot": "headline", "severity": "error", "message": "Headline overflows by 2 lines" } ] }
```

## Production

### `save_template` / `list_templates` / `get_template`
`save_template { name, spec, campaignId? }` → `{ "templateId": "tpl_bold_hero", "path": ".../Templates/bold-hero.json", "thumbnail": ".../bold-hero.png" }`. The spec keeps merge fields; lead-specific values are **not** baked in.

### `set_lead_overrides`
Input: `{ "campaignId": "...", "leadId": "L0004", "overrides": { "slots": { "headline": "Lift more in Cypress." }, "scene": { "productPosition": "left" } } }` → `{ "saved": true }`

### `assign_tracking_codes`
Input: `{ "campaignId": "..." }` → `{ "assigned": 15, "existing": 0, "sample": { "leadId": "L0001", "code": "K7Q3MX", "offerCode": "LIFT-K7Q3MX", "url": "https://example.com/lp?code=K7Q3MX" } }`

### `render_campaign` (job)
Input: `{ "campaignId": "...", "templateId": "tpl_bold_hero", "leadIds": null, "formats": ["print","email"], "onlyFlagged": false }`
Result: `{ "rendered": 15, "flagged": 2, "blockedByLicense": 0, "proofs": ".../postcards/proofs.pdf", "qaReport": ".../postcards/qa-report.xlsx" }`
Requires tracking codes (error `NOT_READY` otherwise).

### `build_dealer_packets` (job)
Result: `{ "dealers": 2, "files": [".../dealers/Gulf Lift Equipment/lead-packet.pdf", "..."] }`

### `export_mailing_manifest`
Output: `{ "path": ".../mailing/manifest.csv", "rows": 15, "excluded": { "holdout": 3 } }`
Columns: LeadId, Company, Attn, Address1, City, State, ZIP, DealerId, Dealer, Code, OfferCode, Url, Cohort, PrintPdf, EmailPng.

## Measure (C13)

### `assign_cohorts`
Input: `{ "campaignId": "...", "designs": [ { "type": "mailLater", "waves": 2, "split": [50,50] }, { "type": "ab", "variants": ["personalized","generic"], "split": [50,50], "within": "wave1" } ], "seed": "2026-10-houston", "force": false }`
Output: `{ "cohorts": { "wave1/personalized": 4, "wave1/generic": 4, "wave2": 7 } }`
Errors: `CONFLICT` if renders exist and `force` is false.

### `run_matchback`
Input: `{ "campaignId": "...", "mailDate": "2026-10-15", "windowDays": 365 }`
Output: `{ "matches": { "exact": 1, "strong": 2, "fuzzy": 3 }, "byCohort": [ { "cohort": "wave1", "leads": 8, "purchases": 2 } ], "report": ".../reports/attribution.xlsx" }`

## Errors

| Code | When | Hint example |
|---|---|---|
| `NOT_READY` | Setup or prerequisite missing (data, codes, profile) | "Run prepare_data for TX first." |
| `NOT_FOUND` | Unknown campaign/lead/template/job | "Use list_campaigns." |
| `VALIDATION_FAILED` | Schema or rule violation; include `details[]` | "signals[1].url is required." |
| `CONFLICT` | Locked cohorts, duplicate names, override conflicts | "Pass force=true to reassign." |
| `FILE_LOCKED` | Workbook or output file open in another app | "Close leads.xlsx in Excel." |
| `EXTERNAL_API` | Census/S3/website failures after retries | "Census API returned 503; try again later." |
| `RATE_LIMITED` | External rate limit hit | "Wait a few minutes and try again." (Not a Census hint any more: a key is mandatory there, and no rate-limit headers or documented daily cap exist.) |
| `LICENSE_BLOCKED` | Asset lacks print rights | "Replace products/foo.png or add print rights to its .asset.json." |
| `JOB_RUNNING` | Conflicting job already running for the campaign | "Wait for job_ab12cd." |
| `UNSUPPORTED` | Feature not built yet (stretch) | "Available after chunk S3." |
| `INTERNAL` | An unexpected failure inside the server (any exception that isn't one of the above) | "Something went wrong; check the log in `%LOCALAPPDATA%\ProspectStudio\logs`." |

Every tool error uses this envelope, so a skill can always branch on `code`:

```json
{ "error": { "code": "NOT_READY", "message": "…", "hint": "…" } }
```

`VALIDATION_FAILED` adds `details[]` **inside** `error` (see [`save_search_profile`](#save_search_profile)). The field is omitted when empty, so every other error keeps exactly the three keys above.

`INTERNAL` messages are deliberately generic: the exception text, stack and file paths go to the log only, never to the client.
