# Prospect Studio (working name)

A local, AI-assisted tool for a national equipment distributor's marketing team. It finds likely buyers in a territory, routes each lead to the right local dealer, and generates co-branded personalized postcards. Every card is traceable from mailbox to dealer to sale.

> **Status:** Concept / pitch → **Claude-native POC ready to build** (see [`poc/`](poc/README.md)). Specs are living documents. Last updated 2026-09-29.

## The one-paragraph pitch

A marketer types "Find warehouses, manufacturers and HVAC/electrical contractors around Houston that likely need scissor or boom lifts; skip anyone who's already a customer." The tool, running on their own PC, turns that into a search profile, shows how many companies match and what the search will cost, then researches open business data, permits, job posts and company websites. It produces a **`leads.xlsx`** of scored, evidence-backed leads, each assigned to its local dealer. The marketer approves leads in Excel or in the app, picks one of four AI-drafted postcard designs, and refines it by voice or text. The approved design becomes a template, and one co-branded **PDF per lead** lands in the campaign folder. Each card carries a unique QR code and offer code. Scans, calls, quote requests, dealer follow-ups and eventual sales are tied back to the campaign.

## Documents

| # | Doc | What it covers |
|---|-----|----------------|
| 01 | [PRD](docs/01-PRD.md) | Problem, users, goals, requirements, phases, success metrics |
| 02 | [Architecture](docs/02-Architecture.md) | Local-first desktop design, HubSpot or a small tracking relay for responses, folder/Excel outputs, data model, costs |
| 03 | [Lead Search Strategy](docs/03-Lead-Search-Strategy.md) | How the AI search works end to end: ICP, geography, sourcing, enrichment, scoring |
| 04 | [Postcard Generation](docs/04-Postcard-Generation.md) | Template model, AI variants, voice edits, image pipeline, **imagery licensing**, print specs |
| 05 | [Data Sources & Tools](docs/05-Data-Sources.md) | APIs and datasets the AI can use, with license, storage rules and cost notes |
| 06 | [Ideas Backlog](docs/06-Ideas-Backlog.md) | Features beyond the core, prioritized |
| 07 | [Open Questions & Decisions](docs/07-Open-Questions.md) | Working assumptions, technical questions, decision log |
| 08 | [Dealer Routing & Attribution](docs/08-Dealer-Routing-and-Attribution.md) | Assigning leads to dealers, co-branding, tracking codes, dealer feedback, sales matchback, holdout lift |
| 09 | [Sales Team Discussion Guide](docs/09-Sales-Team-Discussion-Guide.md) | **For the pitch meeting:** plain-language summary, ideas to vote on, ways to prove results, questions for the team, strawman pilot |
| — | [UI Mockup](mockups/prospect-studio-mockup.html) | Clickable HTML mockup for the pitch (open in a browser) |

## POC handoff (for Claude Code)

| Doc | What it covers |
|---|---|
| [CLAUDE.md](CLAUDE.md) | Instructions Claude Code reads automatically: stack, rules, conventions |
| [poc/README.md](poc/README.md) | **Start here:** what the POC is, milestones, kickoff prompts |
| [poc/requirements.md](poc/requirements.md) | POC requirements, non-functional requirements, acceptance demo |
| [poc/technical-design.md](poc/technical-design.md) | Solution structure, data model, algorithms, rendering, skills |
| [poc/mcp-tools.md](poc/mcp-tools.md) | Contract for every MCP tool |
| [poc/implementation-plan.md](poc/implementation-plan.md) | Chunks C0–C13 + stretch, each with tests and a manual check |
| [poc/agent-workflow.md](poc/agent-workflow.md) · [poc/claude-config/](poc/claude-config/README.md) | Subagent roles, the per-chunk pipeline, parallel waves; agent files to install into `.claude/` |
| [poc/schemas/](poc/schemas/) · [poc/fixtures/](poc/fixtures/README.md) | JSON Schemas and fictional test data |

## Critical constraints

1. **Google Street View imagery can't go on the postcard.** Google's guidelines forbid Street View in print or promotional materials, and forbid screenshots and alterations. It can be viewed inside the app; the mailer image must come from a licensed source ([04 §5](docs/04-Postcard-Generation.md#5-imagery-sources--licensing-read-this-first)).
2. **Google Places data can't be stored as a lead database.** Only `place_id` may be stored. The lead list is built from sources that allow storage (Overture Maps, licensed B2B data, public records).
3. **Attribution needs something online.** The app is local, but QR codes must resolve and quote forms must reach dealers while the laptop is off. **HubSpot** (which the team likely uses) can cover this with landing pages, forms and workflows. A tiny custom relay is the fallback ([08 §6b](docs/08-Dealer-Routing-and-Attribution.md#6b-using-hubspot-for-tracking-and-routing)).
4. **Email must comply with CAN-SPAM.** Physical mail is the primary channel for this concept.

## Project folder layout

```
PersonalizedMarketing/        ← this spec repo
├─ README.md
├─ docs/                      specs (this set)
├─ mockups/                   pitch mockups, screenshots
├─ poc/                       POC handoff pack (requirements, design, plan, schemas, fixtures)
├─ CLAUDE.md                  instructions for Claude Code
├─ .claude/                   subagents (agents/*.md) and shared settings
├─ adr/                       architecture decision records (as decisions get made)
├─ src/                       POC code (created in chunk C0)
└─ plugin/prospect-studio/    Claude plugin with skills (created in chunk C9)
```

The tool's own working folder (campaigns, postcards, leads workbooks) is described in [02 §4.2](docs/02-Architecture.md#42-files-as-the-user-facing-surface).
