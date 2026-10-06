using System.Text.Json;
using ProspectStudio.Core.Candidates;
using ProspectStudio.Core.Leads;
using ProspectStudio.Mcp.Tests.Infrastructure;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// <c>save_research</c> against mcp-tools.md §save_research: the spec pack's valid document is accepted,
/// stored and re-scored with a signed delta; every case in <c>sample-research-invalid.json</c> is
/// refused as <c>VALIDATION_FAILED</c> at the right pointer; and <c>status: "no_signal"</c> with an empty
/// <c>signals</c> array is accepted.
/// </summary>
[Collection(LeadServerCollection.Name)]
public class SaveResearchContractTests(LeadServerFixture server)
{
    public static TheoryData<string> InvalidCases() =>
        [.. SampleResearch.InvalidCases.Select(entry => entry.Case)];

    [Fact]
    public async Task The_valid_document_is_accepted_and_the_lead_is_re_scored_with_the_delta()
    {
        var campaignId = await server.NewScoredCampaignAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.BayouFulfillment.Company);

        var before = await server.GetLeadAsync(campaignId, leadId);

        before.GetProperty("score").GetInt32().ShouldBe(
            52,
            "unresearched, Bayou Fulfillment scores 0.25×1.0 + 0.15×0.5 + 0.20×0.4 + 0 + 0.05×1.0 + "
            + "0.10×0.665 = 0.5215 → 52: segment matched by category, size and facility unknown, no "
            + $"signals, inside 25 mi of gulf-west, Overture confidence 0.95. Got: {Trim(before)}");
        before.GetProperty("researchStatus").GetString().ShouldBe(ResearchStatuses.None);

        var saved = await server.SaveResearchAsync(
            campaignId,
            leadId,
            WorkedExamples.BayouFulfillment.Research);

        saved.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["saved", "score", "tier", "delta"],
            ignoreOrder: true,
            $"mcp-tools.md §save_research: {{ saved, score, tier, delta }}. Got: {saved}");

        saved.GetProperty("saved").GetBoolean().ShouldBeTrue($"Got: {saved}");
        saved.GetProperty("score").GetInt32().ShouldBe(100, $"Got: {saved}");
        saved.GetProperty("tier").GetString().ShouldBe(LeadTiers.A, $"Got: {saved}");
        saved.GetProperty("delta").GetInt32().ShouldBe(
            48,
            "mcp-tools.md §save_research: 'delta is signed'. 100 − 52 = 48, which is the number that tells "
            + $"the marketer the research was worth doing. Got: {saved}");

        var after = await server.GetLeadAsync(campaignId, leadId);

        after.GetProperty("score").GetInt32().ShouldBe(100, $"Got: {Trim(after)}");
        after.GetProperty("researchStatus").GetString().ShouldBe(
            ResearchStatuses.Saved,
            "technical-design §5.2: research_status is 'saved' for a document whose own status is "
            + $"'researched'. Got: {Trim(after)}");
        after.GetProperty("research").ValueKind.ShouldBe(
            JsonValueKind.Object,
            $"get_lead returns the stored document. Got: {Trim(after)}");
        after.GetProperty("signals").EnumerateArray().Count().ShouldBe(
            2,
            "the document's two signals are extracted into the signals table, so §7.6 and list_leads' "
            + $"topSignal can read them as rows. Got: {Trim(after)}");
    }

    [Fact]
    public async Task Research_on_a_lead_that_has_never_been_scored_reports_a_null_delta()
    {
        // mcp-tools.md §save_research: "delta ... is null when the lead had no previous score." A 0 would
        // claim the research changed nothing, which is a different and false statement.
        var campaignId = await server.NewCampaignWithCandidatesAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.BayouFulfillment.Company);

        var before = await server.GetLeadAsync(campaignId, leadId);
        before.GetProperty("score").ValueKind.ShouldBe(
            JsonValueKind.Null,
            $"score_leads has not run on this campaign. Got: {Trim(before)}");

        var saved = await server.SaveResearchAsync(
            campaignId,
            leadId,
            WorkedExamples.BayouFulfillment.Research);

        saved.GetProperty("delta").ValueKind.ShouldBe(
            JsonValueKind.Null,
            $"there is no previous score to subtract. Got: {saved}");
        saved.GetProperty("score").GetInt32().ShouldBe(
            100,
            $"the lead is scored by this call even though nothing scored it before. Got: {saved}");
    }

    [Fact]
    public async Task Research_that_lowers_a_score_reports_a_negative_delta()
    {
        // The signed half of the contract. A no_signal document on a scored lead replaces "size unknown
        // = 0.5" with nothing better and pins facilityFit at 'unknown' = 0.4, so the score moves down.
        var campaignId = await server.NewScoredCampaignAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.BayouFulfillment.Company);

        var withSignals = await server.SaveResearchAsync(
            campaignId,
            leadId,
            WorkedExamples.BayouFulfillment.Research);
        var replaced = await server.SaveResearchAsync(
            campaignId,
            leadId,
            SampleResearch.NorthlineGlassNoSignal());

        withSignals.GetProperty("score").GetInt32().ShouldBe(100);
        replaced.GetProperty("score").GetInt32().ShouldBeLessThan(
            100,
            $"the second document cites nothing. Got: {replaced}");
        replaced.GetProperty("delta").GetInt32().ShouldBeLessThan(
            0,
            "mcp-tools.md §save_research: 'delta is signed (negative when research lowers the score)'. "
            + $"Got: {replaced}");

        var lead = await server.GetLeadAsync(campaignId, leadId);
        lead.GetProperty("signals").EnumerateArray().ShouldBeEmpty(
            "saving a document replaces the previous one, signals included. Leaving the old rows behind "
            + $"would keep scoring evidence the researcher has withdrawn. Got: {Trim(lead)}");
    }

    [Fact]
    public async Task No_signal_with_an_empty_signals_array_is_accepted()
    {
        var campaignId = await server.NewScoredCampaignAsync();
        var leadId = await server.LeadIdForAsync(campaignId, SamplePlaces.Row(WorkedExamples.NoSignalPlaceId).Name);

        var saved = await server.SaveResearchAsync(
            campaignId,
            leadId,
            SampleResearch.NorthlineGlassNoSignal());

        saved.GetProperty("saved").GetBoolean().ShouldBeTrue(
            "mcp-tools.md §save_research: 'status: \"no_signal\" allowed with an empty signals'. "
            + $"Got: {saved}");

        var lead = await server.GetLeadAsync(campaignId, leadId);

        lead.GetProperty("researchStatus").GetString().ShouldBe(
            ResearchStatuses.NoSignal,
            "technical-design §5.2 keeps 'no_signal' distinct from 'none': this lead has been looked at "
            + $"and will never reach tier A, which is worth knowing. Got: {Trim(lead)}");
        lead.GetProperty("tier").GetString().ShouldNotBe(
            LeadTiers.A,
            $"no cited buying signal, so §7.6's ceiling applies. Got: {Trim(lead)}");
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public async Task A_rejected_document_is_VALIDATION_FAILED_with_a_pointer_at_the_offending_value(string caseName)
    {
        var entry = SampleResearch.InvalidCase(caseName);
        var campaignId = server.RejectionCampaignId;
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.BayouFulfillment.Company);

        var error = await ToolCall.ErrorAsync(
            server.Client,
            "save_research",
            new Dictionary<string, object?>
            {
                ["campaignId"] = campaignId,
                ["leadId"] = leadId,
                ["research"] = entry.Research,
            },
            server.Diagnostics);

        error.Code.ShouldBe(
            "VALIDATION_FAILED",
            $"'{entry.Case}' violates poc/schemas/research.schema.json ({entry.Reason}). Raw: {error.RawJson}");

        error.Details.ShouldNotBeEmpty(
            "mcp-tools.md §Errors: VALIDATION_FAILED 'include details[]'. Without them Claude cannot tell "
            + "the user which field to fix. Raw: " + error.RawJson);

        error.Details.ShouldContain(
            detail => detail.Pointer == entry.Pointer
                || detail.Pointer.StartsWith(entry.Pointer + "/", StringComparison.Ordinal),
            $"'{entry.Case}' claims the problem is at '{entry.Pointer}'. A pointer one level deeper is "
            + "fine - C1's convention synthesizes the pointer of a missing member - but a pointer "
            + "somewhere else sends the reader hunting. Raw: " + error.RawJson);
    }

    [Fact]
    public async Task A_rejected_document_is_not_stored_and_leaves_the_score_alone()
    {
        var campaignId = await server.NewScoredCampaignAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.BayouFulfillment.Company);

        var before = await server.GetLeadAsync(campaignId, leadId);

        await ToolCall.ErrorAsync(
            server.Client,
            "save_research",
            new Dictionary<string, object?>
            {
                ["campaignId"] = campaignId,
                ["leadId"] = leadId,
                ["research"] = SampleResearch.InvalidCase("signal without url").Research,
            },
            server.Diagnostics);

        var after = await server.GetLeadAsync(campaignId, leadId);

        after.GetProperty("researchStatus").GetString().ShouldBe(
            ResearchStatuses.None,
            $"a refused document must not half-land. Got: {Trim(after)}");
        after.GetProperty("score").GetInt32().ShouldBe(
            before.GetProperty("score").GetInt32(),
            $"nor re-score the lead. Got: {Trim(after)}");
    }

    [Fact]
    public async Task The_stored_adjustment_column_agrees_with_the_document_it_was_copied_from()
    {
        // research.llm_adjustment is a denormalized copy of a field inside research_json: the document
        // stays opaque TEXT (CLAUDE.md), so scoring reads the column. Two copies of one number can drift,
        // and nothing else would notice - the score would come from the column while get_lead showed the
        // document.
        using var timeout = TestTimeout.Start(60);
        var campaignId = await server.NewScoredCampaignAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.BayouFulfillment.Company);

        await server.SaveResearchAsync(campaignId, leadId, WorkedExamples.BayouFulfillment.Research);

        var stored = await ServerResearch.ReadAsync(server.Data, campaignId, leadId, timeout.Token);

        stored.ShouldNotBeNull("save_research writes one research row per lead (technical-design §5.2).");

        using var document = JsonDocument.Parse(stored.ResearchJson);

        document.RootElement.GetProperty("llmAdjustment").GetInt32().ShouldBe(
            stored.LlmAdjustment,
            "the column and the document hold the same number by definition. "
            + $"Column {stored.LlmAdjustment}, document {stored.ResearchJson}");
        stored.LlmAdjustment.ShouldBe(5, "the fixture's llmAdjustment.");

        stored.Signals.Count.ShouldBe(2, "the document's two signals became rows.");
        foreach (var signal in stored.Signals)
        {
            SignalTypes.All.ShouldContain(signal.Type, $"Got: {signal}");
            signal.Date.ShouldMatch(
                @"^\d{4}-\d{2}(-\d{2})?$",
                "signals.date stays text in the shape the schema allows, YYYY-MM or YYYY-MM-DD. "
                + $"Got: {signal}");
        }
    }

    [Fact]
    public async Task A_duplicate_row_is_refused_before_anything_is_written()
    {
        // mcp-tools.md §save_research: "A duplicate row may not [be researched]: that is VALIDATION_FAILED
        // naming the primary lead to use instead, refused before anything is written."
        //
        // NOT_FOUND would be a lie - the row exists and list_leads status:["duplicate"] hands out its id -
        // and a refusal that lands after the write is worse than either: the research, its signals, the
        // research_status and updated_at all stay behind on a row no scoring run will ever look at, so the
        // marketer sees their work vanish from every list with no error they can act on.
        using var timeout = TestTimeout.Start(60);
        var campaignId = await server.NewScoredCampaignAsync();

        var duplicates = await server.ListLeadsAsync(campaignId, new Dictionary<string, object?>
        {
            ["status"] = new[] { LeadStatuses.Duplicate },
            ["limit"] = LeadResponseLimits.DefaultPageSize,
        });

        // fx_0013 "Brazos Way Fulfillment" shares Bayou Fulfillment's domain and loses the dedupe on
        // confidence (0.71 against 0.95), so the primary is the lead the caller should have researched.
        var duplicate = duplicates.GetProperty("rows").EnumerateArray()
            .Single(row => row.GetProperty("name").GetString() == "Brazos Way Fulfillment");

        var duplicateLeadId = duplicate.GetProperty("id").GetString()!;
        var primaryLeadId = await server.LeadIdForAsync(campaignId, WorkedExamples.BayouFulfillment.Company);

        var before = await server.GetLeadAsync(campaignId, duplicateLeadId);

        before.GetProperty("status").GetString().ShouldBe(LeadStatuses.Duplicate, $"Got: {Trim(before)}");
        before.GetProperty("researchStatus").GetString().ShouldBe(ResearchStatuses.None, $"Got: {Trim(before)}");

        var updatedAt = before.GetProperty("updatedAt").GetString();

        var error = await ToolCall.ErrorAsync(
            server.Client,
            "save_research",
            new Dictionary<string, object?>
            {
                ["campaignId"] = campaignId,
                ["leadId"] = duplicateLeadId,
                ["research"] = SampleResearch.Valid(),
            },
            server.Diagnostics);

        // Asserted before the error code, deliberately: "nothing was written" is the half that matters, and
        // a test that checked the code first would stop there and never report that the write had happened.
        var after = await server.GetLeadAsync(campaignId, duplicateLeadId);

        after.GetProperty("researchStatus").GetString().ShouldBe(
            ResearchStatuses.None,
            $"research_status must not have moved. Got: {Trim(after)}");
        after.GetProperty("research").ValueKind.ShouldBe(
            JsonValueKind.Null,
            $"no document may be stored against a duplicate. Got: {Trim(after)}");
        after.GetProperty("signals").EnumerateArray().ShouldBeEmpty(
            $"nor any of its signals. Got: {Trim(after)}");
        after.GetProperty("updatedAt").GetString().ShouldBe(
            updatedAt,
            "a refused call must not stamp the row either - an updated_at with nothing behind it tells the "
            + $"next reader somebody edited this lead. Got: {Trim(after)}");

        var stored = await ServerResearch.ReadAsync(server.Data, campaignId, duplicateLeadId, timeout.Token);

        stored.ShouldBeNull(
            "and nothing in the research or signals tables either, which is the check the tool response "
            + "cannot make for itself.");

        // The primary is untouched too, so the refusal did not quietly redirect the write.
        (await server.GetLeadAsync(campaignId, primaryLeadId))
            .GetProperty("researchStatus").GetString().ShouldBe(
                ResearchStatuses.None,
                "naming the primary is advice, not an instruction to save it there.");

        error.Code.ShouldBe(
            "VALIDATION_FAILED",
            "the row exists and is listable, so NOT_FOUND is a false statement about it. §Errors keeps "
            + "VALIDATION_FAILED for 'Schema or rule violation', which this is. Raw: " + error.RawJson);

        (error.Message + " " + error.Hint + " " + string.Join(" ", error.Details.Select(detail => detail.ToString())))
            .ShouldContain(
                primaryLeadId,
                Case.Sensitive,
                $"the refusal has to name the primary lead ({primaryLeadId}) so the caller can retry "
                + "against it. The duplicate's site shares the primary's company_id - §7.2 collapses a "
                + "group into one company - so the primary is a lookup away. Raw: " + error.RawJson);
    }

    [Fact]
    public async Task A_suppressed_lead_may_be_researched()
    {
        // mcp-tools.md §save_research: "A suppressed lead may be researched - it keeps its score, it may be
        // released later, and the research can inform that decision." Pinned so it is not later tidied into
        // a refusal by analogy with the duplicate rule: the two cases look alike and are opposites. A
        // duplicate is not a lead; a suppressed lead is a real one that compliance removed, and finding out
        // it is a 40-site national account is exactly the sort of thing that reopens the decision.
        var campaignId = await server.NewScoredCampaignAsync();
        var suppressedName = SampleDealers.Dealer("gulf").DealerName;
        var leadId = await server.LeadIdForAsync(campaignId, suppressedName);

        var before = await server.GetLeadAsync(campaignId, leadId);
        before.GetProperty("status").GetString().ShouldBe(
            LeadStatuses.Suppressed,
            $"'{suppressedName}' is a dealer on suppression.csv. Got: {Trim(before)}");

        var saved = await server.SaveResearchAsync(campaignId, leadId, WorkedExamples.BayouFulfillment.Research);

        saved.GetProperty("saved").GetBoolean().ShouldBeTrue($"Got: {saved}");
        saved.GetProperty("score").ValueKind.ShouldBe(
            JsonValueKind.Number,
            "mcp-tools.md §save_research returns a score, and only 'delta' is documented as nullable. "
            + $"score_leads skipping suppressed leads is score_leads' rule, not this one. Got: {saved}");

        var after = await server.GetLeadAsync(campaignId, leadId);

        after.GetProperty("status").GetString().ShouldBe(
            LeadStatuses.Suppressed,
            $"researching a lead is not releasing it. Got: {Trim(after)}");
        after.GetProperty("researchStatus").GetString().ShouldBe(ResearchStatuses.Saved, $"Got: {Trim(after)}");
        after.GetProperty("research").ValueKind.ShouldBe(JsonValueKind.Object, $"Got: {Trim(after)}");

        // And it is still skipped by scoring, so researching it has not quietly put it back in the queue.
        var rescored = await server.ScoreLeadsAsync(campaignId);

        rescored.GetProperty("skipped").GetInt32().ShouldBe(
            7,
            $"all seven suppressed leads are still skipped. Got: {rescored}");
    }

    [Fact]
    public async Task An_unknown_lead_is_NOT_FOUND()
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "save_research",
            new Dictionary<string, object?>
            {
                ["campaignId"] = server.RejectionCampaignId,
                ["leadId"] = "L9999",
                ["research"] = SampleResearch.Valid(),
            },
            server.Diagnostics);

        error.Code.ShouldBe(
            "NOT_FOUND",
            "mcp-tools.md §Errors: NOT_FOUND for an unknown lead, not VALIDATION_FAILED - the document is "
            + "fine, the lead is not. Raw: " + error.RawJson);
    }

    [Fact]
    public async Task Saving_twice_replaces_the_document_rather_than_adding_a_second_one()
    {
        using var timeout = TestTimeout.Start(60);
        var campaignId = await server.NewScoredCampaignAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.WestparkMetalFab.Company);

        var first = await server.SaveResearchAsync(campaignId, leadId, WorkedExamples.WestparkMetalFab.Research);
        var second = await server.SaveResearchAsync(campaignId, leadId, WorkedExamples.WestparkMetalFab.Research);

        second.GetProperty("score").GetInt32().ShouldBe(
            first.GetProperty("score").GetInt32(),
            $"the same document gives the same score. Got: {second}");
        second.GetProperty("delta").GetInt32().ShouldBe(
            0,
            $"nothing changed the second time, which is a delta of 0 rather than null. Got: {second}");

        var stored = await ServerResearch.ReadAsync(server.Data, campaignId, leadId, timeout.Token);

        stored.ShouldNotBeNull();
        stored.Signals.Count.ShouldBe(
            1,
            "the document has one signal, so a second save must not leave two rows behind - duplicated "
            + "signals would push §7.6's signals feature from 0.6 to 1.0 on a re-save. "
            + $"Got: {string.Join("; ", stored.Signals)}");
    }

    private static string Trim(JsonElement payload)
    {
        var text = payload.ToString();
        return text.Length <= 1200 ? text : text[..1200] + "…";
    }
}
