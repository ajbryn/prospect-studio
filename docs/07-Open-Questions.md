# 07 · Open Questions & Decision Log

This file holds **working assumptions, technical questions and the decision log**. Questions for the sales and marketing team (products, dealers, data, offers, budget) are in [09 · Sales Team Discussion Guide](09-Sales-Team-Discussion-Guide.md).

---

## Answers & working assumptions

| Date | Topic | Answer / assumption | Source |
|---|---|---|---|
| 2026-09-28 | Users | One marketing team of fewer than 5 people, possibly a single user. They may prefer a tool that runs in house, with outputs as files (PDFs/images in a folder, leads in Excel or Google Sheets). | Andy |
| 2026-09-28 | Business model | National distributor; sells mainly or only through local dealers, who usually close the deal. Attribution is high value. | Andy |
| 2026-09-28 | CRM | Sales team likely uses **HubSpot**, maybe Salesforce in some way. Details unknown. | Andy (to confirm, 09 Q18–Q20) |
| 2026-09-28 | Territory map | **Assume** a ZIP/county → dealer map exists. | Working assumption (09 Q7) |
| 2026-09-28 | Warranty data | Likely obtainable. | Andy (to confirm, 09 Q14) |
| 2026-09-28 | End-customer sales data | Unknown. | 09 Q15 |
| 2026-09-28 | Offer-code incentive | Possibly open to it; unknown. | 09 Q23 |
| 2026-09-28 | Holdout | Unknown. Explore softer designs ("mail later" wave, personalized vs generic). | 09 §4, Q24 |

## Technical questions

| # | Question | Why it matters |
|---|---|---|
| T1 | Which HubSpot tier and hubs (Marketing Hub Starter/Pro/Enterprise)? Can we get an API key or private app token? | Decides whether HubSpot replaces the custom tracking relay |
| T2 | Is Salesforce used for dealers (partner portal / Experience Cloud)? | Leads may need to flow there instead of email |
| T3 | Does the marketing team work in **Google Workspace or Microsoft 365**? (Andy has no Excel license) | The `.xlsx` works in both; native Google Sheets (C8b) becomes core if the team uses Google Workspace |
| T4 | Are users' PCs locked down (install rights, WebView2, outbound network to AI/data APIs)? | Desktop packaging and dependencies |
| T5 | Is OneDrive/Teams available for a shared campaign folder? | Sharing outputs, backup |
| T6 | Can we use a short subdomain of the brand's domain for QR links? | Trust and deliverability of QR links |
| T7 | Any policy on cloud AI providers processing prospect data? Anthropic API account (direct or via Azure/AWS/GCP marketplace)? | Vendor approval |
| T8 | Format of the warranty data (CSV export, database, portal)? Refresh cadence? | Matchback design |
| T9 | Format of the dealer territory map? | Import design |

## Decision log

| # | Date | Decision | Status | Rationale |
|---|---|---|---|---|
| D1 | 2026-09-28 | Google Street View is **not** used in mailers; in-app live display only | Accepted | Google guidelines prohibit print/promotional use, screenshots and modification |
| D2 | 2026-09-28 | Google Places used for live verification only; lead DB built from storable sources | Accepted | Maps Platform ToS prohibit storing names/addresses |
| D3 | 2026-09-28 | ~~Hosted Blazor web app on Azure~~ → **Local-first Windows desktop app** (Blazor Hybrid in WPF), outputs as files | **Proposed (v0.2)** | 1–5 users, likely 1; users prefer in-house tools and files in folders |
| D4 | 2026-09-28 | Bulk work runs as a deterministic pipeline; free-form agent only for interactive tasks | Proposed | Cost and quality predictability |
| D5 | 2026-09-28 | Designs are JSON specs over a fixed layout library; AI edits = JSON Patches | Proposed | Reversible, templatable, print-safe |
| D6 | 2026-09-28 | Product appears via composite + AI harmonization (not pure generation) | Proposed, pending bake-off | Product fidelity |
| D7 | 2026-09-28 | Default search scope = metro or dealer territory; national allowed as a budget-capped background job | Proposed | Cost control; matches the dealer channel |
| D8 | — | Primary firmographic data vendor (or none for the POC) | Open | Depends on budget (09 Q29) |
| D9 | — | Image model provider | Open | Bake-off ([04 §6](04-Postcard-Generation.md#6-putting-the-machine-in-the-picture)) |
| D10 | — | Product name | Open | "Prospect Studio" is a placeholder. Alternatives: LiftLeads, SiteScout, Territory Studio (check trademarks) |
| D11 | 2026-09-28 | Attribution ladder: tracking code per lead (QR + URL + offer code) → dealer outcome links → offer-code redemption → warranty matchback → lift measurement | Proposed | Dealer closes the sale; national needs proof ([08](08-Dealer-Routing-and-Attribution.md)) |
| D12 | 2026-09-28 | SQLite is the source of truth; `leads.xlsx` is a round-trip working view | Proposed | Keeps Excel usable without losing evidence and tracking data |
| D13 | 2026-09-29 | **Build the Claude-native POC first** (C# MCP server + Claude plugin skills, used from Claude Desktop/Cowork); desktop app later on the same Core/Infrastructure | **Accepted** | Fastest path to real output; engine carries over ([poc/](../poc/README.md)) |
| D14 | 2026-09-28 | **HubSpot** hosts landing pages, forms and dealer notifications, and is the campaign system of record; Prospect Studio generates QR codes and syncs via API. Custom relay = fallback | Proposed, pending T1 | Team already uses HubSpot; less to build and host ([08 §6b](08-Dealer-Routing-and-Attribution.md#6b-using-hubspot-for-tracking-and-routing)) |
| D15 | 2026-09-28 | Pilot measurement: "mail later" wave + personalized-vs-generic split; warranty matchback at 3/6/12 months | Proposed, pending 09 Q24 | Avoids permanently withholding good leads; tests the core pitch |
| D16 | 2026-09-29 | Data access with **EF Core 10** (SQLite, code-first migrations) rather than Dapper | Accepted | Team familiarity; migrations and LINQ; same stack as the future desktop app |
| D17 | 2026-09-29 | Lead spreadsheet: **Google-Sheets-compatible `.xlsx`** now (works in Sheets, Excel web, LibreOffice); native Google Sheets = optional chunk C8b | Accepted | No Excel license needed; keeps the POC free of OAuth setup until the team's platform is known |
