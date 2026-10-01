---
name: produce-postcards-lite
description: Produces the final mailing package for a lead campaign. Assigns a unique tracking code to each approved lead, renders print-ready 6x9 postcard PDFs and email images from the saved design template, runs QA, and writes a proofs PDF, per-dealer lead packets and a mailing manifest. Use when the user says "produce the postcards", "make the cards for approved leads", "create the print files", "build the mailing list", or "make dealer packets".
---

# Produce postcards (Prospect Studio Lite)

Turn `leads.xlsx` (approved rows) plus `design/template.json` into a complete, reviewable file set in the campaign folder. Nothing is mailed or emailed. The output is files the user reviews and hands to a print vendor or dealers.

**Shared toolkit** (`<this skill's base directory>/../../shared/`): `render_postcards.py` (`codes`, `produce`, `html2pdf`), `postcard-template.html`, `example-cards.json`.
**Reference:** `references/output-spec.md` (folder layout, file names, codes, cohorts, manifest and dealer-packet formats).

## Step 1: Check inputs
1. Open the campaign's `leads.xlsx` and read the rows with Status = Approve. Match headers case-insensitively; accept the synonyms `Approved`/`Yes`.
2. Report the count by dealer and tier, and list any approved leads missing an address or dealer. **Ask the user to confirm before producing.**
3. Require `design/template.json`. If it's missing, offer to run the design step first.
4. Check the renderer as in the design skill (`render_postcards.py --help`; install Playwright/Chromium if needed).

## Step 2: Tracking codes
1. Load `codes.csv` (`LeadId,Code,OfferCode,Url`) if it exists. **Never change an existing code.**
2. For leads without a code, run `python3 <shared>/render_postcards.py codes --n <count> --existing codes.csv` and assign them.
3. Build `OfferCode = <offerPrefix>-<Code>` and `Url` from the template's `urlPattern`. Append the new rows to `codes.csv`.

## Step 3: Optional test groups
Ask once: *"Do you want a 'mail later' split, so you can later compare mailed vs. not-yet-mailed companies?"*
- If yes, randomly assign about 50% of approved leads to `wave1` and the rest to `wave2`, balanced within each tier and dealer, as described in `references/output-spec.md`. Save the result in `cohorts.csv`.
- Only `wave1` is produced now.
- If no, every approved lead is `wave1`.

## Step 4: Build `cards.json`
For each lead being produced, fill the template's fields:
- Replace the placeholders `{company}`, `{shortName}`, `{city}`, `{personalLine}`, `{dealerName}`, `{dealerCity}`, `{dealerPhone}` and `{code}`.
- Use a `*Fallback` field when the main text exceeds its limit or its value is empty.
- Set `fileStem = <LeadId>_<Company-with-dashes>`, plus `offerCode`, `url`, `attn` (contact name if known, else `Attn: Facilities Manager`), the address fields, and the dealer fields.

Write the result to `postcards/cards.json`.

## Step 5: Render and QA
1. Run `python3 <shared>/render_postcards.py produce --cards postcards/cards.json --out postcards`, from the workspace folder.
2. Read `postcards/qa-report.json`. For each card with errors, apply the smallest fix for **that lead only**, usually the headline fallback or a shorter company short name, then re-render just the fixed cards: write a small `cards-fix.json` and produce into the same folder.
3. Open two or three PNGs from `postcards/email/` and check them visually. Name any concern to the user.

## Step 6: Dealer packets
For each dealer with leads in this wave:
- Write `dealers/<Dealer>/leads.xlsx` with the columns in `references/output-spec.md`, including the Outcome dropdown.
- Write `dealers/<Dealer>/lead-packet.html`, one section per lead: company, address, why now (rationale + signals with links), suggested angle, contact roles, tracking code, and a thumbnail of the card from `postcards/email/`.
- Convert the packet with `render_postcards.py html2pdf --in … --out dealers/<Dealer>/lead-packet.pdf`.

## Step 7: Manifest and summary
1. Write `mailing/manifest.csv` with the columns in `references/output-spec.md`, one row per produced card.
2. Tell the user, briefly:
   - how many cards were produced and how many were fixed after QA
   - the wave split, if used
   - where each file set is: `postcards/proofs.pdf` for sign-off, `postcards/print/` for the printer, `dealers/` for dealers, `mailing/manifest.csv`
   - next steps: review `proofs.pdf`, send `print/` and the manifest to the print vendor, and send each dealer their packet before the cards land
   - a reminder that the QR codes point to the URL pattern they gave, so that page must exist before mailing

## Guardrails
- Produce only rows with Status = Approve, and never change or reuse a tracking code.
- Offers and legal text come only from the template or the brand kit.
- Don't claim anything was mailed, emailed or uploaded. This skill only creates files.
