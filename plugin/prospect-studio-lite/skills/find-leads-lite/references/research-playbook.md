# Research playbook

## Discovery search patterns (Step 2)
Replace `<kw>` with segment keywords and `<area>` with a city, suburb or county. Rotate areas so coverage spreads across the territory.

- `<kw> <area> TX` (e.g., "distribution center Katy TX", "electrical contractor Pasadena TX")
- `"<kw>" "<area>" warehouse OR facility OR "square feet"`
- `new distribution center <area> 2026` · `expansion <area> warehouse square feet`
- `<area> commercial building permit warehouse` · `<city> permits issued industrial addition`
- `"scissor lift" OR "aerial lift" OR "boom lift" job <area>` (the hiring companies are prospects)
- `<area> chamber of commerce <industry> members` · `<industry> association Texas members <area>`
- `general contractor new warehouse <area>` (then look for the electrical/HVAC/racking subs)

Good sources: company websites; local business journals and news; city/county permit portals and permit news; job boards (via search results); press releases; industry association and chamber directories; state contract award lists.

Avoid: scraping LinkedIn, copying Google Maps listings, paywalled databases, anything behind a login.

## Signal types and rules (Step 5)
| Type | Examples | Counts as a buying signal? |
|---|---|---|
| `permit` | New building, addition, mezzanine, racking, high-bay lighting retrofit | Yes |
| `expansion` | New site, added square footage, relocation to a larger facility | Yes |
| `hiring` | Maintenance tech, rack installer, sign installer, electrician, "aerial lift certification" | Yes |
| `contract` | Won a large facilities, construction or installation contract | Yes |
| `news` | Growth news, acquisition, big new customer | Yes |
| `funding` | Investment, loan, grant tied to expansion | Yes |
| `registry` | Appears in an industry/facility registry | No (supporting context only) |

Every signal needs a **URL** and a **date** (`YYYY-MM` or `YYYY-MM-DD`). Older than 12 months doesn't score, but may appear in the summary.

## Research record (one JSON object per line in `research.jsonl`)
```json
{"leadId":"L0001","company":"Bayou Fulfillment Co.","status":"researched",
 "summary":"3PL running a tilt-wall DC in Katy with 14 dock doors.",
 "facility":{"level":"high","evidence":"32-ft clear height racking","url":"https://www.example.com/facility"},
 "employees":{"value":140,"url":"https://www.example.com/about"},
 "signals":[{"type":"permit","text":"180,000 sq ft addition","url":"https://permits.example.gov/rec/123","date":"2026-07"}],
 "suggestedAngle":"Congratulate on expansion; offer rental-to-own for the fit-out.",
 "personalLine":"Congrats on the 180,000 sq ft addition in Katy. Fitting out high-bay racking goes faster with the right lift on site.",
 "contactRoles":["Facilities Manager","Operations Director"],
 "adjustment":0,"rationale":"Expanding high-bay warehouse with an active permit and lift-related hiring.",
 "sources":["https://www.example.com/about","https://permits.example.gov/rec/123"]}
```
Use `"status":"no_signal"` and an empty `signals` list when nothing was found.

**Personal line rules:** ≤ 180 characters; only public, business-level facts; friendly and specific; no mention of safety incidents, lawsuits, OSHA or anything negative; no named individuals.
