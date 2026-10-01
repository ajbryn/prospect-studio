# Design spec

## Card fields (the input to `render_postcards.py`; see `shared/example-cards.json`)

| Field | Where it appears | Limit / notes |
|---|---|---|
| `layout` | Front | `hero-bold-left` · `photo-band-bottom` · `spec-panel-left` · `dusk-centered` |
| `palette` | Front | `brand-dark` (the only palette in v0.1; brand colors come from `brandPrimary`/`brandAccent`) |
| `scene.timeOfDay` | Front | `day` · `dusk` |
| `scene.liftPosition` | Front | `left` · `center` · `right` (keep it away from the text side of the layout) |
| `scene.buildingType` | Front | `warehouse` · `plant` · `shop` |
| `scene.signText` | Front (sign on the building) | Company name, ≤ 30 chars (use `shortName` if longer) |
| `scene.productSrc` | Front | Optional transparent PNG/SVG of the real product (licensed for print). Replaces the drawn lift |
| `scene.photoSrc` / `photoCredit` | Front | Optional licensed photo replacing the whole scene, plus the required credit text |
| `headline` | Front | hero ≤ 55 chars · band ≤ 50 · spec ≤ 40 · dusk ≤ 45 |
| `tagline` | Front (hero, band, dusk) | ≤ 70 chars |
| `cta` | Front (band, dusk) | ≤ 22 chars |
| `spec1..3` | Front (spec panel) | ≤ 26 chars each |
| `greeting` | Back | ≤ 40 chars, e.g. "Hello from {dealerName}," |
| `personal` | Back | ≤ 180 chars; the lead's personal line or a generic fallback |
| `benefit1..2` | Back | ≤ 50 chars each |
| `offerCode` | Back | `<PREFIX>-<CODE>`, e.g. `LIFT-K7Q3MX` |
| `offerText` | Back | ≤ 90 chars; the offer the user supplied |
| `url` / `urlDisplay` | Back (QR + printed) | The QR encodes `url`; `urlDisplay` is the short printed form |
| `dealerName` / `dealerLine` | Back | e.g. "Gulf Lift Equipment" / "Katy, TX · (281) 555-0142" |
| `attn` | Back (address) | Default `Attn: Facilities Manager`, or `Attn: <Contact name>` if known |
| `company`, `address1`, `cityStateZip` | Back (address) | From the lead |
| Brand: `brandPrimary`, `brandAccent`, `logoSrc`, `returnAddress`, `legal` | Both | From `brand.json` or the user |

The renderer's QA flags text overflowing its box, empty required text, text too close to the edge, and a missing QR code. Fix every **error** before showing or producing.

## Template file (`design/template.json`)
```json
{
  "name": "Bold hero",
  "layout": "hero-bold-left",
  "palette": "brand-dark",
  "scene": {"timeOfDay": "day", "liftPosition": "right", "buildingType": "warehouse", "signText": "{company}"},
  "fields": {
    "headline": "Reaching new heights in {city}, {shortName}?",
    "headlineFallback": "Reaching new heights in {city}?",
    "tagline": "Scissor and boom lifts for high-bay work, delivered this week.",
    "greeting": "Hello from {dealerName},",
    "personal": "{personalLine}",
    "personalFallback": "We help teams across {city} work safely at height, with local service from {dealerCity}.",
    "benefit1": "Same-week delivery and operator training",
    "benefit2": "Local service from {dealerCity}",
    "offerText": "Mention this code for free delivery and first-year service.",
    "cta": "Scan for a demo"
  },
  "urlPattern": "https://example.com/lp?code={code}",
  "offerPrefix": "LIFT"
}
```
Placeholders: `{company}`, `{shortName}` (company name without legal suffixes, ≤ 25 chars), `{city}`, `{personalLine}`, `{dealerName}`, `{dealerCity}`, `{dealerPhone}`, `{code}`. `*Fallback` fields are used when the main field would exceed its limit or its placeholder is empty.
