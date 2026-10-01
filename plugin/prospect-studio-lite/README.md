# Prospect Studio Lite (Cowork plugin)

A skills-only version of Prospect Studio for **demos and small pilots**. Claude does the prospect research with its own web search, and a small bundled renderer produces print-ready postcards. There's no server, database or install beyond Python + Playwright in the Cowork sandbox.

| Skill | Say something like | Produces |
|---|---|---|
| `find-leads-lite` | "Find warehouses and electrical contractors around Houston that likely need scissor lifts" | `profile.md`, `candidates.csv`, `research.jsonl`, **`leads.xlsx`** (Google Sheets / Excel compatible) |
| `design-postcards-lite` | "Design postcards for this campaign" → "shorter headline, lift on the left" → "save this design" | 4 preview variants, edits, `design/template.json` |
| `produce-postcards-lite` | "Produce the postcards for the approved leads" | Tracking codes, print PDFs (6×9 + bleed), email PNGs, `proofs.pdf`, QA report, dealer packets, `manifest.csv` |

## Set up
1. **Install the plugin** in Claude Desktop → Cowork: open the `.plugin` file and accept it. To update it later, install the new `.plugin` file.
2. **Create a workspace folder**, e.g. `Documents\Prospect Studio Lite`, and connect it to your Cowork task. The skills create these subfolders if they're missing:
   - `Brand Kit/`: optional `brand.json`, `logo.svg`/`.png`, product cutouts (only images you have print rights for)
   - `Dealers/`: `dealers.csv`, `territories.csv`
   - `Suppression/`: existing customers, do-not-contact, competitors, dealers
   - `Campaigns/`: one folder per campaign, created automatically
3. The CSV formats match the full POC's fixtures (`poc/fixtures/` in the Prospect Studio repo); copy those to try it with sample dealers.
4. **Renderer requirements** (checked automatically): Python 3 with `playwright` and Chromium in the Cowork sandbox. The skills try `pip install playwright && python -m playwright install chromium` if they're missing, which needs network access from the sandbox. Without it, you get HTML cards to print from a browser instead of PDFs.

`brand.json` (optional):
```json
{ "brandPrimary": "#1c3550", "brandAccent": "#e8a317", "logoSrc": "Brand Kit/logo.svg",
  "returnAddress": "Your Brand · 100 Example Pkwy · Houston, TX 77002",
  "legal": "Offer valid through participating dealers until 2027-03-31.",
  "productSrc": "Brand Kit/products/scissor-lift.png" }
```

## Typical demo (about 1–2 hours)
1. "Find leads: <brief>." Confirm the profile. Claude researches about 50 companies, deep-dives the top 20, and writes `leads.xlsx`.
2. Open `leads.xlsx` in Google Sheets (from Drive, edit as .xlsx) or Excel, set Status = **Approve** on 10–15 rows, and save.
3. "Design postcards." Pick one of 4 variants, request a couple of edits, then "save this design."
4. "Produce the postcards." Review `postcards/proofs.pdf` and the dealer packets.

## Limits (compared with the full Prospect Studio POC)
- **Discovery isn't exhaustive:** web search finds a strong sample, not every company in a territory. The full POC uses Overture business data + Census counts.
- **Scoring is judgment-based** (a written rubric), so two runs can differ slightly.
- **Guardrails** (image licensing, copy rules) are instructions, not code-enforced checks. QA of text overflow and QR codes *is* automated.
- Sized for **about 40–150 leads per campaign**.
- Nothing is mailed or emailed; it only produces files.

## Guardrails built in
- No Google Street View, Maps or Earth imagery; only licensed or owned images.
- Every lead claim cites a URL and date; no personal data beyond published business contacts.
- No invented offers; no negative or safety-incident references in copy.
- Tracking codes are unique and never change after assignment.

Third-party notice: `shared/qrcode.bundle.js` (MIT); see `shared/THIRD-PARTY-NOTICES.md`.
