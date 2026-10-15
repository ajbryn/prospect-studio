using System.Text.Json;
using ProspectStudio.Core.Candidates;
using ProspectStudio.Core.Leads;
using ProspectStudio.Mcp.Tests.Infrastructure;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// Implementation-plan C6: "filters combine correctly". Six filters are applied at once to a campaign
/// arranged so the answer is exactly one lead - and then each filter is loosened on its own, so a filter
/// that was accepted and ignored shows up as a test failure rather than as a wider list nobody counted.
/// </summary>
[Collection(LeadServerCollection.Name)]
public class LeadFilterCombinationTests(LeadServerFixture server) : IAsyncLifetime
{
    private const int Leads = 83;

    private static readonly Lock Gate = new();
    private static Task<Prepared>? _shared;

    private string _campaignId = string.Empty;
    private string _bayouLeadId = string.Empty;

    /// <summary>
    /// A campaign of its own, with research saved on two leads so all three
    /// <see cref="ResearchStatuses"/> values have a subject: Bayou Fulfillment is <c>saved</c> and tier A,
    /// Northline Glass is <c>no_signal</c>, and the other 81 are <c>none</c>.
    /// </summary>
    /// <remarks>
    /// Prepared once for the whole class rather than once per test. xUnit builds a new test instance per
    /// test, and every test here only reads, so repeating a <c>find_candidates</c> and two
    /// <c>save_research</c> calls nine times would add about twenty seconds for no extra coverage.
    /// </remarks>
    public async Task InitializeAsync()
    {
        Task<Prepared> prepared;
        lock (Gate)
        {
            _shared ??= PrepareAsync(server);
            prepared = _shared;
        }

        var ready = await prepared;
        _campaignId = ready.CampaignId;
        _bayouLeadId = ready.BayouLeadId;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<Prepared> PrepareAsync(LeadServerFixture server)
    {
        var campaignId = await server.NewScoredCampaignAsync();

        var bayouLeadId = await server.LeadIdForAsync(campaignId, WorkedExamples.BayouFulfillment.Company);
        var northlineLeadId = await server.LeadIdForAsync(
            campaignId,
            SamplePlaces.Row(WorkedExamples.NoSignalPlaceId).Name);

        await server.SaveResearchAsync(campaignId, bayouLeadId, WorkedExamples.BayouFulfillment.Research);
        await server.SaveResearchAsync(campaignId, northlineLeadId, SampleResearch.NorthlineGlassNoSignal());

        return new Prepared(campaignId, bayouLeadId);
    }

    private sealed record Prepared(string CampaignId, string BayouLeadId);

    [Fact]
    public async Task Every_research_status_has_its_own_slice_and_the_three_add_up()
    {
        var saved = await Total(new Dictionary<string, object?> { ["researchStatus"] = ResearchStatuses.Saved });
        var noSignal = await Total(new Dictionary<string, object?> { ["researchStatus"] = ResearchStatuses.NoSignal });
        var none = await Total(new Dictionary<string, object?> { ["researchStatus"] = ResearchStatuses.None });

        saved.ShouldBe(1, "research was saved on one lead with status 'researched'.");
        noSignal.ShouldBe(
            1,
            "and on one with status 'no_signal'. technical-design §5.2 keeps these apart because a "
            + "no_signal lead has been looked at and will never reach tier A - it must not be offered "
            + "again as unresearched work.");
        none.ShouldBe(Leads - 2, "the rest.");

        (saved + noSignal + none).ShouldBe(Leads, "the three slices partition the campaign.");
    }

    [Fact]
    public async Task An_unknown_research_status_is_VALIDATION_FAILED()
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "list_leads",
            new Dictionary<string, object?> { ["campaignId"] = _campaignId, ["researchStatus"] = "researched" },
            server.Diagnostics);

        error.Code.ShouldBe(
            "VALIDATION_FAILED",
            "'researched' is the research document's own status, not the lead column's - §5.2's values are "
            + "none/saved/no_signal. Accepting it and matching nothing would look like 'nobody has been "
            + "researched'. Raw: " + error.RawJson);
    }

    [Fact]
    public async Task Six_filters_at_once_narrow_to_the_one_lead_that_satisfies_all_of_them()
    {
        var page = await server.ListLeadsAsync(_campaignId, AllSix());

        page.GetProperty("total").GetInt32().ShouldBe(
            1,
            "status candidate, tier A, dealer gulf, minScore 80, researchStatus saved, sorted score_desc: "
            + $"only Bayou Fulfillment satisfies all six. Got: {page}");

        var row = page.GetProperty("rows").EnumerateArray().ShouldHaveSingleItem();

        row.GetProperty("id").GetString().ShouldBe(_bayouLeadId, $"Got: {row}");
        row.GetProperty("name").GetString().ShouldBe(WorkedExamples.BayouFulfillment.Company, $"Got: {row}");
        row.GetProperty("segment").GetString().ShouldBe(
            WorkedExamples.BayouFulfillment.SegmentName,
            "mcp-tools.md §list_leads' row shows the matched segment's name, which is what the marketer "
            + $"reads to see why the lead is there. Got: {row}");
        row.GetProperty("dealer").GetString().ShouldBe(
            SampleDealers.Dealer("gulf").DealerName,
            $"the row carries the dealer's name, not its id - as its own example does. Got: {row}");
        row.GetProperty("research").GetString().ShouldBe(ResearchStatuses.Saved, $"Got: {row}");
    }

    [Theory]
    [InlineData("status", "approved", "the lead is a candidate, not approved")]
    [InlineData("tier", "B", "it is tier A")]
    [InlineData("dealerId", "bay", "it is routed to gulf")]
    [InlineData("minScore", "101", "it scores 100")]
    [InlineData("researchStatus", "none", "its research is saved")]
    public async Task Loosening_any_one_of_the_six_filters_to_something_it_does_not_satisfy_empties_the_result(
        string filter,
        string value,
        string why)
    {
        // Each filter has to be load-bearing on its own. A filter that is accepted and ignored would leave
        // the combined query above returning one lead for the wrong reasons.
        var filters = AllSix();
        filters[filter] = filter switch
        {
            "status" or "tier" => new[] { value },
            "minScore" => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            _ => value,
        };

        var page = await server.ListLeadsAsync(_campaignId, filters);

        page.GetProperty("total").GetInt32().ShouldBe(
            0,
            $"with '{filter}' set to '{value}' nothing matches, because {why}. If this is 1, that filter "
            + $"is not being applied. Got: {page}");
        page.GetProperty("rows").EnumerateArray().ShouldBeEmpty($"Got: {page}");
    }

    [Fact]
    public async Task A_filter_that_matches_nothing_is_an_empty_list_rather_than_an_error()
    {
        var page = await server.ListLeadsAsync(_campaignId, new Dictionary<string, object?>
        {
            ["dealerId"] = "pine",
            ["tier"] = new[] { LeadTiers.A },
        });

        page.GetProperty("total").GetInt32().ShouldBe(
            0,
            $"no pine lead has research. Got: {page}");
        page.GetProperty("rows").EnumerateArray().ShouldBeEmpty($"Got: {page}");
    }

    [Fact]
    public async Task The_top_signal_on_a_researched_row_names_a_signal_and_when_it_happened()
    {
        var page = await server.ListLeadsAsync(_campaignId, AllSix());
        var row = page.GetProperty("rows").EnumerateArray().ShouldHaveSingleItem();

        var topSignal = row.GetProperty("topSignal").GetString() ?? string.Empty;

        topSignal.ShouldNotBeNullOrWhiteSpace(
            "mcp-tools.md §list_leads shows topSignal as 'Permit: 180k sq ft addition (2026-07)' - a type, "
            + "a sentence and a date, so the marketer can judge a lead without opening it. "
            + $"Got: {row}");

        topSignal.ShouldMatch(
            @"\d{4}-\d{2}",
            "the date is what makes a signal worth reading; 'Permit: addition' with no date could be from "
            + $"2019. Got: {row}");

        SignalTypes.All.ShouldContain(
            type => topSignal.Contains(type, StringComparison.OrdinalIgnoreCase),
            $"the row names which kind of signal it is. Got: {row}");
    }

    [Fact]
    public async Task The_top_signal_prefers_a_signal_that_actually_scored()
    {
        // mcp-tools.md §list_leads: "topSignal prefers a signal that actually scored - a buying signal
        // inside the recency window - falling back to the most recent otherwise, with its date always
        // shown." The manual check found a row advertising a 2019 expansion that contributed nothing, which
        // reads as the evidence behind the score and is the opposite of that.
        //
        // The dates here are the subject, so the research goes in exactly as written rather than through
        // the helper that re-dates signals.
        var campaignId = await server.NewScoredCampaignAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.WestparkMetalFab.Company);

        // The two rules have to be made to disagree, or the test proves nothing: a qualifying signal that is
        // also the newest would be picked by "prefers one that scored" and by a plain "most recent" alike.
        // So the non-scoring signal is the newer one - a registry entry filed this month, which §7.6 never
        // credits - and the signal that actually carried the score is six months old.
        var thisMonth = DateTimeOffset.UtcNow
            .ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var sixMonthsAgo = DateTimeOffset.UtcNow.AddMonths(-6)
            .ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

        var research = SampleResearch.With(
            WorkedExamples.WestparkMetalFab.Research,
            ("signals", new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = SignalTypes.Registry,
                    ["text"] = "Federal establishment record refreshed for the Houston plant.",
                    ["url"] = "https://registry.example.gov/establishment/westpark-metal-fab",
                    ["date"] = thisMonth,
                },
                new Dictionary<string, object?>
                {
                    ["type"] = SignalTypes.Permit,
                    ["text"] = "Commercial alteration permit filed for the main fabrication bay.",
                    ["url"] = "https://permits.example.gov/record/2026-5512",
                    ["date"] = sixMonthsAgo,
                },
            }));

        await server.SaveResearchExactAsync(campaignId, leadId, research);

        var row = await RowAsync(campaignId, leadId);
        var topSignal = row.GetProperty("topSignal").GetString() ?? string.Empty;

        row.GetProperty("score").GetInt32().ShouldBeGreaterThan(
            0,
            $"the permit is inside the window, so the lead scored on it. Got: {row}");

        topSignal.ShouldContain(
            SignalTypes.Permit,
            Case.Insensitive,
            "the permit is the signal that carried the score; the registry entry contributed 0 however "
            + $"recent it is. Picking the newest row instead names the wrong one. Got: {row}");
        topSignal.ShouldContain(sixMonthsAgo, Case.Sensitive, $"with its own date. Got: {row}");
        topSignal.ShouldNotContain(
            SignalTypes.Registry,
            Case.Insensitive,
            "a registry entry is never credited by §7.6, so advertising it as the top signal implies "
            + $"evidence that is not there. Got: {row}");
    }

    [Fact]
    public async Task The_top_signal_prefers_the_scoring_signal_even_against_one_of_the_same_type()
    {
        // The companion to the test above, which discriminates on signal *type*. This one discriminates on
        // the *window* alone: both signals are permits, so a rule that merely filtered to buying types would
        // still pick the newer one - and the newer one is dated next month, which §7.6 does not credit
        // because a future date is far more often a typo than a filed-for-later project.
        var campaignId = await server.NewScoredCampaignAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.WestparkMetalFab.Company);

        var nextMonth = DateTimeOffset.UtcNow.AddMonths(1)
            .ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var sixMonthsAgo = DateTimeOffset.UtcNow.AddMonths(-6)
            .ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

        var research = SampleResearch.With(
            WorkedExamples.WestparkMetalFab.Research,
            ("signals", new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = SignalTypes.Permit,
                    ["text"] = "Permit record carrying a transcription error in its filing date.",
                    ["url"] = "https://permits.example.gov/record/2027-0001",
                    ["date"] = nextMonth,
                },
                new Dictionary<string, object?>
                {
                    ["type"] = SignalTypes.Permit,
                    ["text"] = "Commercial alteration permit filed for the main fabrication bay.",
                    ["url"] = "https://permits.example.gov/record/2026-5512",
                    ["date"] = sixMonthsAgo,
                },
            }));

        await server.SaveResearchExactAsync(campaignId, leadId, research);

        var row = await RowAsync(campaignId, leadId);
        var topSignal = row.GetProperty("topSignal").GetString() ?? string.Empty;

        topSignal.ShouldContain(
            sixMonthsAgo,
            Case.Sensitive,
            "only the six-month-old permit is inside the window, so it is the one that scored. Naming the "
            + $"future-dated one advertises a mistyped date as the reason for the score. Got: {row}");
        topSignal.ShouldNotContain(nextMonth, Case.Sensitive, $"Got: {row}");
    }

    [Theory]
    [InlineData(SignalTypes.Expansion, "2019-05", "stale: outside the recency window")]
    [InlineData(SignalTypes.Registry, "2026-03", "a registry entry, which §7.6 never credits")]
    public async Task A_row_whose_only_signal_scored_nothing_still_shows_it_with_its_date(
        string type,
        string date,
        string why)
    {
        // The fallback half. Hiding the signal would be worse than the original bug: the marketer would see
        // a researched lead with no evidence at all and no way to tell it from an unresearched one. The date
        // is what lets them judge it - "Expansion: ... (2019-05)" is honest, the same line without the date
        // is not.
        var campaignId = await server.NewScoredCampaignAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.WestparkMetalFab.Company);

        var research = SampleResearch.With(
            WorkedExamples.WestparkMetalFab.Research,
            ("signals", new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = type,
                    ["text"] = "Opened the second bay on Energy Parkway.",
                    ["url"] = "https://news.example.com/westpark-second-bay",
                    ["date"] = date,
                },
            }));

        await server.SaveResearchExactAsync(campaignId, leadId, research);

        var row = await RowAsync(campaignId, leadId);
        var topSignal = row.GetProperty("topSignal").GetString() ?? string.Empty;

        topSignal.ShouldNotBeNullOrWhiteSpace(
            $"the only signal is {why}, but the lead has been researched and the row must say what was "
            + $"found. Got: {row}");
        topSignal.ShouldContain(type, Case.Insensitive, $"Got: {row}");
        topSignal.ShouldContain(
            date,
            Case.Sensitive,
            $"the date is what makes this readable as {why} rather than as fresh evidence. Got: {row}");
    }

    [Fact]
    public async Task An_unresearched_row_has_no_top_signal()
    {
        var page = await server.ListLeadsAsync(_campaignId, new Dictionary<string, object?>
        {
            ["researchStatus"] = ResearchStatuses.None,
            ["limit"] = 5,
        });

        foreach (var row in page.GetProperty("rows").EnumerateArray())
        {
            row.GetProperty("topSignal").ValueKind.ShouldBe(
                JsonValueKind.Null,
                "no research means no cited signal. A placeholder string here would read as evidence. "
                + $"Got: {row}");
        }
    }

    private Dictionary<string, object?> AllSix() => new(StringComparer.Ordinal)
    {
        ["status"] = new[] { LeadStatuses.Candidate },
        ["tier"] = new[] { LeadTiers.A },
        ["dealerId"] = "gulf",
        ["minScore"] = 80,
        ["researchStatus"] = ResearchStatuses.Saved,
        ["sort"] = LeadSorts.ScoreDesc,
    };

    /// <summary>One lead's <c>list_leads</c> row, found by walking the list.</summary>
    private async Task<JsonElement> RowAsync(string campaignId, string leadId)
    {
        var rows = await server.AllLeadsAsync(campaignId);

        return rows.SingleOrDefault(row => row.GetProperty("id").GetString() == leadId) is { ValueKind: JsonValueKind.Object } row
            ? row
            : throw new Xunit.Sdk.XunitException($"'{leadId}' is not in {campaignId}'s lead list.");
    }

    private async Task<int> Total(Dictionary<string, object?> filters)
    {
        filters["limit"] = 1;
        var page = await server.ListLeadsAsync(_campaignId, filters);
        return page.GetProperty("total").GetInt32();
    }
}
