using System.Text.Json;
using ProspectStudio.Core.Candidates;
using ProspectStudio.Core.Leads;
using ProspectStudio.Mcp.Tests.Infrastructure;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// <c>list_leads</c> and <c>get_lead</c> over a real scored campaign, against mcp-tools.md §list_leads
/// and §get_lead: compact rows inside the token budget, a correct <c>total</c> under paging, and filters
/// that each actually narrow the result.
/// </summary>
[Collection(LeadServerCollection.Name)]
public class ListLeadsContractTests(LeadServerFixture server)
{
    /// <summary>Leads the sample profile produces, as C5's tests pin it: 86 rows, three collapsed.</summary>
    private const int Leads = 83;

    /// <summary>Leads §7.3 removes.</summary>
    private const int Suppressed = 7;

    /// <summary>
    /// Rows §7.2 collapsed into another lead. They are not leads and are in no lead-facing total.
    /// </summary>
    private const int Duplicates = 3;

    private const int Candidates = Leads - Suppressed;

    [Fact]
    public async Task A_page_of_rows_carries_the_columns_the_contract_shows()
    {
        var page = await server.ListLeadsAsync(server.ScoredCampaignId, new Dictionary<string, object?>
        {
            ["limit"] = LeadResponseLimits.DefaultPageSize,
        });

        page.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["total", "rows"],
            ignoreOrder: true,
            $"mcp-tools.md §list_leads returns {{ total, rows }}. Got: {Trim(page)}");

        var row = page.GetProperty("rows").EnumerateArray().First();

        row.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["id", "name", "city", "segment", "score", "tier", "dealer", "status", "research", "topSignal"],
            ignoreOrder: true,
            "mcp-tools.md §list_leads' row example. These ten and no more: the response budget is about "
            + $"4,000 tokens and the full lead is what get_lead is for. Got: {row}");

        ResearchStatuses.IsKnown(row.GetProperty("research").GetString()).ShouldBeTrue(
            "technical-design §5.2: research_status is none/saved/no_signal. Got: " + row);
    }

    [Fact]
    public async Task Twenty_five_researched_rows_fit_in_the_response_budget()
    {
        // Measured on a page where every row has a topSignal. The shared fixture campaign has no research
        // at all, so every topSignal there is null - the most optimistic payload the response can produce,
        // which is the wrong thing to measure an NFR against. topSignal is also the longest field in the
        // row: the one used here is a 66-character permit sentence, longer than the contract's own example.
        var campaignId = await server.NewScoredCampaignAsync();
        var research = WorkedExamples.BayouFulfillment.Research;

        // The same document on 25 leads. It is fictional for 24 of them, which costs nothing here: what is
        // under test is how many bytes a full row of this shape takes, not whose company it describes.
        var candidates = (await server.AllLeadsAsync(campaignId))
            .Where(row => row.GetProperty("status").GetString() == LeadStatuses.Candidate)
            .Take(LeadResponseLimits.DefaultPageSize)
            .Select(row => row.GetProperty("id").GetString()!)
            .ToList();

        candidates.Count.ShouldBe(LeadResponseLimits.DefaultPageSize, "the campaign has 76 candidates.");

        foreach (var leadId in candidates)
        {
            await server.SaveResearchAsync(campaignId, leadId, research);
        }

        var page = await server.ListLeadsAsync(campaignId, new Dictionary<string, object?>
        {
            ["researchStatus"] = ResearchStatuses.Saved,
            ["limit"] = LeadResponseLimits.DefaultPageSize,
        });

        var rows = page.GetProperty("rows").EnumerateArray().ToList();

        rows.Count.ShouldBe(LeadResponseLimits.DefaultPageSize, $"Got: {Trim(page)}");
        rows.ShouldAllBe(
            row => row.GetProperty("topSignal").ValueKind == JsonValueKind.String,
            "every row on this page is researched, so every one carries a topSignal. A page of nulls would "
            + $"make the measurement below meaningless. Got: {Trim(page)}");

        var bytes = JsonSerializer.Serialize(page).Length;

        bytes.ShouldBeLessThan(
            16 * 1024,
            "implementation-plan C6 and NFR-2: 25 rows must serialize to under 16 KB, which is roughly "
            + $"4,000 tokens. This page was {bytes} bytes. If it is over, something per-lead that belongs "
            + $"in get_lead has been added to the row.{Environment.NewLine}{Trim(page)}");
    }

    [Fact]
    public async Task The_total_is_every_matching_lead_not_the_page()
    {
        var page = await server.ListLeadsAsync(server.ScoredCampaignId, new Dictionary<string, object?>
        {
            ["limit"] = 5,
        });

        page.GetProperty("rows").EnumerateArray().Count().ShouldBe(5);
        page.GetProperty("total").GetInt32().ShouldBe(
            Leads,
            "mcp-tools.md §list_leads: 'total: 812' alongside one page of rows. A total that reported the "
            + $"page size would make paging impossible. Got: {Trim(page)}");

        // Cross-checked against the campaign's own counts, so the two summaries cannot drift apart.
        var campaign = await ToolCall.OkAsync(
            server.Client,
            "get_campaign",
            new Dictionary<string, object?> { ["campaignId"] = server.ScoredCampaignId },
            server.Diagnostics);

        campaign.GetProperty("counts").GetProperty("byStatus").EnumerateObject()
            .Sum(status => status.Value.GetInt32())
            .ShouldBe(
                page.GetProperty("total").GetInt32(),
                "an unfiltered list_leads and get_campaign's byStatus are counting the same leads.");
    }

    [Theory]
    [InlineData(LeadSorts.ScoreDesc)]
    [InlineData(LeadSorts.NameAsc)]
    public async Task Paging_walks_every_lead_exactly_once_whatever_the_page_size(string sort)
    {
        // mcp-tools.md §list_leads: every sort carries leadId as a final tiebreak. Scores tie constantly -
        // a page of leads all on 52 has no defined order without it - and offset/limit over a non-unique
        // key both repeats and skips rows. The union is the property that actually matters, and it fails
        // loudly instead of flaking: moving the page boundaries is what exposes an unstable order, so the
        // same walk is done at two page sizes and the two sequences have to agree exactly.
        var inPagesOfTwentyFive = await server.AllLeadsAsync(server.ScoredCampaignId, sort);
        var inPagesOfSeven = await server.AllLeadsAsync(server.ScoredCampaignId, sort, pageSize: 7);

        var ids = Ids(inPagesOfTwentyFive);
        var oddPageIds = Ids(inPagesOfSeven);

        ids.Count.ShouldBe(
            Leads,
            $"'{sort}' in pages of 25: four pages cover {Leads} leads. Got {ids.Count}.");
        oddPageIds.Count.ShouldBe(
            Leads,
            $"'{sort}' in pages of 7: twelve pages cover the same {Leads}. Got {oddPageIds.Count}.");

        ids.Distinct(StringComparer.Ordinal).Count().ShouldBe(
            Leads,
            $"'{sort}' returned a lead on two pages, which means the sort is not totally ordered. "
            + $"Repeated: {string.Join(", ", ids.GroupBy(id => id, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key))}");

        ids.Order(StringComparer.Ordinal).ShouldBe(
            oddPageIds.Order(StringComparer.Ordinal),
            $"'{sort}' returned a different set of leads at a different page size, so some lead is "
            + "reachable only when the boundaries happen to fall kindly. Missing from the pages of 7: "
            + $"{string.Join(", ", ids.Except(oddPageIds, StringComparer.Ordinal))}");

        oddPageIds.ShouldBe(
            ids,
            $"'{sort}' put the leads in a different order at a different page size. A total order gives "
            + "the same sequence however it is sliced; anything else means ties are being broken by "
            + "whatever the database felt like.");
    }

    [Fact]
    public async Task Name_asc_orders_on_the_normalized_name_so_the_sequence_survives_a_provider_swap()
    {
        // CLAUDE.md: match on the normalized columns rather than depending on the provider's text
        // comparison. Ordering on sites.name would make the sequence depend on the collation - SQLite's
        // default is case-sensitive, SQL Server's is not - so the list would reorder itself under the
        // provider swap the neutrality rule exists to keep cheap.
        var rows = await server.AllLeadsAsync(server.ScoredCampaignId, LeadSorts.NameAsc);
        var names = rows.Select(row => row.GetProperty("name").GetString() ?? string.Empty).ToList();

        names.Count.ShouldBe(Leads);

        var normalized = names.Select(NameNormalizer.Normalize).ToList();

        normalized.ShouldBe(
            [.. normalized.Order(StringComparer.Ordinal)],
            "the sequence has to be ordered by name_norm. Got: " + string.Join(" | ", normalized));

        // name_norm is lowercase ASCII by construction (§7.1), so an ordinal and a case-insensitive
        // comparison cannot disagree about it. That is the whole point: the answer is the same whichever
        // collation the provider brings, which is what ordering on the raw name cannot promise.
        List<string> caseSensitive = [.. normalized.Order(StringComparer.Ordinal)];
        List<string> caseInsensitive = [.. normalized.Order(StringComparer.OrdinalIgnoreCase)];

        caseSensitive.ShouldBe(
            caseInsensitive,
            "a name_norm value with an uppercase letter in it would make this ordering collation-dependent "
            + "again, which would mean §7.1's normalizer had stopped lower-casing.");

        // The decisive row, and the reason fx_0112 carries 'The ... Company, LLC': normalization strips the
        // leading 'the' and the trailing suffixes, moving it from the T block to the W block. Ordering on
        // the raw name puts it before both of these under either collation; ordering on name_norm puts it
        // after both.
        const string longName = "The Waller County Industrial Park Facilities Management Company, LLC";

        names.ShouldContain(longName, "fx_0112 is a candidate lead in this campaign.");

        foreach (var earlier in new[] { "Tidewater Lift Systems", "Tomball Precision Machining" })
        {
            names.IndexOf(earlier).ShouldBeLessThan(
                names.IndexOf(longName),
                $"'{earlier}' normalizes to '{NameNormalizer.Normalize(earlier)}', which sorts before "
                + $"'{NameNormalizer.Normalize(longName)}'. Raw-name ordering reverses this pair, so a "
                + "failure here means the sort is still on sites.name.");
        }
    }

    [Fact]
    public async Task A_page_past_the_end_is_empty_and_still_reports_the_total()
    {
        var page = await server.ListLeadsAsync(server.ScoredCampaignId, new Dictionary<string, object?>
        {
            ["limit"] = 25,
            ["offset"] = 10_000,
        });

        page.GetProperty("rows").EnumerateArray().ShouldBeEmpty($"Got: {Trim(page)}");
        page.GetProperty("total").GetInt32().ShouldBe(
            Leads,
            $"an offset past the end is not an error and does not change the total. Got: {Trim(page)}");
    }

    [Fact]
    public async Task Two_identical_calls_return_the_same_order()
    {
        var filters = new Dictionary<string, object?>
        {
            ["limit"] = LeadResponseLimits.DefaultPageSize,
            ["sort"] = LeadSorts.ScoreDesc,
        };

        var first = await server.ListLeadsAsync(server.ScoredCampaignId, filters);
        var second = await server.ListLeadsAsync(server.ScoredCampaignId, filters);

        Ids(second).ShouldBe(Ids(first), "paging is only safe if the order is stable between calls.");
    }

    [Fact]
    public async Task Score_desc_really_sorts_by_score_descending()
    {
        var rows = await server.AllLeadsAsync(server.ScoredCampaignId);

        var scores = rows
            .Select(row => row.GetProperty("score").ValueKind == JsonValueKind.Null
                ? (int?)null
                : row.GetProperty("score").GetInt32())
            .ToList();

        var scored = scores.Where(score => score is not null).Select(score => score!.Value).ToList();

        scored.ShouldBe(
            [.. scored.OrderByDescending(score => score)],
            "mcp-tools.md §list_leads: sort 'score_desc'. The first page is what the marketer reviews, so "
            + "a wrong order puts the wrong leads in front of them.");
    }

    [Fact]
    public async Task A_status_filter_narrows_to_that_status()
    {
        var page = await server.ListLeadsAsync(server.ScoredCampaignId, new Dictionary<string, object?>
        {
            ["status"] = new[] { LeadStatuses.Candidate },
            ["limit"] = 1,
        });

        page.GetProperty("total").GetInt32().ShouldBe(
            Candidates,
            $"{Leads} leads less the {Suppressed} §7.3 removed. Got: {Trim(page)}");

        var suppressed = await server.ListLeadsAsync(server.ScoredCampaignId, new Dictionary<string, object?>
        {
            ["status"] = new[] { LeadStatuses.Suppressed },
            ["limit"] = 1,
        });

        suppressed.GetProperty("total").GetInt32().ShouldBe(Suppressed, $"Got: {Trim(suppressed)}");
    }

    [Fact]
    public async Task Duplicates_are_hidden_from_every_total_but_reachable_by_naming_their_status()
    {
        // mcp-tools.md §score_leads: "duplicate rows are not leads at all and appear in no lead-facing
        // total - not scored, not skipped, not get_campaign's byStatus, and not an unfiltered list_leads -
        // unless a status filter names them explicitly." §7.2 keeps the highest-confidence record as the
        // lead and marks the rest duplicate, so a duplicate is provenance for the dedupe decision rather
        // than a prospect. The status filter is the one documented way to look at them, and nothing was
        // checking that it works.
        var duplicates = await server.ListLeadsAsync(server.ScoredCampaignId, new Dictionary<string, object?>
        {
            ["status"] = new[] { LeadStatuses.Duplicate },
            ["limit"] = LeadResponseLimits.DefaultPageSize,
        });

        duplicates.GetProperty("total").GetInt32().ShouldBe(
            Duplicates,
            "C4's arithmetic: 86 rows survive the profile and three collapse into other leads. "
            + $"Got: {Trim(duplicates)}");

        var rows = duplicates.GetProperty("rows").EnumerateArray().ToList();

        rows.Count.ShouldBe(Duplicates, $"Got: {Trim(duplicates)}");
        rows.ShouldAllBe(
            row => row.GetProperty("status").GetString() == LeadStatuses.Duplicate,
            $"Got: {Trim(duplicates)}");
        rows.ShouldAllBe(
            row => row.GetProperty("score").ValueKind == JsonValueKind.Null,
            "score_leads does not count duplicates, so none of them is scored - a scored duplicate would "
            + $"turn up in a tier count as a prospect that does not exist. Got: {Trim(duplicates)}");

        var unfiltered = await server.ListLeadsAsync(server.ScoredCampaignId, new Dictionary<string, object?>
        {
            ["limit"] = 1,
        });

        unfiltered.GetProperty("total").GetInt32().ShouldBe(
            Leads,
            $"{Leads} leads, with the {Duplicates} duplicates left out. Got: {Trim(unfiltered)}");

        var campaign = await ToolCall.OkAsync(
            server.Client,
            "get_campaign",
            new Dictionary<string, object?> { ["campaignId"] = server.ScoredCampaignId },
            server.Diagnostics);

        campaign.GetProperty("counts").GetProperty("byStatus")
            .EnumerateObject()
            .Select(status => status.Name)
            .ShouldNotContain(
                LeadStatuses.Duplicate,
                "a 'duplicate: 3' line in the campaign summary reads as three more prospects. "
                + $"Got: {campaign.GetProperty("counts")}");
    }

    [Fact]
    public async Task Two_statuses_in_one_call_return_the_union()
    {
        var page = await server.ListLeadsAsync(server.ScoredCampaignId, new Dictionary<string, object?>
        {
            ["status"] = new[] { LeadStatuses.Candidate, LeadStatuses.Suppressed },
            ["limit"] = 1,
        });

        page.GetProperty("total").GetInt32().ShouldBe(
            Leads,
            "mcp-tools.md §list_leads passes status as an array, which means 'any of these'. "
            + $"Got: {Trim(page)}");
    }

    [Fact]
    public async Task A_dealer_filter_narrows_to_that_dealers_leads()
    {
        // C5 pinned the routing: gulf 32, bay 24, pine 19, plus one coverage gap and seven suppressed.
        foreach (var (dealerId, expected) in new[] { ("gulf", 32), ("bay", 24), ("pine", 19) })
        {
            var page = await server.ListLeadsAsync(server.ScoredCampaignId, new Dictionary<string, object?>
            {
                ["dealerId"] = dealerId,
                ["status"] = new[] { LeadStatuses.Candidate },
                ["limit"] = 1,
            });

            page.GetProperty("total").GetInt32().ShouldBe(
                expected,
                $"C5's routing gave '{dealerId}' {expected} candidate leads. Got: {Trim(page)}");
        }
    }

    [Fact]
    public async Task With_no_research_anywhere_no_lead_reaches_tier_A()
    {
        // §7.6's load-bearing property, end to end: the base maxes out at 75 without a cited buying
        // signal, so a campaign nobody has researched cannot contain a tier A lead however good the
        // companies are. A tier A here means the ceiling has been broken somewhere in the pipeline.
        var page = await server.ListLeadsAsync(server.ScoredCampaignId, new Dictionary<string, object?>
        {
            ["tier"] = new[] { LeadTiers.A },
            ["limit"] = 5,
        });

        page.GetProperty("total").GetInt32().ShouldBe(
            0,
            "§7.6: 'Without research signals the base maxes out at 75, so tier A requires cited "
            + $"evidence.' Got: {Trim(page)}");

        var scores = (await server.AllLeadsAsync(server.ScoredCampaignId))
            .Select(row => row.GetProperty("score"))
            .Where(score => score.ValueKind == JsonValueKind.Number)
            .Select(score => score.GetInt32())
            .ToList();

        scores.ShouldNotBeEmpty("score_leads ran in the fixture, so the leads have scores.");
        scores.Max().ShouldBeLessThanOrEqualTo(
            LeadTiers.BaseCeilingWithoutSignals,
            "no research has been saved in this campaign, so 75 is the arithmetic ceiling - and with no "
            + "research there is no llmAdjustment either, so §7.6's cap at 79 never even comes into it.");
    }

    [Fact]
    public async Task A_minScore_filter_drops_everything_below_it()
    {
        var all = await server.ListLeadsAsync(server.ScoredCampaignId, new Dictionary<string, object?>
        {
            ["limit"] = 1,
        });
        var above = await server.ListLeadsAsync(server.ScoredCampaignId, new Dictionary<string, object?>
        {
            ["minScore"] = 50,
            ["limit"] = LeadResponseLimits.DefaultPageSize,
        });

        above.GetProperty("total").GetInt32().ShouldBeLessThan(
            all.GetProperty("total").GetInt32(),
            "the fixture campaign has leads below 50, so the filter has to remove some. "
            + $"Got: {Trim(above)}");

        foreach (var row in above.GetProperty("rows").EnumerateArray())
        {
            row.GetProperty("score").GetInt32().ShouldBeGreaterThanOrEqualTo(50, $"Got: {row}");
        }

        var impossible = await server.ListLeadsAsync(server.ScoredCampaignId, new Dictionary<string, object?>
        {
            ["minScore"] = 101,
            ["limit"] = 1,
        });

        impossible.GetProperty("total").GetInt32().ShouldBe(0, $"Got: {Trim(impossible)}");
    }

    [Fact]
    public async Task An_unknown_sort_value_is_VALIDATION_FAILED_rather_than_a_silent_default()
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "list_leads",
            new Dictionary<string, object?> { ["campaignId"] = server.ScoredCampaignId, ["sort"] = "scoredesc" },
            server.Diagnostics);

        error.Code.ShouldBe(
            "VALIDATION_FAILED",
            "falling back to the default would hand back a differently ordered list than the caller asked "
            + "for and say nothing. Raw: " + error.RawJson);
    }

    [Fact]
    public async Task An_unknown_campaign_is_NOT_FOUND()
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "list_leads",
            new Dictionary<string, object?> { ["campaignId"] = "cmp_NOPE99" },
            server.Diagnostics);

        error.Code.ShouldBe("NOT_FOUND", "mcp-tools.md §Errors. Raw: " + error.RawJson);
        error.Hint.ShouldNotBeNullOrWhiteSpace("the hint should point at list_campaigns.");
    }

    [Fact]
    public async Task Get_lead_returns_the_full_lead_with_a_breakdown_that_explains_its_score()
    {
        var leadId = await server.LeadIdForAsync(server.ScoredCampaignId, WorkedExamples.BayouFulfillment.Company);

        var lead = await server.GetLeadAsync(server.ScoredCampaignId, leadId);

        foreach (var key in new[]
                 {
                     "id", "name", "status", "address", "city", "state", "zip", "website", "phone",
                     "confidence", "features", "score", "tier", "scoreBreakdown", "research", "signals",
                     "researchStatus", "dealerId", "dealer", "branchId", "branch", "assignment", "notes",
                     "contactName", "contactTitle", "code", "cohort",
                 })
        {
            lead.TryGetProperty(key, out _).ShouldBeTrue(
                $"mcp-tools.md §get_lead: 'full lead: site fields, provenance, features, scoreBreakdown[], "
                + $"research, signals, dealer/branch, code, cohort, notes'. Missing '{key}'. Got: {Trim(lead)}");
        }

        lead.GetProperty("id").GetString().ShouldBe(leadId);

        // mcp-tools.md §get_lead: code (C11) and cohort (C13) "are present and null until those chunks
        // populate them, so the response shape does not change later". A key that appears in C11 would make
        // every skill that reads this response conditional on which chunk had shipped.
        lead.GetProperty("code").ValueKind.ShouldBe(
            JsonValueKind.Null,
            $"tracking codes arrive in C11. Got: {Trim(lead)}");
        lead.GetProperty("cohort").ValueKind.ShouldBe(
            JsonValueKind.Null,
            $"cohorts arrive in C13. Got: {Trim(lead)}");

        var breakdown = lead.GetProperty("scoreBreakdown").EnumerateArray().ToList();

        breakdown.Select(row => row.GetProperty("feature").GetString()).ShouldBe(
            ScoreFeatures.All,
            ignoreOrder: true,
            "§7.6: 'score_breakdown_json stores each feature, weight and contribution so Claude can "
            + $"explain any score.' Got: {Trim(lead)}");

        foreach (var row in breakdown)
        {
            foreach (var key in new[] { "feature", "value", "weight", "contribution" })
            {
                row.TryGetProperty(key, out _).ShouldBeTrue($"a breakdown row needs '{key}'. Got: {row}");
            }
        }

        lead.GetProperty("signals").ValueKind.ShouldBe(
            JsonValueKind.Array,
            $"signals is a list even when it is empty. Got: {Trim(lead)}");
    }

    [Fact]
    public async Task The_richest_single_lead_still_fits_in_the_response_budget()
    {
        // get_lead is the biggest lead-facing response: a whole research document, its signals, six
        // breakdown rows, scoreDetail, the features object and every site field. CLAUDE.md's "default
        // responses fit in about 4,000 tokens" applies to it as much as to a page of rows, and nothing was
        // measuring it - so a research document that grew, or a features object that started carrying the
        // whole website excerpt, would show up first as a truncated reply in Claude Desktop.
        var campaignId = await server.NewScoredCampaignAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.BayouFulfillment.Company);

        await server.SaveResearchAsync(campaignId, leadId, WorkedExamples.BayouFulfillment.Research);

        var lead = await server.GetLeadAsync(campaignId, leadId, includeWebExcerpt: true);
        var bytes = JsonSerializer.Serialize(lead).Length;

        lead.GetProperty("research").ValueKind.ShouldBe(
            JsonValueKind.Object,
            "the measurement is only worth anything on a lead that actually carries research. "
            + $"Got: {Trim(lead)}");
        lead.GetProperty("signals").EnumerateArray().Count().ShouldBe(2, $"Got: {Trim(lead)}");

        bytes.ShouldBeLessThan(
            16 * 1024,
            $"NFR-2. This lead was {bytes} bytes with research, two signals, six breakdown rows and "
            + $"scoreDetail.{Environment.NewLine}{Trim(lead)}");
    }

    [Fact]
    public async Task Get_lead_returns_a_null_web_excerpt_until_chunk_C7_fetches_websites()
    {
        // mcp-tools.md §get_lead: "The web excerpt is likewise null until C7 fetches websites - C7 owns the
        // 1,500-char cap and the test for it." The cap genuinely cannot be exercised here: web_pages arrives
        // with C7, so there is nothing to truncate, and an assertion over an always-empty value would pass
        // whatever the implementation did with a real excerpt.
        var leadId = await server.LeadIdForAsync(server.ScoredCampaignId, WorkedExamples.BayouFulfillment.Company);

        var lead = await server.GetLeadAsync(server.ScoredCampaignId, leadId, includeWebExcerpt: true);

        lead.TryGetProperty("webExcerpt", out var excerpt).ShouldBeTrue(
            $"the key is present from C6 so C7 changes a value rather than the shape. Got: {Trim(lead)}");
        excerpt.ValueKind.ShouldBe(
            JsonValueKind.Null,
            $"nothing has been fetched, and an empty string would read as 'fetched, no text'. Got: {Trim(lead)}");
    }

    [Fact]
    public async Task Get_lead_for_an_unknown_lead_is_NOT_FOUND()
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "get_lead",
            new Dictionary<string, object?> { ["campaignId"] = server.ScoredCampaignId, ["leadId"] = "L9999" },
            server.Diagnostics);

        error.Code.ShouldBe("NOT_FOUND", "mcp-tools.md §Errors. Raw: " + error.RawJson);
    }

    private static List<string?> Ids(JsonElement page) =>
        [.. page.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("id").GetString())];

    /// <summary>The lead ids of a walked list, in the order the pages returned them.</summary>
    private static List<string> Ids(IReadOnlyList<JsonElement> rows) =>
        [.. rows.Select(row => row.GetProperty("id").GetString() ?? string.Empty)];

    /// <summary>A response cut down for a failure message, so a 25-row page does not fill the log.</summary>
    private static string Trim(JsonElement payload)
    {
        var text = payload.ToString();
        return text.Length <= 1200 ? text : text[..1200] + "…";
    }
}
