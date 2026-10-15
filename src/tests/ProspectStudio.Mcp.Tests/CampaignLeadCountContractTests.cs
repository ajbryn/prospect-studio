using System.Text.Json;
using ProspectStudio.Mcp.Tests.Infrastructure;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// mcp-tools.md §list_campaigns: "<c>leads</c> is the campaign's real lead count on the same basis as
/// <c>get_campaign</c>'s <c>byStatus</c> — <c>duplicate</c> rows excluded — so the two tools cannot
/// disagree about how many leads a campaign has."
/// </summary>
/// <remarks>
/// <para>
/// The number was hardcoded to <c>0</c>, which was honest in C1 when there was no <c>leads</c> table and
/// has been wrong since C4. A confident wrong number is worse than an absent one: the manual check showed a
/// campaign listing as <c>"leads": 0</c> while <c>get_campaign</c> reported 4,741 for the same campaign,
/// which reads as a lost campaign rather than as a missing feature.
/// </para>
/// <para>
/// The two tools are asserted <strong>against each other</strong> rather than both against 83. Pinning a
/// constant twice would leave them free to drift apart together, and the thing that matters here is that
/// they agree.
/// </para>
/// </remarks>
[Collection(LeadServerCollection.Name)]
public class CampaignLeadCountContractTests(LeadServerFixture server)
{
    [Fact]
    public async Task List_campaigns_agrees_with_get_campaign_about_a_populated_campaign()
    {
        var listed = await LeadCountAsync(server.ScoredCampaignId);
        var byStatus = await ByStatusTotalAsync(server.ScoredCampaignId);

        listed.ShouldBeGreaterThan(
            0,
            "the campaign has leads, so a 0 here is the hardcoded value rather than a count. This is the "
            + "assertion the empty-campaign case below cannot make.");
        listed.ShouldBe(
            byStatus,
            $"list_campaigns said {listed} and get_campaign's byStatus sums to {byStatus} for the same "
            + "campaign. Whichever is right, a user comparing the two screens cannot tell.");
    }

    [Fact]
    public async Task A_campaign_with_no_leads_reports_zero_from_both_tools()
    {
        // Kept separate and kept explicit: the bug's value was 0, so a suite that only ever checked a
        // populated campaign would pass again the moment somebody reintroduced the shortcut "for empty
        // campaigns". Zero has to be a computed answer here, not a default.
        var campaignId = await server.NewEmptyCampaignAsync();

        var listed = await LeadCountAsync(campaignId);
        var byStatus = await ByStatusTotalAsync(campaignId);

        listed.ShouldBe(0, "find_candidates has not run on this campaign.");
        byStatus.ShouldBe(0, "and get_campaign agrees.");
    }

    [Fact]
    public async Task The_count_excludes_duplicates_the_same_way_byStatus_does()
    {
        // mcp-tools.md §score_leads: duplicate rows "appear in no lead-facing total". The campaign holds 86
        // rows, three of them duplicates, so a count that included them would read 86 - and the three are
        // reachable only through an explicit status filter, which is how this test gets at them.
        var listed = await LeadCountAsync(server.ScoredCampaignId);

        var duplicates = await server.ListLeadsAsync(server.ScoredCampaignId, new Dictionary<string, object?>
        {
            ["status"] = new[] { Core.Candidates.LeadStatuses.Duplicate },
            ["limit"] = 1,
        });

        var duplicateCount = duplicates.GetProperty("total").GetInt32();
        duplicateCount.ShouldBe(3, $"C4's arithmetic. Got: {duplicates}");

        var unfiltered = await server.ListLeadsAsync(server.ScoredCampaignId, new Dictionary<string, object?>
        {
            ["limit"] = 1,
        });

        listed.ShouldBe(
            unfiltered.GetProperty("total").GetInt32(),
            "list_campaigns and an unfiltered list_leads count the same leads, so neither includes the "
            + $"{duplicateCount} duplicate rows.");
    }

    private async Task<int> LeadCountAsync(string campaignId)
    {
        var listed = await ToolCall.OkAsync(server.Client, "list_campaigns", diagnostics: server.Diagnostics);

        var row = listed.GetProperty("campaigns").EnumerateArray()
            .SingleOrDefault(campaign => campaign.GetProperty("campaignId").GetString() == campaignId);

        row.ValueKind.ShouldBe(
            JsonValueKind.Object,
            $"'{campaignId}' should be in list_campaigns. Got: {Trim(listed)}");

        row.TryGetProperty("leads", out var leads).ShouldBeTrue(
            $"mcp-tools.md §list_campaigns shows a 'leads' field on every row. Got: {row}");

        return leads.GetInt32();
    }

    private async Task<int> ByStatusTotalAsync(string campaignId)
    {
        var campaign = await ToolCall.OkAsync(
            server.Client,
            "get_campaign",
            new Dictionary<string, object?> { ["campaignId"] = campaignId },
            server.Diagnostics);

        return campaign.GetProperty("counts").GetProperty("byStatus")
            .EnumerateObject()
            .Sum(status => status.Value.GetInt32());
    }

    private static string Trim(JsonElement payload)
    {
        var text = payload.ToString();
        return text.Length <= 1200 ? text : text[..1200] + "…";
    }
}
