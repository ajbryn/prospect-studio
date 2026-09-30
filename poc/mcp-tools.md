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
  "trackingBaseUrl": "https://example.com/lp?code={code}", "warnings": ["No CENSUS_API_KEY: limited to 500 calls/day"] }
```

### `prepare_data` (job)
Input: `{ "states": ["TX"], "force": false }` → `{ "jobId": "job_ab12cd", "status": "queued" }`.
Steps: NAICS check → counties → CBSA → ZCTA → Overture extract per state. Skips completed steps unless `force`.

### `get_job` / `list_jobs` / `cancel_job`
`get_job { jobId }` →
```json
{ "jobId": "job_ab12cd", "kind": "prefetch_websites", "status": "running", "progress": 0.42,
  "message": "Fetched 336/800 sites (12 blocked by robots.txt)", "result": null }
```
`list_jobs { campaignId?, status? }` → `{ "jobs": [...] }`. `cancel_job { jobId }` → `{ "status": "cancelled" }`.

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
`profile` is `null` until `save_search_profile` runs. **`geoLabel` stays `null` in C1** — resolving a query like "Houston metro" to "Houston-Pasadena-The Woodlands, TX" needs `resolve_geography`, which arrives in C2; until then the raw query is available under `profile.geographyQuery`. Counts are empty objects/arrays for a new campaign, not absent. Errors: `NOT_FOUND`.

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
Output: a `GeoScope` (see [technical-design §6.2](technical-design.md#62-geography-resolution)) plus `alternatives[]` when the query was ambiguous.

### `lookup_overture_categories`
Input: `{ "query": "warehouse", "state": "TX", "limit": 15 }`
Output: `{ "results": [ { "category": "warehouse", "path": ["...","warehouse"], "countInState": 5123 } ] }`

## Market

### `estimate_market`
Input: `{ "naics": ["4931","238210"], "geo": { "query": "Houston metro" }, "minEmployees": 20 }` (or `"geo": <GeoScope>`)
Output:
```json
{ "cbpYear": 2024, "geoLabel": "Houston-Pasadena-The Woodlands, TX",
  "byNaics": [ { "naics": "4931", "title": "Warehousing and Storage", "establishments": 612, "withMinEmployees": 318 } ],
  "total": { "establishments": 1310, "withMinEmployees": 520 },
  "notes": ["Some county cells suppressed by Census; totals are lower bounds"] }
```

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
| `RATE_LIMITED` | External rate limit hit | "Add CENSUS_API_KEY or wait." |
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
