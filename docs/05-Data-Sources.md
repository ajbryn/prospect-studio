# 05 · Data Sources & AI Tools Catalog

The tools and datasets the AI assistant can use. **Always confirm license terms and current pricing with the vendor before committing.** The notes below reflect public information as of Sept 2026.

Legend: 💲 paid · 🆓 free · 🗄️ storable · ⏳ restricted storage

---

## 1. AI & platform services

| Tool | Use | Notes |
|---|---|---|
| **Claude API** (Anthropic C# SDK, NuGet `Anthropic`) 💲 | Profile building, extraction, scoring, copywriting, design edits, vision QA | Official SDK, GA; supports `IChatClient` (Microsoft.Extensions.AI) |
| **Claude web search tool** 💲 | Open-web research (news, expansions, company pages) | ~$10 per 1,000 searches + tokens; returns citations |
| **Claude web fetch tool** 💲 | Read a company website/page | Token cost only |
| **Gemini image API** 💲 | Scene generation, harmonization; multiple reference images, 2K/4K output | Good for multi-reference compositing |
| **OpenAI image API** 💲 | Mask-based edits, strong text rendering | Cheaper at low resolution |
| **Speech-to-text** (Web Speech API / Azure AI Speech / Whisper) 🆓/💲 | Voice edits in the Postcard Studio | Browser API is fine for a POC |
| **MCP** (C# `ModelContextProtocol` SDK) 🆓 | Expose the tool adapters as MCP servers | The same tools then also work in Claude Desktop |

## 2. Company / place discovery (breadth)

| Source | What you get | License / storage | Cost |
|---|---|---|---|
| **Overture Maps – Places** | ~80M global POIs: name, categories, address, website, phone, socials, confidence | CDLA-Permissive 2.0 / Apache 2.0 🗄️ | 🆓 (download Parquet from S3/Azure, query with DuckDB) |
| **OpenStreetMap** (via Overpass or extracts) | Buildings (footprints → size!), landuse=industrial, some businesses | ODbL (share-alike on the database) 🗄️ | 🆓 |
| **Google Places API (New)** | Best-in-class freshness, `businessStatus`, ratings | ⏳ **Only `place_id` storable**; ToS prohibits saving names/addresses; use for live verification/display | 💲 per request, SKU depends on fields; Text Search max 60 results/query |
| **Licensed B2B data**: Apollo, ZoomInfo, Data Axle, Dun & Bradstreet, People Data Labs | NAICS/SIC, employee count, revenue, HQ vs branch, contacts | Per contract 🗄️ (usually for internal use) | 💲 subscription or credits |
| **OpenCorporates / state Secretary of State** | Legal entity, registered agent, formation date (new companies!) | Varies | 🆓/💲 |

## 3. Market sizing & geography

| Source | Use | Cost |
|---|---|---|
| **Census County Business Patterns (CBP) API** | Establishment counts by NAICS × county/CBSA × size class, for the preview and "total addressable market" | 🆓 |
| **Census TIGER/Line** | County, CBSA, ZIP (ZCTA) boundaries for geography resolution and tiling | 🆓 |
| **Census Geocoder** | Address → lat/long, county FIPS | 🆓 |

## 4. Buying signals (depth)

| Signal | Source | Why it matters for lifts | Cost |
|---|---|---|---|
| New construction / expansion permits | **Shovels** (permit + contractor API), city/county open-data portals | New/expanded buildings need lifts for fit-out; also names the **contractors** doing the work (they're prospects too) | 💲 / 🆓 portals |
| Construction project pipeline | Dodge Construction Network, ConstructConnect | Large commercial projects months before build | 💲💲 |
| Hiring | Job boards via APIs (e.g., Adzuna API, Google Jobs via SerpApi, Coresignal) | Job posts that mention "aerial lift", "scissor lift", "forklift", "maintenance tech", "rack installer" | 💲 low |
| Regulatory facility presence | **OSHA inspection/establishment data** (DOL enforcement data), **EPA FRS / ECHO** | Confirms an industrial site exists, gives NAICS, sometimes employee counts. **Targeting only, never in copy.** | 🆓 |
| Government contracts | SAM.gov entity API, USAspending | Contractors that win facilities/construction awards | 🆓 |
| **UCC-1 filings** (equipment financing liens) | State SOS UCC search; data vendors | Shows who financed lifts/forklifts and when, which points to **replacement or upgrade timing**. Also competitive intel | 🆓/💲 |
| Fleet / logistics | FMCSA carrier data | Trucking and 3PL companies with terminals/yards | 🆓 |
| News & PR | Claude web search | New DC openings, contracts, acquisitions | via Claude |
| Commercial real estate | CoStar/LoopNet (licensed), press releases | New leases of large industrial space | 💲💲 |

## 5. Contacts (who to address the card to)

| Source | Notes |
|---|---|
| Apollo / ZoomInfo / People Data Labs | Titles like Facilities Manager, Operations Manager, Plant Manager, EHS/Safety Manager, Purchasing |
| Hunter.io | Email patterns/verification from a domain |
| Company website (web_fetch) | "Team" or "Contact" pages |
| **Default fallback** | "Attn: Facilities Manager" on print mail. It works fine and needs no contact data |

## 6. Imagery

| Source | Allowed for print mailers? | Notes |
|---|---|---|
| Google Street View / Maps / Earth | **No** | In-app live display only, with attribution |
| Mapillary | Yes, with attribution; CC BY-SA 4.0 | Patchy coverage; API v4 |
| Nearmap / EagleView | Per contract | Oblique aerial imagery; premium |
| Dealer / field photos | Yes | Emailed or dropped into the campaign folder |
| AI-generated scenes | Yes (don't derive from Google imagery) | Company logo use → legal review |
| Own product photos / OEM media kits | Yes | Cutouts for compositing |

## 7. Delivery & tracking

| Service | Use | Notes |
|---|---|---|
| **Lob** | Postcards, letters, address verification, tracking webhooks | 6×9 First Class ~$0.66–1.03/piece depending on plan; monthly plan fee on paid tiers |
| **PostGrid** | Same, often cheaper; US/CA | Compare in pilot |
| **Microsoft Graph** | Create email drafts in the marketer's Outlook | Lowest deliverability/compliance risk |
| Azure Communication Services / SendGrid | Batch email | Needs domain auth (SPF/DKIM/DMARC), unsubscribe handling |
| **Tracking relay** (Azure Functions + Table Storage) | QR/PURL redirect, landing pages, quote form, dealer alerts, one-click outcome links | The only hosted piece ([02 §3](02-Architecture.md#3-system-context-option-a)) |
| Call tracking (CallRail / Twilio) | One tracked number per dealer per campaign, forwarding to the dealer | Calls are a common response for equipment buyers |
| CRM API (HubSpot / Salesforce / Dynamics) | Push approved leads, sync suppression list | Depends on the company's CRM |

## 8. Internal data (the company's own)

Often the most valuable sources, and free.

| Source | Use |
|---|---|
| **Dealer territory map** (ZIP/county → dealer/branch) | Lead routing, co-branding, per-dealer campaigns |
| **Warranty / product registrations** | Suppression of existing end customers; lookalike seed list; **sales matchback** for attribution |
| **Dealer sales reports / rebate & co-op claims** | Matchback; offer-code redemption |
| **Install base** (model, serial, date) | Existing-customer campaigns: service, upgrades, trade-ins |
| **Product catalog & media kit** | Product cutouts for compositing; specs for copy |
| **Past campaign lists & results** | Baseline response rates; training labels for scoring |

## 9. Sources to avoid

- **Scraping Google Maps or LinkedIn**: violates ToS; accounts and API keys get banned.
- **Unlicensed "lead lists"** of unknown origin: poor quality, and consent/compliance risk for email.
- **Personal consumer data** (home addresses, personal phones) of employees.
