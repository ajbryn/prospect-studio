using System.Text.Json;
using ProspectStudio.Core.Leads;
using ProspectStudio.Mcp.Tests.Infrastructure;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// <c>get_lead</c>'s <c>scoreDetail</c> (mcp-tools.md §get_lead): <c>baseScore</c>,
/// <c>llmAdjustment</c>, <c>asOf</c>, <c>sizeMinimum</c> and <c>cappedFrom</c>.
/// </summary>
/// <remarks>
/// The per-feature rows explain the weighted sum but not why the total differs from it, so without these
/// the one thing §7.6 promises to explain - a capped or adjusted score - is the one thing it cannot. The
/// fields are also read back out of stored JSON by property name, which fails silently to <c>null</c> if
/// a name drifts; asserting each one is what turns that into a red test.
/// </remarks>
[Collection(LeadServerCollection.Name)]
public class ScoreDetailContractTests(LeadServerFixture server)
{
    [Fact]
    public async Task A_plain_scored_lead_carries_every_field_of_the_detail()
    {
        var leadId = await server.LeadIdForAsync(server.ScoredCampaignId, WorkedExamples.BayouFulfillment.Company);

        var lead = await server.GetLeadAsync(server.ScoredCampaignId, leadId);
        var detail = Detail(lead);

        detail.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["baseScore", "llmAdjustment", "asOf", "sizeMinimum", "cappedFrom"],
            ignoreOrder: true,
            $"mcp-tools.md §get_lead names all five. Got: {detail}");

        detail.GetProperty("baseScore").GetInt32().ShouldBe(
            lead.GetProperty("score").GetInt32(),
            "unresearched there is no adjustment and no cap, so the base is the score. "
            + $"Got: {detail}");
        detail.GetProperty("llmAdjustment").GetInt32().ShouldBe(0, $"no research, no adjustment. Got: {detail}");
        detail.GetProperty("cappedFrom").ValueKind.ShouldBe(
            JsonValueKind.Null,
            $"52 is nowhere near §7.6's cap of {LeadTiers.ScoreCapWithoutSignals}. Got: {detail}");

        var asOf = detail.GetProperty("asOf").GetString();
        asOf.ShouldNotBeNullOrWhiteSpace(
            "§7.6's signal window is measured from a reference instant, and a score that silently depends "
            + $"on when it ran cannot be explained later. Got: {detail}");
        DateTimeOffset.TryParse(
            asOf,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var instant).ShouldBeTrue($"asOf has to be a timestamp. Got: {detail}");
        instant.ShouldBeGreaterThan(
            DateTimeOffset.UtcNow.AddHours(-1),
            $"the fixture scored this campaign a moment ago. Got: {detail}");

        var minimum = detail.GetProperty("sizeMinimum");
        minimum.GetProperty("value").GetInt32().ShouldBe(
            20,
            "the sample profile's size.employeesMin. Without it a sizeFit of 0.5 is unexplainable: 25 "
            + $"employees is half of 50 and well over 20. Got: {detail}");
        minimum.GetProperty("source").GetString().ShouldBe(
            SizeMinimumSources.Profile,
            "Warehousing & 3PL sets no minEmployees of its own, so §7.6 falls back to the profile's. "
            + $"Got: {detail}");
    }

    [Fact]
    public async Task A_research_adjustment_shows_up_as_the_difference_between_the_base_and_the_score()
    {
        var campaignId = await server.NewScoredCampaignAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.BayouFulfillment.Company);

        await server.SaveResearchAsync(campaignId, leadId, WorkedExamples.BayouFulfillment.Research);

        var detail = Detail(await server.GetLeadAsync(campaignId, leadId));

        detail.GetProperty("baseScore").GetInt32().ShouldBe(
            97,
            $"0.9665 → 96.65 → 97, before the document's +5. Got: {detail}");
        detail.GetProperty("llmAdjustment").GetInt32().ShouldBe(5, $"Got: {detail}");
        detail.GetProperty("cappedFrom").ValueKind.ShouldBe(
            JsonValueKind.Null,
            $"two cited buying signals, so the tier-A guard never applies. Got: {detail}");

        // 97 + 5 = 102, clamped to 100. Without the base and the adjustment, "100" cannot be told apart
        // from a lead that scored exactly 100 on the arithmetic alone.
        (await server.GetLeadAsync(campaignId, leadId)).GetProperty("score").GetInt32().ShouldBe(100);
    }

    [Fact]
    public async Task A_capped_score_records_what_it_was_held_back_from()
    {
        // §7.6: a lead with zero qualifying buying signals is capped at 79 however large its adjustment,
        // and "score_breakdown_json records when the cap bound, so a capped score is explainable rather
        // than merely lower than expected". Westpark Metal Fab is the subject: its only signal is a registry
        // row, so a +15 adjustment takes 71 to 86 and the guard pulls it back to 79.
        var campaignId = await server.NewScoredCampaignAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.WestparkMetalFab.Company);

        var nudged = SampleResearch.With(WorkedExamples.WestparkMetalFab.Research, ("llmAdjustment", 15));

        var saved = await server.SaveResearchAsync(campaignId, leadId, nudged);

        saved.GetProperty("score").GetInt32().ShouldBe(
            LeadTiers.ScoreCapWithoutSignals,
            $"71 + 15 = 86, capped at 79. Got: {saved}");
        saved.GetProperty("tier").GetString().ShouldBe(LeadTiers.B, $"Got: {saved}");

        var detail = Detail(await server.GetLeadAsync(campaignId, leadId));

        detail.GetProperty("baseScore").GetInt32().ShouldBe(71, $"Got: {detail}");
        detail.GetProperty("llmAdjustment").GetInt32().ShouldBe(15, $"Got: {detail}");
        detail.GetProperty("cappedFrom").GetInt32().ShouldBe(
            86,
            "without this the score reads as 79 against a breakdown that adds up to 71 plus an adjustment "
            + $"of 15, and nothing accounts for the seven points. Got: {detail}");
    }

    [Fact]
    public async Task A_lead_whose_score_was_never_capped_reports_a_null_rather_than_its_own_score()
    {
        // The guard against "cappedFrom = score" as a convenient non-null default, which would make every
        // lead look capped and the field useless for spotting the ones that are.
        var campaignId = await server.NewScoredCampaignAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.WestparkMetalFab.Company);

        await server.SaveResearchAsync(campaignId, leadId, WorkedExamples.WestparkMetalFab.Research);

        var detail = Detail(await server.GetLeadAsync(campaignId, leadId));

        detail.GetProperty("baseScore").GetInt32().ShouldBe(71, $"Got: {detail}");
        detail.GetProperty("cappedFrom").ValueKind.ShouldBe(
            JsonValueKind.Null,
            "Westpark has no qualifying signal either, but 71 is already inside tier B so nothing was held "
            + $"back. The cap lowers a score; it does not set one. Got: {detail}");
    }

    [Fact]
    public async Task The_stored_breakdown_is_free_of_float_noise_and_still_reconciles_with_the_score()
    {
        // §7.6: "Stored feature values and contributions are rounded to 4 decimal places, since the score is
        // computed from full precision and a reader of the explanation should not meet 0.08000000000000002.
        // The score itself is never computed from the rounded values." The manual check met both
        // 0.08000000000000002 and 0.053899999999999997 in a real breakdown.
        var campaignId = await server.NewScoredCampaignAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.BayouFulfillment.Company);

        await server.SaveResearchAsync(campaignId, leadId, WorkedExamples.BayouFulfillment.Research);

        var lead = await server.GetLeadAsync(campaignId, leadId);
        var rows = lead.GetProperty("scoreBreakdown").EnumerateArray().ToList();

        rows.Count.ShouldBe(6, $"Got: {Trim(lead)}");

        foreach (var row in rows)
        {
            var feature = row.GetProperty("feature").GetString();

            foreach (var field in new[] { "value", "contribution" })
            {
                var number = row.GetProperty(field).GetDouble();

                number.ShouldBe(
                    Math.Round(number, 4, MidpointRounding.AwayFromZero),
                    $"'{feature}.{field}' is {number:R}, which is not a 4-decimal number. The breakdown is "
                    + "what Claude reads back to explain a score, and 0.08000000000000002 explains nothing. "
                    + $"Got: {row}");
            }
        }

        // The other half: the score is still the full-precision one. 0.9665 → 96.65 → 97, plus the
        // document's +5, clamped to 100. If somebody "simplifies" this into rounding the feature values
        // before the weighted sum, the base moves and this goes red.
        var detail = Detail(lead);

        detail.GetProperty("baseScore").GetInt32().ShouldBe(
            97,
            $"computed from 0.25×1.0 + 0.15×1.0 + 0.20×1.0 + 0.25×1.0 + 0.05×1.0 + 0.10×0.665. Got: {detail}");
        lead.GetProperty("score").GetInt32().ShouldBe(100, $"Got: {detail}");

        // And the explanation has to reconcile with the score it explains: a reader who adds the displayed
        // contributions up must land on the displayed base. That holds while the rounding is cosmetic, and
        // breaks if the stored values are ever rounded coarsely enough to move the total.
        var total = rows.Sum(row => row.GetProperty("contribution").GetDouble());

        ((int)Math.Round(100 * total, MidpointRounding.AwayFromZero)).ShouldBe(
            detail.GetProperty("baseScore").GetInt32(),
            $"the six displayed contributions sum to {total:R}, which does not round to the displayed base "
            + $"of {detail.GetProperty("baseScore").GetInt32()}. Got: {string.Join(", ", rows.Select(row => row.ToString()))}");
    }

    [Fact]
    public async Task A_lead_nobody_has_scored_has_no_detail_to_show()
    {
        var campaignId = await server.NewCampaignWithCandidatesAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.BayouFulfillment.Company);

        var lead = await server.GetLeadAsync(campaignId, leadId);

        lead.TryGetProperty("scoreDetail", out var detail).ShouldBeTrue(
            $"the key is part of the response shape whether or not there is a score. Got: {Trim(lead)}");
        detail.ValueKind.ShouldBe(
            JsonValueKind.Null,
            "an object full of zeros would read as a real explanation of a score that does not exist. "
            + $"Got: {Trim(lead)}");
        lead.GetProperty("scoreBreakdown").EnumerateArray().ShouldBeEmpty($"Got: {Trim(lead)}");
    }

    private static JsonElement Detail(JsonElement lead)
    {
        lead.TryGetProperty("scoreDetail", out var detail).ShouldBeTrue(
            "mcp-tools.md §get_lead: 'Alongside scoreBreakdown[] it carries scoreDetail (baseScore, "
            + $"llmAdjustment, asOf, sizeMinimum, cappedFrom)'. Got: {Trim(lead)}");

        detail.ValueKind.ShouldBe(
            JsonValueKind.Object,
            "the lead is scored, so the detail is an object. A null here is what happens when the stored "
            + $"property names drift and the read falls through silently. Got: {Trim(lead)}");

        return detail;
    }

    private static string Trim(JsonElement payload)
    {
        var text = payload.ToString();
        return text.Length <= 1200 ? text : text[..1200] + "…";
    }
}
