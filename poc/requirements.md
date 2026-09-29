# POC Requirements: Prospect Studio (Claude-native)

**Version:** 0.1 · 2026-09-29 · Traces to [PRD v0.2](../docs/01-PRD.md) (IDs in brackets)

---

## 1. Goal

Prove, with real open data and a real territory, that a single marketing user can go from a plain-English brief to:
1. a **researched, scored, dealer-assigned lead list** as a spreadsheet that works in Google Sheets or Excel, and
2. **print-ready, personalized, dealer co-branded postcards**, each with a unique tracking code,

in **under 90 minutes of working time**, using Claude Desktop/Cowork plus a local MCP server. No custom UI.

## 2. Users & environment

- **Primary user:** one marketer (non-developer) on Windows 11 using Claude Desktop / Cowork.
- **Builder/tester:** Andy, using Claude Code.
- **Runs locally.** The MCP server is a stdio process registered in Claude Desktop's config. Data lives in `%LOCALAPPDATA%\ProspectStudio` and the user's `Documents\Prospect Studio`.
- **Network:** needed for setup (Census, Overture), Census market queries, and website fetching. Candidate search runs offline against the local extract.

## 3. Functional requirements

Priority: **M** = required for POC acceptance · **S** = should, if time allows · **X** = stretch (separate chunk, optional).

### 3.1 Setup & workspace
| ID | Requirement | Pri | Chunk |
|---|---|---|---|
| POC-1 | The server reports its status: version, workspace paths, reference data present, Overture release and states extracted, which optional keys are configured. | M | C0–C2 |
| POC-2 | One-time setup (CLI and tool) downloads Census reference data (county boundaries, CBSA delineation, ZCTA↔county) and extracts Overture Places for chosen states to local Parquet. Re-runs are idempotent. | M | C2, C4 |
| POC-3 | Workspace folder is created on first use: `Brand Kit\`, `Dealers\`, `Suppression\`, `Templates\`, `Campaigns\`. | M | C1 |
| POC-4 | Import dealers, territories (ZIP- or county-level) and suppression lists from CSV/XLSX, with row-level validation errors. [DR-1, SP-7] | M | C5 |

### 3.2 Campaign & search profile
| ID | Requirement | Pri | Chunk |
|---|---|---|---|
| POC-5 | Create, list and open campaigns; each has a folder under `Campaigns\` and a record in SQLite. | M | C1 |
| POC-6 | Save a **search profile** (JSON, validated against `schemas/search-profile.schema.json`) and write `search-profile.json` to the campaign folder. [SP-1, SP-2] | M | C1 |
| POC-7 | Look up NAICS codes and Overture taxonomy categories by keyword to help Claude build the profile. | M | C2, C4 |
| POC-8 | Resolve geography from state, county, CBSA/metro name, ZIP list, radius around a point/address, or a dealer's territory. [SP-3] | M | C2 |
| POC-9 | **Market estimate** before searching: Census CBP establishment counts by NAICS for the geography, with an employee-size breakdown. [SP-4] | M | C3 |

### 3.3 Candidates, routing & scoring
| ID | Requirement | Pri | Chunk |
|---|---|---|---|
| POC-10 | Find candidates in the local Overture extract by taxonomy categories and/or name keywords within the geography, above a confidence threshold, and store them as sites with provenance. [LD-1, LD-7] | M | C4 |
| POC-11 | Deduplicate (domain, normalized name + proximity) and **suppress** existing customers, do-not-contact entries, dealers and competitors. [LD-1, SP-7] | M | C4, C5 |
| POC-12 | **Assign each lead to a dealer/branch** using the territory map (ZIP overrides county); flag coverage gaps. [DR-2, DR-3] | M | C5 |
| POC-13 | **Light enrichment:** fetch each candidate's homepage (+ one about page), store a text excerpt, and derive keyword features, as a background job. [LD-2, LD-4] | M | C7 |
| POC-14 | **Deterministic score** (0–100) and tier from features, per the rubric in the technical design. [LD-3] | M | C6 |
| POC-15 | **Save deep research** from Claude for a lead (summary, signals with URLs and dates, suggested angle, personal line, LLM adjustment), validated against `schemas/research.schema.json`; the score is recomputed. Claims without a URL are rejected. [LD-3, LD-4] | M | C6 |
| POC-16 | List leads compactly with filters and paging; get full detail for one lead; bulk-update status/notes/dealer. | M | C6 |

### 3.4 Spreadsheet round-trip (Google Sheets & Excel compatible)
| ID | Requirement | Pri | Chunk |
|---|---|---|---|
| POC-17 | Export `leads.xlsx`: a Leads sheet (frozen header, filters, `Status` and `Dealer` dropdowns, evidence URLs, visible `LeadId`), plus Evidence, Lists, Summary and ReadMe sheets. The file must open and edit correctly in **Google Sheets** (including editing as .xlsx from Drive), Excel for the web, desktop Excel and LibreOffice ([compatibility profile](technical-design.md#91-compatibility-profile-use-only-features-that-survive-all-four-apps)). [LR-2] | M | C8 |
| POC-18 | Import edits from `leads.xlsx` or a CSV export (Status, Notes, Dealer override, Contact name/title), matching columns by header, including files re-saved by Google Sheets or LibreOffice; report a change summary. If the file is open and locked, return a clear error. [LR-2] | M | C8 |
| POC-18b | **Native Google Sheet** per campaign (publish + pull edits) via the Sheets API with OAuth `drive.file`. [LR-6] | S (core if the team uses Google Workspace) | C8b |

### 3.5 Postcards
| ID | Requirement | Pri | Chunk |
|---|---|---|---|
| POC-19 | A layout library of at least **4 front layouts and 1 back layout** (based on the mockup), each with defined slots and text limits. | M | C10 |
| POC-20 | Postcard **design spec** JSON (`schemas/postcard-spec.schema.json`) with merge fields for lead, company, dealer, campaign and tracking data. | M | C10 |
| POC-21 | **Render a preview** (PNG, returned to Claude as an image, and saved) of a spec for a given lead, front and/or back, with QA findings (text overflow, missing merge values, low contrast). [PC-2, PC-3] | M | C10 |
| POC-22 | **Hero scene:** a parameterized illustrated scene (building type, sign text, time of day) with the **product cutout from the brand kit** placed at a chosen position. The scene is license-safe by construction. [PC-4] | M | C10 |
| POC-23 | **Asset license enforcement:** every image has an `*.asset.json` sidecar; print rendering refuses assets without print rights. | M | C10 |
| POC-24 | Save and list **templates** (spec + thumbnail) in `Templates\`. [PC-5] | M | C11 |
| POC-25 | **Tracking codes:** one unique code per approved lead, printed as QR code, short URL and offer code. Base URL is configurable (e.g., a HubSpot landing page with `?code=`). [AT-1] | M | C11 |
| POC-26 | **Batch render** a template for all approved leads (background job): per-lead print PDF (6×9 with 0.125" bleed) and email PNG, `proofs.pdf`, and a QA report. [PC-6, DL-1] | M | C11 |
| POC-27 | **Dealer packets:** per dealer, a PDF (one page per lead: why, evidence, suggested angle, code) and an XLSX with outcome columns. [DR-5] | M | C11 |
| POC-28 | **Mailing manifest** CSV: lead, company, address, dealer, code, cohort, file names. | M | C11 |

### 3.6 Skills (Claude plugin)
| ID | Requirement | Pri | Chunk |
|---|---|---|---|
| POC-29 | `setup-workspace` skill: checks status, runs setup, imports dealers, territories and suppression, and verifies the brand kit. | M | C9 |
| POC-30 | `find-leads` skill: brief → profile (confirmed with the user) → market estimate → candidates → enrichment → scoring → deep research on the top N → workbook. | M | C9 |
| POC-31 | `design-postcards` skill: campaign copy → N variants → previews → edit loop → save template, following the copy and imagery guardrails. | M | C12 |
| POC-32 | `produce-campaign` skill: codes → cohorts (optional) → batch render → QA review → dealer packets → manifest → summary. | M | C12 |

### 3.7 Measurement groundwork
| ID | Requirement | Pri | Chunk |
|---|---|---|---|
| POC-33 | **Cohort assignment:** "mail later" waves, holdout %, or A/B creative split; seeded, stratified by tier and dealer; recorded per lead and in the manifest. [AT-5] | S | C13 |
| POC-34 | **Warranty import + matchback:** import warranty registrations and match them to campaign leads (exact / strong / fuzzy); write `reports\attribution.xlsx` with a review queue for fuzzy matches. [AT-6, AT-7] | S | C13 |

### 3.8 Stretch (optional chunks)
| ID | Requirement | Pri |
|---|---|---|
| POC-X1 | AI photo-style scene generation (OpenAI or Gemini image API) saved with license metadata; product composited from the cutout. | X |
| POC-X2 | Google Places live verification (`businessStatus`), storing `place_id` only. | X |
| POC-X3 | HubSpot sync: upsert approved leads as companies with custom properties (`ps_lead_id`, `ps_code`, `ps_dealer`, `ps_cohort`, `ps_score`); pull form submissions by code. | X |
| POC-X4 | Lob test-mode send of postcards from the manifest; pull tracking events. | X |
| POC-X5 | Bulk LLM light-enrichment via the Anthropic API (small model) for large candidate sets. | X |
| POC-X6 | Signals source: city/county open-data building permits for Houston. | X |

## 4. Non-functional requirements

| ID | Requirement |
|---|---|
| NFR-1 | **Responsiveness:** any tool returns within 20 s, or starts a background job and returns a `jobId`. `get_job` reports progress %, counts and messages. |
| NFR-2 | **Compact outputs:** default tool responses under about 4,000 tokens; paging (`limit`/`offset`) for lists; full data in files/DB. |
| NFR-3 | **Idempotency:** setup, candidate search (same inputs), code assignment and rendering can be re-run without duplicating records. Codes never change once assigned. |
| NFR-4 | **Performance:** candidate search over a metro on the local extract < 30 s; batch render ≥ 10 cards/minute; workbook export < 10 s for 1,000 leads. |
| NFR-5 | **Reliability:** background jobs persist state in SQLite and are resumable after a server restart (at minimum, they report "interrupted" and can be re-run safely). |
| NFR-6 | **Compliance:** the guardrails in `CLAUDE.md` (Street View/Places rules, robots.txt, asset licensing, copy rules) are enforced in code where possible and in skills otherwise. |
| NFR-7 | **Observability:** structured logs to `%LOCALAPPDATA%\ProspectStudio\logs\` and stderr; each tool call logged with duration and outcome; no secrets in logs. |
| NFR-8 | **Security:** keys only from environment variables; no network listener (stdio only). |
| NFR-9 | **Portability:** Core/Infrastructure have no MCP dependency and are reusable by the future desktop app. |
| NFR-10 | **Testability:** unit tests need no network; fixtures cover the Houston example; live tests are opt-in (`Category=Network`). |

## 5. Out of scope for the POC

- Custom UI (desktop or web): Claude Desktop/Cowork is the UI.
- Sending mail or email (manifest only; Lob is stretch).
- Paid data vendors (Apollo, ZoomInfo, Shovels…).
- Multi-user concurrency, SSO, roles.
- Native Google Sheets **if** C8b isn't promoted (the .xlsx works in Google Sheets regardless).
- Voice input beyond the OS/Claude app's own dictation.

## 6. Acceptance demo (POC is "done" when this runs end to end)

Run in **Cowork** with the plugin installed and the MCP server registered, using the fixtures as dealer, territory and suppression data.

| Step | User says / does | Expected result |
|---|---|---|
| 1 | "Set up Prospect Studio for Texas." | Status OK; reference data present; TX extract present; fixtures imported (3 dealers, territories, suppression). |
| 2 | "New campaign: Houston scissor & boom lifts, Q4." + the brief from [03 §2](../docs/03-Lead-Search-Strategy.md#2-step-1-search-profile-icp) | Claude proposes a profile (segments, NAICS, Overture categories, keywords, exclusions) and asks to confirm; `search-profile.json` saved. |
| 3 | "How big is this market?" | Establishment counts for the Houston CBSA (9 counties) by segment, with size classes. |
| 4 | "Find the candidates." | ≥ 300 candidates; duplicates merged; suppressed entries removed with counts by reason; every lead has a dealer or a coverage-gap flag. |
| 5 | "Enrich and score them." | Website job completes; scores and tiers assigned; the top 50 are listed compactly. |
| 6 | "Research the top 25." | Claude researches with web search; each of the 25 has saved research with ≥ 1 cited source, or an explicit "no signal found". |
| 7 | "Give me the spreadsheet." → user opens `leads.xlsx` in **Google Sheets** (from a Drive-synced folder) or Excel, approves ~15 leads, saves → "I've updated the sheet." | The file opens with dropdowns and links intact; import reports 15 approved and any notes/dealer changes. |
| 8 | "Design postcards for this campaign." | 4 variants rendered as previews in chat for the top lead, front and back, with dealer co-branding and a QR code. |
| 9 | Two refinement requests, e.g. "shorter headline", "put the lift on the left, dusk light" → "Save this as a template." | Previews update; template saved with a thumbnail. |
| 10 | "Produce the campaign with a mail-later split." | Codes and cohorts assigned; PDFs and PNGs for each approved lead; `proofs.pdf`; QA report (flags ≤ 20% of cards); dealer packets; `manifest.csv`. |
| 11 | Andy checks the files | PDF page size 9.25 × 6.25 in; QR codes decode to `<base>?code=<CODE>`; no asset without print rights; no Google imagery anywhere. |

**Success criteria:** steps 1–11 complete without developer intervention; total working time < 90 min; the marketer rates ≥ 70% of the top 25 leads "worth contacting" and ≥ 1 of the 4 variants "good enough to mail" after ≤ 3 edits.

## 7. Open items affecting the POC

| Item | Default until answered |
|---|---|
| Real territory map / suppression list / warranty data | Use fixtures |
| Brand kit and product photos | Placeholder brand + SVG product cutout from fixtures |
| Tracking base URL (HubSpot landing page?) | `https://example.com/lp?code=` placeholder, configurable |
| Print vendor (Lob/PostGrid) template specifics | Generic 6×9 layout with an address block and clear zone; adjust later |
| Which HubSpot tier | Stretch chunk only |
