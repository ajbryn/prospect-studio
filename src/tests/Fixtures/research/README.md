# Research fixtures (chunk C6)

Research documents for the three worked examples in
[docs/03 §8](../../../../docs/03-Lead-Search-Strategy.md#8-worked-example-illustrative-fictional-companies),
plus one `no_signal` document. All four validate against `poc/schemas/research.schema.json`.

Every subject is a real row of `poc/fixtures/sample-places.csv`, so the leads these documents are
saved against are leads a real `find_candidates` run produces. `ResearchFixtureIntegrityTests`
proves that: each subject is selected by `poc/fixtures/sample-search-profile.json`, routes to the
dealer docs/03 §8 names, and sits inside §7.6's 25-mile proximity band.

| File | Subject | Places row | Dealer | Expected |
|---|---|---|---|---|
| `poc/fixtures/sample-research-valid.json` | Bayou Fulfillment Co. | `fx_0001` | `gulf` / `gulf-west` | **100, tier A** (clamped from 102) |
| `gulf-coast-sign.research.json` | Gulf Coast Sign & Lighting | `fx_0002` | `bay` / `bay-pas` | **86, tier A** |
| `westpark-metal-fab.research.json` | Westpark Metal Fab | `fx_0007` | `gulf` / `gulf-west` | **71, tier B** |
| `northline-glass.no-signal.research.json` | Northline Glass & Glazing | `fx_0011` | `pine` / `pine-north` | `research_status = no_signal` |

Example 1 is the spec pack's own `sample-research-valid.json` rather than a copy, so a change to that
file is felt by the scoring tests too.

## The arithmetic

Weights are §7.6's defaults: `segmentFit 0.25, sizeFit 0.15, facilityFit 0.20, signals 0.25,
proximity 0.05, confidence 0.10`. The reference instant is **2026-10-06T00:00:00Z**
(`FixedTimeProvider.ScoringReference`), so the signal dates below are inside §7.6's twelve-month
window. `websiteReachable` is **false** throughout: chunk C7 is what fetches websites, so in C6
`confidence` is `0.7 × Overture confidence` with nothing added.

| Feature | Bayou (`fx_0001`) | Gulf Coast Sign (`fx_0002`) | Westpark (`fx_0007`) |
|---|---|---|---|
| `segmentFit` | 1.0 — `warehouse` is a segment category | 1.0 — `sign_making` | 1.0 — `metal_fabricator` |
| `sizeFit` | 1.0 — 140 ≥ 20 | 1.0 — 45 ≥ 20 | 1.0 — 60 ≥ 20 |
| applicable minimum | 20 (profile) | 20 (profile) | 20 (profile) |
| `facilityFit` | 1.0 — research `high` | 1.0 — research `high` | 1.0 — research `high` |
| `signals` | 1.0 — permit `2026-07` + hiring `2026-09-12` | 0.6 — one hiring signal | 0.0 — `registry` is not a buying signal |
| `proximity` | 1.0 — 0.0 mi to `gulf-west` | 1.0 — 2.2 mi to `bay-pas` | 1.0 — 16.1 mi to `gulf-west` |
| `confidence` | 0.665 — 0.7 × 0.95 | 0.63 — 0.7 × 0.90 | 0.63 — 0.7 × 0.90 |
| Σ wᵢ·fᵢ | 0.9665 | 0.863 | 0.713 |
| base = round(100 × Σ) | 97 | 86 | **71** |
| `llmAdjustment` | +5 | 0 | 0 |
| score | **100** (clamped from 102) | **86** | **71** |
| tier | **A** | **A** | **B** |

Westpark's **71** is the number docs/03 §8 documents, and it falls out of the arithmetic exactly.
That is what settles §7.6's `facilityFit` ruling: the keyword path would give Westpark 0.4 for
"no website text", dropping it to **59 — tier C**, which contradicts the document. The research
level wins when present, and a `WorkedExampleScoringTests` case pins the 59 so the ruling cannot be
reverted silently.

Examples 1 and 2 do **not** reproduce docs/03's illustrative 92 and 88 under either reading; the
tiers are the contract, and only Westpark's score is pinned to a number the document gives.

None of the three is touched by §7.6's **tier-A cap** (zero qualifying buying signals → capped at 79).
Bayou and Gulf Coast Sign each cite one or more, and Westpark cites none but scores 71, which is already
inside tier B — so an implementation that set the score to 79 whenever `signals` was 0 would *raise*
Westpark. `WorkedExampleScoringTests` pins that distinction.

`sizeFit` uses the **profile's** minimum of 20 for all three, because none of them matched the one
segment that sets its own ("Facilities & property management", 50). The segment-minimum branch of §7.6
is covered by `LeadScorerTests` against `fx_0071` Bluebonnet Property Services, which is the fixture
company that reaches that segment.

## Why `registry` matters here

Westpark's single signal is a `registry` row, which §7.6 excludes from the buying-signal set. If an
implementation counted it, Westpark would score `0.713 + 0.25 × 0.6 = 0.863` → 86 → tier A, and the
example would silently change tier. The fixture is the test for that exclusion.

The MCP-level tests re-date these signals to one month before the real clock before saving them
(`SampleResearch.WithSignalsDatedOneMonthBefore`), because the server runs on `TimeProvider.System`.
The committed dates above are what the Core scoring tests use, measured against the pinned
`FixedTimeProvider.ScoringReference`. Leaving the fixture dates in the server tests would make
example 2 stop being tier A in September 2027 with no code change.

Its `cautions` entry is a reminder, not a scored field: CLAUDE.md forbids referencing OSHA citations
or safety incidents in outreach copy, and the `personalLine` here deliberately mentions none.
