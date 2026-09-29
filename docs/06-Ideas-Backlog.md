# 06 · Ideas Backlog

Features brainstormed around the core concept. Rated by **Value** (to the business) and **Effort** (to build): H/M/L.

---

## A. Finding better leads

| Idea | Description | Value | Effort |
|---|---|---|---|
| **Lookalike search** | Seed with the best end customers (from warranty registrations) → AI finds common traits → "find more like these" in any territory. Probably the most convincing demo. | H | M |
| **Signal watchlists** | Saved searches that run weekly and flag *new* permits, facility openings, lift-related job posts and expansions per dealer territory. Turns a one-off tool into a steady lead feed. | H | M |
| **Contractor chain** | From a permit for a new warehouse, find the **general contractor and subs** (electrical, HVAC, racking). They need lifts *now* for that job. | H | M |
| **Replacement-timing leads** | UCC equipment-financing filings from 3–5 years ago suggest leases ending or fleets aging. | H | M |
| **Rental → purchase conversion** | If dealers also rent: frequent renters who'd save by buying. Needs dealer data. | H | L–M |
| **"Explain this market"** | AI brief per territory: top industries, growth areas, big projects coming. Doubles as a dealer business-review handout. | M | L |

## B. Dealer channel (national distributor view)

| Idea | Description | Value | Effort |
|---|---|---|---|
| **Coverage-gap map** | Market size (Census CBP) and lead density vs dealer territories. Shows **where the network is thin** and where to recruit dealers. | H | M |
| **Dealer scorecards** | Per dealer: leads received, response rate, time to first contact, win rate, revenue from campaigns. Useful in quarterly business reviews. | H | L (once attribution exists) |
| **Dealer-requested campaigns** | A dealer asks for "a campaign in my territory for boom lifts". Marketing runs it in the tool and sends back the packet and cards. Co-op marketing without a portal. | H | L |
| **Dealer pre-review & claim** | Dealers mark existing customers before mailing, and can "claim" leads they want to call first. Builds trust and removes waste. | H | L |
| **Lead-alert quality loop** | Dealers rate lead quality with one click ("good fit / not a fit") → scoring improves per territory. | M | L |
| **Dealer co-op budget tracking** | If national funds part of the mailing, track spend and results per dealer against co-op budgets. | M | M |
| **Dealer-branded variants** | Optional dealer logo and colors on the back of the card, within national brand rules. | M | L |

## C. Outreach & personalization

| Idea | Description | Value | Effort |
|---|---|---|---|
| **Multi-touch sequence** | Postcard → email 5 days later referencing the card → dealer call task → second card at 3 weeks. | H | M |
| **Personal landing page** | "Hi Bayou Fulfillment. Here's the lift we'd recommend for your new 40-ft racking." Spec sheet, video, local dealer card, quote form. Tracks visits. | H | M |
| **Demo-day invite cards** | Invite nearby leads to a demo at the dealer's branch or a jobsite, with an RSVP link. Brings dealers and prospects together. | H | L |
| **"Neighbor" social proof** | "A warehouse on your street just added three of our lifts." Only with the customer's permission. | M | L |
| **Seasonal / event triggers** | Hurricane prep (Gulf Coast), trade-show follow-ups, fiscal year-end budget flush, Section 179 tax deduction season (Q4). | M | L |
| **A/B testing** | Split by variant; compare response by dealer and segment. | M | M |
| **Handwritten-style mail** | Robot-pen notes (via vendors) for A-tier leads. | M | L |
| **Video postcards** | Short AI-assisted personalized video for email. | M | H |

## D. Existing customers

| Idea | Description | Value | Effort |
|---|---|---|---|
| **Service & inspection reminders** | Annual inspection due, battery replacement, and similar reminders driven by warranty/install-base data. Routed to the servicing dealer. | H | M |
| **Upgrade / trade-in offers** | Machines older than X years → "your 2019 scissor lift is worth $Y in trade at {{dealer}}". | H | M |
| **Cross-sell** | Customers with scissor lifts but no boom lifts. | M | M |
| **Warranty-expiry offers** | Extended-warranty or service-plan offer before coverage ends. | M | L |
| **Thank-you / anniversary cards** | Relationship touchpoints with an image of *their* machine at *their* site (company-owned photos). | M | L |

## E. Tool & operations

| Idea | Description | Value | Effort |
|---|---|---|---|
| **Campaign ROI report** | Cost → mailed → responses → dealer outcomes → wins → revenue, with holdout lift. | H | M |
| **Budget guardrails** | Per-campaign caps and a monthly cap. | M | L |
| **Brand lock** | Template fields locked except the ones marketing allows to change. | M | L |
| **Compliance center** | Suppression list, opt-outs, audit log, image license report per campaign. | M | M |
| **Claude Desktop tools** | The same MCP tools available in Claude Desktop for ad-hoc questions ("how many HVAC contractors are in Dealer X's territory?"). | M | L (comes with the MCP design) |

## Suggested "wow" demo for the pitch (1–2 days of prep)

1. Pick **one real dealer territory** (ideally a friendly dealer) and one product.
2. Run the lookalike + permit-signal flow by hand (Claude + Overture + a permits portal) → 25 scored leads with evidence, as `leads.xlsx`.
3. Make 4 co-branded postcard variants for the #1 lead (dealer photo, Mapillary, or AI look-alike scene), saved as PDFs in a folder.
4. Show a one-page attribution mock: how that card's QR code, offer code and dealer feedback would report back.
5. Walk through the [mockup](../mockups/prospect-studio-mockup.html).
6. Ask sales: *"Which 5 of these 25 would your dealer call tomorrow?"* The answer is the first accuracy metric.
