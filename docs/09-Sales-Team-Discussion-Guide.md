# 09 · Sales Team Discussion Guide

**Purpose:** a working agenda for a 45–60 minute conversation with the sales and marketing team. It explains the idea in plain language, lists ideas for them to react to, and collects the questions only they can answer.
**Audience:** sales leadership, marketing, anyone who works with dealers. No technical background needed.
**Status:** Draft v0.1, 2026-09-28. Technical questions and decisions stay in [07](07-Open-Questions.md).

**How to use it:** walk through sections 1–4 as the pitch. In section 3, ask the team to mark each idea **Must / Nice / Skip**. Section 5 is the question list; capture answers directly in this file (or a copy) during the meeting.

---

## 1. The idea in 60 seconds

> Tell the tool who you want to reach ("warehouses and electrical contractors around Houston that probably need scissor lifts"). It finds those companies, explains **why** each one is a good fit, and assigns each one to the **local dealer** who covers that area.
>
> For the companies you approve, it designs a **personalized postcard**: their kind of building with our machine in front of it, a headline that mentions their city or recent expansion, and the local dealer's name and phone on the back.
>
> Every card has its own **QR code and offer code**, so when a prospect responds, we know which card, which dealer and which campaign it came from. That lets us show whether marketing is creating sales, even though the dealer closes the deal.

What the team gets at the end of each campaign:
- a **spreadsheet** of scored leads with the reasons behind each score,
- **print-ready postcards** (PDF) in a folder, one per company,
- a **lead packet for each dealer**,
- a **results report**: responses, dealer follow-up, and sales traced back to the campaign.

## 2. What we're assuming (please confirm or correct)

| # | Assumption | Status |
|---|---|---|
| A1 | The company is the national distributor and sells mainly or only through local dealers. | Confirmed |
| A2 | Marketing is a small team (1–5 people). One person may run the tool. | Confirmed |
| A3 | There is a **map of which dealer covers which ZIP codes or counties**. | Assumed. Please confirm |
| A4 | Marketing can get **warranty registration data** (end customer, address, model, date). | Likely. Please confirm |
| A5 | End-customer **sales data from dealers** is *not* routinely available. | Unknown |
| A6 | The team uses **HubSpot** (and maybe Salesforce somewhere). | Likely. Which one holds what? |
| A7 | Physical postcards are the main channel; email is secondary. | Assumed |
| A8 | The company might fund an **incentive tied to an offer code** (e.g., free delivery). | Open |

## 3. Ideas to react to

Mark each: **M** = must have · **N** = nice to have · **S** = skip. Ideas marked ⭐ are what we'd suggest for a first pilot.

### Finding the right companies
| Idea | What it means for sales | Vote |
|---|---|---|
| ⭐ **Describe it, get a list** | Type what an ideal customer looks like; get a scored list with reasons and sources for each company. | |
| ⭐ **"Find more like our best customers"** | Use warranty data to learn what our best customers have in common, then find lookalikes in any territory. | |
| **Growth signals** | Rank companies higher when they are expanding: new building permits, job posts for lift operators, news of a new facility. | |
| **Contractors on new projects** | When a permit shows a new warehouse going up, find the electrical, HVAC and racking contractors on that job. They need lifts now. | |
| **Weekly "new signals" alerts** | Every week, list new permits and expansions in each dealer's territory. | |
| **Replacement timing** | Public equipment-financing records from 3–5 years ago hint at fleets due for replacement. | |

### Making outreach stand out
| Idea | What it means for sales | Vote |
|---|---|---|
| ⭐ **Personalized postcards** | Each card shows a building like theirs with our machine, and mentions their city or recent news. | |
| ⭐ **Local dealer on every card** | National brand on the front; the local dealer's name and phone on the back. | |
| **Personal web page per company** | The QR code opens a page greeting them by company name, with the recommended machine, a spec sheet and a "request a quote" button that goes to the dealer. | |
| **Demo-day invitations** | Invite nearby prospects to a demo at the dealer's branch or a jobsite. | |
| **Follow-up sequence** | Card → email a few days later → dealer call → second card. | |
| **Timely themes** | Hurricane prep (Gulf Coast), year-end budget spending, Section 179 tax deduction season in Q4. | |

### Working with dealers
| Idea | What it means for sales | Vote |
|---|---|---|
| ⭐ **Leads routed to the right dealer automatically** | Based on the territory map. Overrides are easy. | |
| ⭐ **Dealer lead packet** | One page per lead for the dealer: who they are, why now, what to say. | |
| **Dealer preview before mailing** | Dealers see the list first and remove their existing customers or deals in progress. | |
| **Instant alert to the dealer** | When a prospect scans or requests a quote, the dealer gets an email right away with one-click buttons to report progress. | |
| **Dealer scorecards** | Per dealer: leads received, how fast they followed up, what closed. | |
| **Coverage-gap map** | Where there's demand but no dealer, which helps with dealer recruitment. | |
| **Campaigns on request** | A dealer asks for a campaign in their territory; marketing runs it. | |

### Existing customers
| Idea | What it means for sales | Vote |
|---|---|---|
| **Service and inspection reminders** | From warranty data, routed to the servicing dealer. | |
| **Upgrade / trade-in offers** | For machines older than X years. | |
| **Warranty-expiry offers** | Extended warranty or service plan before coverage ends. | |

## 4. Proving it works

Because dealers close the sales, we need ways to connect a postcard to a sale. There are two separate questions:

1. **Did people respond?** Easy: unique QR codes, offer codes, and tracked phone numbers.
2. **Did the campaign create sales that wouldn't have happened anyway?** Harder, and the question leadership will ultimately ask.

### Ways to connect a card to a sale
| Method | How it works | What we need |
|---|---|---|
| ⭐ **QR code / web page per company** | Each scan or quote request is tied to one company and one dealer. | A landing page (HubSpot may already do this) |
| ⭐ **Offer code** | "Mention code LIFT-K7Q3MX for free delivery." The dealer records the code to get reimbursed, which also tells us the sale happened. | A funded incentive and a claim process |
| ⭐ **Warranty matchback** | Compare the list of mailed companies with warranty registrations over the next 12 months. | Warranty data (assumption A4) |
| **"Promo code" box on the warranty form** | Add an optional "Did you receive a mailer? Enter your code" field to warranty registration. It's cheap and captures sales even when the dealer forgets. | A small change to the warranty form |
| **Dealer reports** | Dealers click "Contacted / Quoted / Won" in the alert email. | Dealer cooperation |

### Ways to show the campaign *caused* the sales
Some of these avoid the objection "we'd be holding back good leads". None of them is required on day one.

| Design | How it works | Pros | Cons |
|---|---|---|---|
| **Classic holdout** | Randomly skip 10–20% of approved companies and compare their purchase rate with the mailed group. | The clearest answer | Some good prospects go unmailed |
| ⭐ **"Mail later" wave** | Randomly split the list into two waves 6–8 weeks apart. Compare wave 1 with wave 2 *before* wave 2 is mailed, then mail wave 2. | Everyone gets mailed eventually; still a fair comparison | Measures short-term effect only; equipment decisions can take longer |
| **Holdout in lower tiers only** | Mail every top-tier company; hold out a share of the "maybe" companies. | No risk to the best leads | Doesn't measure the effect on top leads |
| ⭐ **Personalized vs. generic card** | Half get the personalized card, half get a standard postcard. Nobody goes unmailed. | Directly tests the core pitch ("personalization pays") | Doesn't measure mail vs. no mail |
| **Offer vs. no offer** | Half the cards carry the incentive. | Shows whether the incentive is worth its cost | Fewer offer-code redemptions to track |
| **Territory comparison** | Run the campaign in some dealer territories and compare with similar territories that aren't mailed yet. | Easy to explain to dealers; no one inside a territory is left out | Territories differ; less precise |
| **Before / after** | Compare warranty registrations in the territory before and after. | No design needed | Weakest: seasonality and the economy get mixed in |

**Our suggestion for the pilot:** use QR codes, offer codes and warranty matchback to connect cards to sales. For cause and effect, combine a **"mail later" wave** with a **personalized vs. generic** split. Nobody is permanently skipped, and it answers the two questions leadership will ask: does mailing work, and is personalization worth it?

**An honest expectation:** one pilot of ~150 cards will show *direction*, not proof. Solid numbers come after several campaigns, which is another reason to start measuring from the first one.

## 5. Questions for the team

Priority: 🔴 needed before a pilot · 🟡 needed for the pilot to run well · ⚪ later.

### Customers & products
| # | Question | Pri | Who might know | Answer |
|---|---|---|---|---|
| Q1 | Which product(s) should the pilot focus on? | 🔴 | Sales leadership | |
| Q2 | Who buys today? Industries, company size, job titles of the decision maker. | 🔴 | Sales, dealers | |
| Q3 | Which kinds of companies never buy, or aren't worth the effort? | 🟡 | Sales, dealers | |
| Q4 | Typical deal size and time from first contact to purchase? | 🟡 | Sales leadership | |
| Q5 | Do customers buy, rent, or both? Are rental companies customers, competitors or dealers? | 🟡 | Sales | |
| Q6 | What makes a customer choose us over competitors? (Useful for postcard copy) | 🟡 | Sales, marketing | |

### Dealers & territories
| # | Question | Pri | Who might know | Answer |
|---|---|---|---|---|
| Q7 | Is there a dealer territory map (ZIP/county → dealer)? Exclusive or overlapping? Who maintains it? | 🔴 | Sales ops / channel manager | |
| Q8 | Which 1–2 dealers would be good pilot partners, and why? | 🔴 | Sales leadership | |
| Q9 | How many salespeople does a typical dealer have? How many new leads could they realistically work in a month? | 🟡 | Dealer managers | |
| Q10 | How do dealers receive leads from us today, and what happens to them? | 🟡 | Sales | |
| Q11 | Would dealers want to preview the list before mailing? How much time would they need? | 🟡 | Dealer managers | |
| Q12 | Do dealers have a CRM or portal we should send leads to, or is email best? | ⚪ | Sales ops | |
| Q13 | Is there a co-op marketing program or budget with dealers? | ⚪ | Marketing, finance | |

### Data we might use
| # | Question | Pri | Who might know | Answer |
|---|---|---|---|---|
| Q14 | Can marketing get warranty registration data? Which fields, how far back, how often? | 🔴 | Service / warranty team | |
| Q15 | Is any end-customer sales data reported by dealers (sales reports, rebate or co-op claims)? | 🟡 | Sales ops, finance | |
| Q16 | Is there a list of existing customers and "do not contact" companies we must exclude? | 🔴 | Sales ops | |
| Q17 | Any past mailers or campaigns and how they performed? (Our baseline) | 🟡 | Marketing | |

### HubSpot / Salesforce
| # | Question | Pri | Who might know | Answer |
|---|---|---|---|---|
| Q18 | Which system is used for what (HubSpot for marketing, Salesforce for sales/dealers)? | 🔴 | Marketing ops / IT | |
| Q19 | Which HubSpot subscription (Marketing Hub Starter/Pro/Enterprise)? Are landing pages, forms and workflows available? | 🔴 | HubSpot admin | |
| Q20 | Are dealers set up in HubSpot or Salesforce (as companies, partners, or a portal)? | 🟡 | CRM admin | |
| Q21 | Does anyone already use a direct-mail integration (Lob, PostGrid, Postalytics…) with HubSpot? | ⚪ | Marketing | |
| Q22 | Can we use a short web address on the brand's domain for QR codes (e.g., `go.brand.com`)? | 🟡 | IT / web team | |

### Offers, brand & approvals
| # | Question | Pri | Who might know | Answer |
|---|---|---|---|---|
| Q23 | Would the company fund an incentive tied to an offer code? What kind (free delivery, extended warranty, first service free, rebate)? How would dealers be reimbursed? | 🟡 | Sales leadership, finance | |
| Q24 | Is a "mail later" wave or a small holdout acceptable to leadership? What about personalized vs. generic? | 🟡 | Sales leadership | |
| Q25 | Brand kit, product photos (ideally several angles), dealer logos, co-branding rules? | 🔴 | Marketing | |
| Q26 | Who approves postcard content before it goes out? | 🔴 | Marketing, legal | |
| Q27 | Pricing, financing or promo rules the cards must follow? | 🟡 | Sales leadership | |
| Q28 | Comfort level with mentioning a prospect's public news (e.g., "congrats on your expansion") or showing their name on a sign in the picture? | 🟡 | Marketing, legal | |

### Budget & success
| # | Question | Pri | Who might know | Answer |
|---|---|---|---|---|
| Q29 | Rough budget for a pilot (postage, data, AI)? A pilot of ~150 cards is roughly $200–400 all-in. | 🔴 | Sales leadership | |
| Q30 | What result would make this worth continuing? (e.g., X responses, Y meetings, one sale) | 🔴 | Sales leadership | |

## 6. Strawman pilot

| Item | Proposal |
|---|---|
| Scope | 1 product line, 1–2 friendly dealers, 1 metro or dealer territory |
| Size | ~150–200 approved companies |
| Split | Wave 1 (mailed now) and wave 2 (mailed 6–8 weeks later); within wave 1, personalized vs. generic cards |
| Tracking | QR code + personal page + offer code per company; dealer alerts with one-click status; warranty matchback at 3, 6 and 12 months |
| Timeline | ~2 weeks to prepare the list and cards → mail → 90-day readout (plus a 12-month warranty check) |
| Readout | Response rate, dealer follow-up speed, quotes, wins; wave 1 vs. wave 2; personalized vs. generic; cost per response and per win |

## 7. What we'd need from the team

- [ ] Dealer territory map (spreadsheet)
- [ ] Warranty registrations (export, last 3–5 years)
- [ ] Existing-customer / do-not-contact list
- [ ] Brand kit: logo, colors, fonts; product photos
- [ ] Pilot dealer contacts (who gets lead alerts)
- [ ] HubSpot admin contact (and Salesforce, if relevant)
- [ ] A decision on the incentive (or "no incentive for the pilot")
- [ ] Postcard approver

## Glossary

| Term | Meaning |
|---|---|
| **Lead score** | 0–100 estimate of how good a fit a company is, with the reasons listed |
| **Signal** | A public sign a company may need equipment soon (permit, hiring, expansion) |
| **Offer code** | A short code on the card that the prospect mentions to get the incentive |
| **Matchback** | Checking which mailed companies later bought, using warranty or sales records |
| **Holdout / "mail later" wave** | Companies deliberately not mailed (yet), so we can compare and measure the real effect |
| **Attribution** | Connecting a sale back to the campaign that influenced it |
