using System.Text.Json;
using ProspectStudio.Core.Candidates;
using ProspectStudio.Core.Dealers;
using ProspectStudio.Mcp.Tests.Infrastructure;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// <c>update_leads</c> against mcp-tools.md §update_leads, and the two things that make it safe: a
/// manual dealer change becomes an <c>override</c> that survives both routing tools (technical-design
/// §7.4, which C5 made true), and a status change cannot quietly undo a suppression.
/// </summary>
[Collection(LeadServerCollection.Name)]
public class UpdateLeadsContractTests(LeadServerFixture server)
{
    [Fact]
    public async Task Changing_the_dealer_marks_the_assignment_as_an_override()
    {
        var campaignId = await server.NewScoredCampaignAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.BayouFulfillment.Company);

        var before = await server.GetLeadAsync(campaignId, leadId);
        before.GetProperty("assignment").GetString().ShouldBe(
            Assignments.Auto,
            "§7.4 routed Katy 77494 to gulf through the Fort Bend county rule. "
            + $"Got: {Trim(before)}");

        var result = await server.UpdateLeadsAsync(
            campaignId,
            new Dictionary<string, object?> { ["leadId"] = leadId, ["dealerId"] = "bay" });

        result.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["updated", "errors"],
            ignoreOrder: true,
            $"mcp-tools.md §update_leads returns {{ updated, errors }}. Got: {result}");
        result.GetProperty("updated").GetInt32().ShouldBe(1, $"Got: {result}");
        result.GetProperty("errors").EnumerateArray().ShouldBeEmpty($"Got: {result}");

        var after = await server.GetLeadAsync(campaignId, leadId);

        after.GetProperty("dealerId").GetString().ShouldBe("bay", $"Got: {Trim(after)}");
        after.GetProperty("assignment").GetString().ShouldBe(
            Assignments.Override,
            "§7.4: 'Manual overrides (update_leads, the workbook) set assignment=override and are never "
            + $"overwritten by re-assignment.' Got: {Trim(after)}");
        after.GetProperty("branchId").GetString().ShouldBe(
            "bay-pas",
            "a dealer with one branch leaves no choice, and a lead with a dealer but no branch has no "
            + $"address to mail from. Got: {Trim(after)}");
    }

    [Fact]
    public async Task An_override_survives_assign_dealers_and_apply_suppression()
    {
        // The claim C5 made true and C6 has to keep true: once a human has moved a lead, re-running the
        // routing must not move it back. Both tools, because find_candidates calls both.
        var campaignId = await server.NewScoredCampaignAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.BayouFulfillment.Company);

        await server.UpdateLeadsAsync(
            campaignId,
            new Dictionary<string, object?> { ["leadId"] = leadId, ["dealerId"] = "bay" });

        foreach (var tool in new[] { "assign_dealers", "apply_suppression", "assign_dealers" })
        {
            var routed = await ToolCall.OkAsync(
                server.Client,
                tool,
                new Dictionary<string, object?> { ["campaignId"] = campaignId },
                server.Diagnostics);

            var lead = await server.GetLeadAsync(campaignId, leadId);

            lead.GetProperty("dealerId").GetString().ShouldBe(
                "bay",
                $"'{tool}' put the lead back on its county default. §7.4: an override is 'never "
                + $"overwritten by re-assignment'. Routing reported: {routed}");
            lead.GetProperty("assignment").GetString().ShouldBe(
                Assignments.Override,
                $"'{tool}' cleared the override flag, so the next run will move the lead. "
                + $"Routing reported: {routed}");
        }
    }

    [Fact]
    public async Task An_override_is_reported_by_assign_dealers_rather_than_counted_as_a_change()
    {
        var campaignId = await server.NewScoredCampaignAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.BayouFulfillment.Company);

        await server.UpdateLeadsAsync(
            campaignId,
            new Dictionary<string, object?> { ["leadId"] = leadId, ["dealerId"] = "bay" });

        var routed = await ToolCall.OkAsync(
            server.Client,
            "assign_dealers",
            new Dictionary<string, object?> { ["campaignId"] = campaignId },
            server.Diagnostics);

        routed.GetProperty("overrides").GetInt32().ShouldBe(
            1,
            "mcp-tools.md §assign_dealers: \"'overrides' reports what the run deliberately left alone.\" "
            + $"Got: {routed}");
        routed.GetProperty("changed").GetInt32().ShouldBe(
            0,
            $"the run left the only unusual lead alone, so it changed nothing. Got: {routed}");
    }

    [Fact]
    public async Task A_status_change_cannot_resurrect_a_suppressed_lead()
    {
        // A compliance decision, not a preference. §7.3 suppresses a company because a list says not to
        // contact it; letting update_leads set the status straight back to 'approved' would undo that with
        // one tool call and no record. The way back is to drop the row from the list and re-run
        // apply_suppression, which restores pre_suppression_status.
        var campaignId = await server.NewScoredCampaignAsync();
        var suppressedName = SampleDealers.Dealer("gulf").DealerName;
        var leadId = await server.LeadIdForAsync(campaignId, suppressedName);

        var before = await server.GetLeadAsync(campaignId, leadId);
        before.GetProperty("status").GetString().ShouldBe(
            LeadStatuses.Suppressed,
            $"'{suppressedName}' is a dealer on suppression.csv. Got: {Trim(before)}");

        var result = await server.UpdateLeadsAsync(
            campaignId,
            new Dictionary<string, object?> { ["leadId"] = leadId, ["status"] = LeadStatuses.Approved });

        result.GetProperty("updated").GetInt32().ShouldBe(
            0,
            "a suppressed lead cannot be approved by setting its status. mcp-tools.md §update_leads "
            + $"returns row-level errors rather than failing the call. Got: {result}");

        var errors = result.GetProperty("errors").EnumerateArray().ToList();
        errors.ShouldNotBeEmpty(
            "silently ignoring it would be worse than refusing: the marketer would believe the lead was "
            + $"approved. Got: {result}");
        errors.ShouldContain(
            error => error.ToString().Contains(leadId, StringComparison.Ordinal),
            $"the error names the lead it refused. Got: {result}");

        var after = await server.GetLeadAsync(campaignId, leadId);
        after.GetProperty("status").GetString().ShouldBe(
            LeadStatuses.Suppressed,
            $"and the status is untouched. Got: {Trim(after)}");
    }

    [Fact]
    public async Task A_status_a_person_chooses_is_stored()
    {
        var campaignId = await server.NewScoredCampaignAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.WestparkMetalFab.Company);

        var result = await server.UpdateLeadsAsync(
            campaignId,
            new Dictionary<string, object?>
            {
                ["leadId"] = leadId,
                ["status"] = LeadStatuses.Approved,
                ["notes"] = "Call the plant manager first",
            });

        result.GetProperty("updated").GetInt32().ShouldBe(1, $"Got: {result}");

        var lead = await server.GetLeadAsync(campaignId, leadId);

        lead.GetProperty("status").GetString().ShouldBe(LeadStatuses.Approved, $"Got: {Trim(lead)}");
        lead.GetProperty("notes").GetString().ShouldBe(
            "Call the plant manager first",
            $"technical-design §5.2 gives leads a notes column. Got: {Trim(lead)}");
    }

    [Fact]
    public async Task A_batch_updates_several_leads_in_one_call()
    {
        var campaignId = await server.NewScoredCampaignAsync();

        var ids = new List<string>();
        foreach (var example in WorkedExamples.All)
        {
            ids.Add(await server.LeadIdForAsync(campaignId, example.Company));
        }

        var result = await server.UpdateLeadsAsync(
            campaignId,
            [.. ids.Select(id => new Dictionary<string, object?>
            {
                ["leadId"] = id,
                ["status"] = LeadStatuses.Review,
            })]);

        result.GetProperty("updated").GetInt32().ShouldBe(
            ids.Count,
            $"'updates' is a batch; approving forty leads in one call is the point. Got: {result}");

        var page = await server.ListLeadsAsync(campaignId, new Dictionary<string, object?>
        {
            ["status"] = new[] { LeadStatuses.Review },
            ["limit"] = 1,
        });

        page.GetProperty("total").GetInt32().ShouldBe(ids.Count, $"Got: {page}");
    }

    [Fact]
    public async Task An_unknown_lead_is_reported_in_errors_without_failing_the_call()
    {
        var campaignId = await server.NewScoredCampaignAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.WestparkMetalFab.Company);

        var result = await server.UpdateLeadsAsync(
            campaignId,
            new Dictionary<string, object?> { ["leadId"] = leadId, ["status"] = LeadStatuses.Hold },
            new Dictionary<string, object?> { ["leadId"] = "L9999", ["status"] = LeadStatuses.Hold });

        result.GetProperty("updated").GetInt32().ShouldBe(
            1,
            "one good row and one bad one: mcp-tools.md §update_leads returns both numbers rather than "
            + $"rejecting the batch, the way import_list does with a bad row. Got: {result}");

        var error = result.GetProperty("errors").EnumerateArray().ShouldHaveSingleItem($"Got: {result}");
        error.ToString().ShouldContain("L9999", Case.Insensitive, $"Got: {result}");
    }

    [Fact]
    public async Task An_unknown_dealer_is_refused_rather_than_stored()
    {
        // An unroutable dealer id would leave the lead pointing at nobody, and the dealer packet step
        // would find no address to mail from.
        var campaignId = await server.NewScoredCampaignAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.WestparkMetalFab.Company);

        var result = await server.UpdateLeadsAsync(
            campaignId,
            new Dictionary<string, object?> { ["leadId"] = leadId, ["dealerId"] = "nope" });

        result.GetProperty("updated").GetInt32().ShouldBe(0, $"Got: {result}");
        result.GetProperty("errors").EnumerateArray().ShouldNotBeEmpty($"Got: {result}");

        var lead = await server.GetLeadAsync(campaignId, leadId);
        lead.GetProperty("dealerId").GetString().ShouldBe("gulf", $"Got: {Trim(lead)}");
        lead.GetProperty("assignment").GetString().ShouldBe(
            Assignments.Auto,
            $"a refused change must not leave the override flag behind. Got: {Trim(lead)}");
    }

    [Fact]
    public async Task An_unknown_status_is_refused()
    {
        var campaignId = await server.NewScoredCampaignAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.WestparkMetalFab.Company);

        var result = await server.UpdateLeadsAsync(
            campaignId,
            new Dictionary<string, object?> { ["leadId"] = leadId, ["status"] = "maybe" });

        result.GetProperty("updated").GetInt32().ShouldBe(
            0,
            "technical-design §5.2 lists the statuses. An unknown one stored as text would make every "
            + $"filter and every count disagree with reality. Got: {result}");
        result.GetProperty("errors").EnumerateArray().ShouldNotBeEmpty($"Got: {result}");
    }

    [Fact]
    public async Task An_unknown_campaign_is_NOT_FOUND()
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "update_leads",
            new Dictionary<string, object?>
            {
                ["campaignId"] = "cmp_NOPE99",
                ["updates"] = new[]
                {
                    new Dictionary<string, object?> { ["leadId"] = "L0001", ["status"] = LeadStatuses.Hold },
                },
            },
            server.Diagnostics);

        error.Code.ShouldBe(
            "NOT_FOUND",
            "an unknown campaign is the whole call failing, not a row error. Raw: " + error.RawJson);
    }

    private static string Trim(JsonElement payload)
    {
        var text = payload.ToString();
        return text.Length <= 1200 ? text : text[..1200] + "…";
    }
}
