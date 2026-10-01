# File formats

## `leads.xlsx` (must open and edit cleanly in Google Sheets, Excel for the web, desktop Excel and LibreOffice)

**Sheets, in order:** `Leads`, `Evidence`, `Lists`, `Summary`, `ReadMe`.

**Leads** columns (row 1 = headers, bold, frozen; AutoFilter on the header row):

| Column | Notes |
|---|---|
| LeadId | `L0001`… in score order at creation; never renumber later. Header text: `LeadId (don't edit)` |
| Status | Dropdown: `Review`, `Approve`, `Reject`, `Hold` (inline list validation) |
| Score | Number 0–100 |
| Tier | A / B / C (static cell fill: A light green, B light blue, C none) |
| Company | |
| Segment | |
| Address | |
| City | |
| ZIP | Text (keep leading zeros) |
| Dealer | Dropdown from `Lists!A2:A…` (dealer names + `(no coverage)`) |
| Branch | |
| Top signal | Short text, e.g. "Permit: 180k sq ft addition (2026-07)" |
| Rationale | 1–2 sentences |
| Suggested angle | |
| Personal line | ≤ 180 chars (used on the postcard back) |
| Website | Show the full URL as text **and** set it as a hyperlink |
| Contact name | Blank unless the company publishes it |
| Contact title | Suggested role, e.g. "Facilities Manager" |
| Research | `deep` or `quick` |
| Notes | |

**Evidence**: LeadId, Company, Type, Claim, Date, Source URL (URL as text + hyperlink).
**Lists**: column A dealer names, column B status values.
**Summary**: profile summary, geography, counts (candidates, duplicates, suppressed by reason, researched, tiers, no-coverage), generated date.
**ReadMe**: how to review (change Status to Approve/Reject; editable columns are Status, Dealer, Contact name, Contact title, Notes, Personal line), and how to open it in Google Sheets without converting it.

**Compatibility rules:** plain ranges (no Excel "Tables"), no conditional formatting, no formulas in data columns, no merged cells, no macros, dates as `YYYY-MM-DD` text, one font (Calibri or Arial), ASCII sheet names.

**Reading it back** (for design/produce): match columns by header text, case-insensitive; Status synonyms `Approved`/`Yes` = Approve and `Rejected`/`No` = Reject; ignore unknown columns.

## Workspace reference files (same formats as the full Prospect Studio POC)

`Dealers/dealers.csv`
```
dealer_id,dealer_name,website,alert_email,branch_id,branch_name,address,city,state,zip,lat,lon,phone,tracking_phone
```

`Dealers/territories.csv`: `level` is `zip` or `county` (5-digit county FIPS); ZIP rows win over county rows; lower `priority` wins ties.
```
dealer_id,branch_id,level,code,priority
```

`Suppression/*.csv`: `reason` is one of customer, dnc, dealer, competitor, other.
```
company_name,domain,address,city,state,zip,reason
```
