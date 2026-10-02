---
name: design-postcards-lite
description: Designs personalized, dealer co-branded 6x9 direct-mail postcards for an existing lead campaign. Proposes several design variants, renders previews, applies the user's edits (typed or dictated), and saves the chosen design as a reusable template. Use when the user says "design a postcard", "make mailers for these leads", "show me postcard options", "change the headline/layout/colors", or "save this design".
---

# Design postcards (Prospect Studio Lite)

Produce previews the user can react to, iterate, and save one design as `design/template.json` in the campaign folder. The production skill (`produce-postcards-lite`) uses that template for every approved lead.

**Shared toolkit:** the plugin's `shared/` folder, at `<this skill's base directory>/../../shared/`, contains:
- `render_postcards.py`: the renderer (`preview`, `produce`, `codes`)
- `postcard-template.html`: 6x9 front/back template with 4 front layouts, an illustrated building-and-lift scene, and a QR code
- `example-cards.json`: a complete, working example of the input format

Reference files: `references/design-spec.md` (fields, layouts, limits, template format), `references/copy-guidelines.md`.

## Step 0: Check the renderer (once per session)
1. Run `python3 <shared>/render_postcards.py --help`.
2. If Playwright is missing, try `pip install playwright` and then `python3 -m playwright install chromium`. If Chromium is already installed elsewhere, pass `--chromium <path>`.
3. If installation isn't possible (no network or no Python), tell the user. Fall back to writing the filled HTML files so they can be opened in a browser and printed at 9.25×6.25 in. Don't pretend PDFs were made.

## Step 1: Gather inputs
- **Campaign:** the campaign folder and its `leads.xlsx`. Pick a **sample lead**: the highest-scoring lead with Status = Approve, or the top A-tier lead if none are approved yet. Read its row and its research record from `research.jsonl`.
- **Brand:** `Brand Kit/brand.json` if present (colors, logo path, legal text, return address, product image). Otherwise ask for the two brand colors and a logo file, or use the defaults (navy `#1c3550`, amber `#e8a317`, no logo).
- **Campaign copy.** Ask for, or propose options for:
  - the offer or incentive, which **must come from the user** (never invent discounts or prices)
  - the call to action
  - the tracking URL pattern (e.g., a HubSpot landing page `https://…?code={code}`, or a placeholder)
- **Dealer:** the sample lead's dealer name, city and phone from `Dealers/dealers.csv`.

## Step 2: Propose 4 variants
Write `design/variants.json` in the format of `example-cards.json`, with four cards for the sample lead that differ on purpose:
1. `hero-bold-left`, bold tone, day, lift right
2. `photo-band-bottom`, friendly and congratulatory (uses the lead's signal), lift left
3. `spec-panel-left`, technical tone, with three product specs
4. `dusk-centered`, premium tone, dusk light

Use a preview code such as `LIFT-PREVIEW` and the real URL pattern. Keep every text field within the limits in `references/design-spec.md`.

## Step 3: Render and show
1. Run `python3 <shared>/render_postcards.py preview --cards design/variants.json --out design/previews`, from the workspace folder so relative paths resolve.
2. Fix any QA errors (usually a headline that's too long) and re-render **before** showing anything.
3. Look at the PNGs yourself and check that the text is readable and the scene isn't covering text. Then show the user all four fronts plus one back, with a one-line description of each.

## Step 4: Refine
1. Translate each request into specific changes: layout, palette, scene (`timeOfDay`, `liftPosition`, `buildingType`), or text fields.
2. Apply the change to the chosen variant only, re-render, show it, and state the change in one line ("Shortened headline; moved lift to the left; dusk light").
3. Keep a short change list in `design/changes.md`.
4. After 3 rounds, offer to save.

## Step 5: Save the template
Save `design/template.json` as described in `references/design-spec.md`: layout, palette and scene, plus text fields written with **merge placeholders** (`{shortName}`, `{city}`, `{personalLine}`, `{dealerName}`…) instead of this lead's specific values. Confirm that the user approves the template. Then say that the next step is "produce the postcards for approved leads."

## Guardrails
- Follow `references/copy-guidelines.md`.
- Offers, prices and financing terms come only from the user or `brand.json`.
- The personal line uses only public, business-level facts from the research record, never anything negative or about individuals.
