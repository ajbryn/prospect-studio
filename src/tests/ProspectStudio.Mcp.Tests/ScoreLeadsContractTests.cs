using System.Text.Json;
using ProspectStudio.Core.Candidates;
using ProspectStudio.Core.Dealers;
using ProspectStudio.Core.Leads;
using ProspectStudio.Mcp.Tests.Infrastructure;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Mcp.Tests;

/// <summary>
/// <c>score_leads</c> over a real campaign, against mcp-tools.md §score_leads and technical-design §7.6:
/// tier counts, a weights override, re-runnability, and the three worked examples of docs/03 §8 landing
/// in tiers A, A and B through the production extractor and scorer rather than a hand-built feature set.
/// </summary>
/// <remarks>
/// <strong>Every call that is expected to be refused goes to
/// <see cref="LeadServerFixture.RejectionCampaignId"/>, not to the shared scored campaign.</strong> A
/// rejection test is the one kind that runs its payload against production code that may not reject it
/// yet, and a weights override of 2.0 and −1.0 that gets through re-scores whatever campaign it was
/// pointed at. That happened: two unrelated tests went red in the full run and green in isolation, which
/// is the most expensive kind of failure to read. Keep refusals on the scratch campaign.
/// </remarks>
[Collection(LeadServerCollection.Name)]
public class ScoreLeadsContractTests(LeadServerFixture server)
{
    /// <summary>Leads the sample profile produces, as C5's tests pin it.</summary>
    private const int Leads = 83;

    /// <summary>Leads §7.3 suppressed, which mcp-tools.md §score_leads now skips.</summary>
    private const int Skipped = 7;

    private const int Scored = Leads - Skipped;

    [Fact]
    public void The_first_run_reports_what_it_scored_what_it_skipped_and_the_weights_it_used()
    {
        var result = server.FirstScoreRun;

        result.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["scored", "skipped", "tiers", "weights"],
            ignoreOrder: true,
            $"mcp-tools.md §score_leads: {{ scored, skipped, tiers: {{A,B,C}}, weights }}. Got: {result}");

        var scored = result.GetProperty("scored").GetInt32();
        var tiers = result.GetProperty("tiers");

        foreach (var tier in LeadTiers.All)
        {
            tiers.TryGetProperty(tier, out _).ShouldBeTrue(
                $"the breakdown names all three tiers even at zero, so a skill can read tiers.A without "
                + $"guessing whether the key exists. Got: {tiers}");
        }

        tiers.EnumerateObject().Sum(tier => tier.Value.GetInt32()).ShouldBe(
            scored,
            "mcp-tools.md §score_leads: 'tiers covers the scored leads only'. A tier count a user reads as "
            + "'how many A leads do I have' must not include leads they cannot contact. "
            + $"Got: {result}");

        tiers.GetProperty(LeadTiers.A).GetInt32().ShouldBe(
            0,
            "no research has been saved in this campaign, so §7.6's base ceiling of 75 puts tier A out of "
            + $"reach. Got: {result}");

        scored.ShouldBe(
            Scored,
            $"the campaign holds {Leads} leads and §7.3 suppressed {Skipped} of them, which score_leads "
            + $"skips. Got: {result}");
        result.GetProperty("skipped").GetInt32().ShouldBe(
            Skipped,
            "mcp-tools.md §score_leads: 'skipped counts them so the shortfall against the lead count is "
            + $"stated rather than left for the caller to notice'. Got: {result}");

        (scored + result.GetProperty("skipped").GetInt32()).ShouldBe(
            Leads,
            $"every lead is either scored or skipped. Got: {result}");

        var weights = result.GetProperty("weights");
        foreach (var feature in ScoreFeatures.All)
        {
            weights.TryGetProperty(feature, out _).ShouldBeTrue(
                $"§7.6's weights are echoed so the caller knows what produced the scores. Missing "
                + $"'{feature}'. Got: {weights}");
        }

        weights.EnumerateObject().Sum(weight => weight.Value.GetDouble()).ShouldBe(
            1.0,
            ScoringWeights.Tolerance,
            $"§7.6: the weights must sum to 1.0. Got: {weights}");
    }

    [Fact]
    public async Task A_lead_nobody_has_scored_has_no_score_rather_than_a_zero()
    {
        // The only case mcp-tools.md §list_leads allows a null score: "null only when a lead has never been
        // scored". A 0 would read as "scored, and hopeless", which is a claim about the company.
        var campaignId = await server.NewCampaignWithCandidatesAsync();

        var page = await server.ListLeadsAsync(campaignId, new Dictionary<string, object?>
        {
            ["limit"] = LeadResponseLimits.DefaultPageSize,
        });

        page.GetProperty("rows").EnumerateArray().ShouldNotBeEmpty($"Got: {page}");

        foreach (var row in page.GetProperty("rows").EnumerateArray())
        {
            row.GetProperty("score").ValueKind.ShouldBe(
                JsonValueKind.Null,
                $"score_leads has not run on this campaign. Got: {row}");
            row.GetProperty("tier").ValueKind.ShouldBe(JsonValueKind.Null, $"Got: {row}");
        }
    }

    [Fact]
    public async Task A_lead_scored_before_compliance_suppressed_it_keeps_its_score()
    {
        // The realistic order, which the earlier version of this test never drove: a do-not-contact row
        // arriving AFTER the list was scored. Suppression ran before scoring in the fixture, so "suppressed
        // ⇒ no score" held for free and an implementation that cleared scores on suppression - or one that
        // never cleared them - would both have passed.
        //
        // mcp-tools.md §list_leads: a lead that was scored and later suppressed "keeps and shows its
        // score", because it is true, it is useful (suppressing a tier-A lead because they are already a
        // customer is worth seeing), and status already says suppressed.
        const string company = "Ship Channel Logistics LLC";

        var campaignId = await server.NewScoredCampaignAsync();
        var leadId = await server.LeadIdForAsync(campaignId, company);

        var before = await server.GetLeadAsync(campaignId, leadId);

        before.GetProperty("status").GetString().ShouldBe(
            LeadStatuses.Candidate,
            $"'{company}' is not on the committed suppression list. Got: {Trim(before)}");
        before.GetProperty("score").ValueKind.ShouldBe(
            JsonValueKind.Number,
            $"the campaign has been scored, so this lead has a score to lose. Got: {Trim(before)}");

        var scored = before.GetProperty("score").GetInt32();
        var tier = before.GetProperty("tier").GetString();

        await using (await server.AddSuppressionRowAsync(
            company,
            "shipchannellogistics.example",
            "77571",
            SuppressionReasons.Dnc))
        {
            var suppression = await ToolCall.OkAsync(
                server.Client,
                "apply_suppression",
                new Dictionary<string, object?> { ["campaignId"] = campaignId },
                server.Diagnostics);

            suppression.GetProperty("changed").GetInt32().ShouldBe(
                1,
                $"the new row names exactly one lead in this campaign. Got: {suppression}");

            var after = await server.GetLeadAsync(campaignId, leadId);

            after.GetProperty("status").GetString().ShouldBe(LeadStatuses.Suppressed, $"Got: {Trim(after)}");
            after.GetProperty("score").GetInt32().ShouldBe(
                scored,
                "nothing erases a score. Clearing it here would throw away the one fact that tells the "
                + "marketer whether this suppression cost them anything, and a later release would hand "
                + $"back an unscored lead. Got: {Trim(after)}");
            after.GetProperty("tier").GetString().ShouldBe(tier, $"Got: {Trim(after)}");

            // And a scoring run afterwards skips it rather than clearing it, which is the other half of
            // "nothing erases a score".
            var rescored = await server.ScoreLeadsAsync(campaignId);

            rescored.GetProperty("skipped").GetInt32().ShouldBe(
                Skipped + 1,
                $"the eighth suppressed lead joins the skipped count. Got: {rescored}");
            rescored.GetProperty("scored").GetInt32().ShouldBe(Scored - 1, $"Got: {rescored}");

            var afterRescore = await server.GetLeadAsync(campaignId, leadId);

            afterRescore.GetProperty("score").GetInt32().ShouldBe(
                scored,
                $"skipped means 'left alone', not 'reset'. Got: {Trim(afterRescore)}");
        }
    }

    [Fact]
    public async Task A_suppressed_lead_is_still_counted_in_the_denominator_and_still_listed()
    {
        // mcp-tools.md §score_leads: a suppressed lead "is a real lead that compliance removed ... so it
        // stays in the denominator and is counted, not hidden". The contrast is `duplicate`, which is not a
        // lead at all - covered in ListLeadsContractTests.
        var page = await server.ListLeadsAsync(server.ScoredCampaignId, new Dictionary<string, object?>
        {
            ["status"] = new[] { LeadStatuses.Suppressed },
            ["limit"] = LeadResponseLimits.DefaultPageSize,
        });

        page.GetProperty("total").GetInt32().ShouldBe(
            Skipped,
            $"all seven are reachable through the status filter. Got: {page}");

        foreach (var row in page.GetProperty("rows").EnumerateArray())
        {
            row.GetProperty("status").GetString().ShouldBe(LeadStatuses.Suppressed, $"Got: {row}");
        }
    }

    [Fact]
    public async Task Re_running_it_changes_nothing()
    {
        var again = await server.ScoreLeadsAsync(server.ScoredCampaignId);

        again.GetProperty("scored").GetInt32().ShouldBe(
            server.FirstScoreRun.GetProperty("scored").GetInt32(),
            $"NFR-3: scoring is deterministic and re-runnable. Got: {again}");
        again.GetProperty("skipped").GetInt32().ShouldBe(
            server.FirstScoreRun.GetProperty("skipped").GetInt32(),
            $"Got: {again}");
        again.GetProperty("tiers").ToString().ShouldBe(
            server.FirstScoreRun.GetProperty("tiers").ToString(),
            $"the same leads and the same evidence must give the same tiers. Got: {again}");

        var third = await server.ScoreLeadsAsync(server.ScoredCampaignId);
        third.ToString().ShouldBe(again.ToString(), "and again.");
    }

    [Fact]
    public async Task Weights_that_do_not_sum_to_one_are_VALIDATION_FAILED_at_the_weights_pointer()
    {
        // C1 enforces this on save_search_profile. score_leads is the second way in, and a set summing to
        // 0.9 here would score every lead a tenth low with nothing to show it had happened.
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "score_leads",
            new Dictionary<string, object?>
            {
                ["campaignId"] = server.RejectionCampaignId,
                ["weights"] = new Dictionary<string, object?>
                {
                    ["segmentFit"] = 0.25,
                    ["sizeFit"] = 0.15,
                    ["facilityFit"] = 0.20,
                    ["signals"] = 0.20,
                    ["proximity"] = 0.05,
                    ["confidence"] = 0.05,
                },
            },
            server.Diagnostics);

        error.Code.ShouldBe(
            "VALIDATION_FAILED",
            "§7.6: 'Weights can be overridden in the profile (scoringWeights) and must sum to 1.0.' "
            + "Raw: " + error.RawJson);

        error.Details.ShouldContain(
            detail => detail.Pointer == "/weights",
            "the pointer names what the caller sent. '/scoringWeights' would point at a profile field "
            + "they never touched. Raw: " + error.RawJson);
    }

    [Fact]
    public async Task Weights_passed_to_the_tool_override_the_profiles()
    {
        // All the weight on signals, over a campaign with no research at all, so every feature that could
        // earn a point is worth nothing: every lead must score 0 and land in tier C. A decisive check that
        // the override reached the scorer rather than being accepted and ignored.
        var campaignId = await server.NewCampaignWithCandidatesAsync();

        var result = await server.ScoreLeadsAsync(
            campaignId,
            new Dictionary<string, object?>
            {
                ["segmentFit"] = 0.0,
                ["sizeFit"] = 0.0,
                ["facilityFit"] = 0.0,
                ["signals"] = 1.0,
                ["proximity"] = 0.0,
                ["confidence"] = 0.0,
            });

        var tiers = result.GetProperty("tiers");

        tiers.GetProperty(LeadTiers.C).GetInt32().ShouldBe(
            result.GetProperty("scored").GetInt32(),
            "with signals weighted 1.0 and no research anywhere, every score is 0. If the tiers match the "
            + $"default run, the override was ignored. Got: {result}");

        result.GetProperty("weights").GetProperty(ScoreFeatures.Signals).GetDouble().ShouldBe(
            1.0,
            ScoringWeights.Tolerance,
            $"the weights echoed back are the ones that were used. Got: {result}");

        var page = await server.ListLeadsAsync(campaignId, new Dictionary<string, object?>
        {
            ["status"] = new[] { LeadStatuses.Candidate },
            ["limit"] = 5,
        });

        foreach (var row in page.GetProperty("rows").EnumerateArray())
        {
            row.GetProperty("score").GetInt32().ShouldBe(0, $"Got: {row}");
            row.GetProperty("tier").GetString().ShouldBe(LeadTiers.C, $"Got: {row}");
        }
    }

    [Theory]
    [InlineData("signalz")]
    [InlineData("segment_fit")]
    public async Task An_unknown_key_in_weights_is_VALIDATION_FAILED_and_the_key_is_named(string offender)
    {
        // mcp-tools.md §score_leads: "Unknown keys in weights are VALIDATION_FAILED: the sum-to-1.0 rule
        // cannot catch a typo, so {"signals": 0.5, "signalz": 0.5} would otherwise pass and score every lead
        // at half weight." A profile is protected by search-profile.schema.json; this parameter has no
        // schema, so nothing else stands in front of it.
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "score_leads",
            new Dictionary<string, object?>
            {
                ["campaignId"] = server.RejectionCampaignId,
                ["weights"] = new Dictionary<string, object?>
                {
                    [ScoreFeatures.Signals] = 0.5,
                    [offender] = 0.5,
                },
            },
            server.Diagnostics);

        error.Code.ShouldBe(
            "VALIDATION_FAILED",
            "these sum to exactly 1.0, so the sum rule passes them. Raw: " + error.RawJson);

        error.Details.ShouldNotBeEmpty(
            "mcp-tools.md §Errors: VALIDATION_FAILED 'include details[]'. Raw: " + error.RawJson);
        error.Details.ShouldContain(
            detail => detail.Mentions(offender),
            $"the caller mistyped one key out of six and has to be told which. Raw: {error.RawJson}");
    }

    [Fact]
    public async Task A_weight_outside_zero_to_one_is_VALIDATION_FAILED()
    {
        // mcp-tools.md §score_leads: "as are non-numeric values and any weight outside 0–1: the
        // sum-to-1.0 rule catches none of the three ... {"segmentFit": 2.0, "proximity": -1.0, …} sums to
        // 1.0 while scoring nonsense." Any set containing a weight above 1 needs a negative one to reach
        // 1.0, which is how this one is built - so the sum rule is satisfied and only a range check is left.
        var weights = new Dictionary<string, object?>
        {
            [ScoreFeatures.SegmentFit] = 2.0,
            [ScoreFeatures.SizeFit] = 0.15,
            [ScoreFeatures.FacilityFit] = 0.20,
            [ScoreFeatures.Signals] = -1.0,
            [ScoreFeatures.Proximity] = 0.05,
            [ScoreFeatures.Confidence] = -0.4,
        };

        weights.Values.Sum(value => (double)value!).ShouldBe(
            1.0,
            ScoringWeights.Tolerance,
            "the case only means something if the sum rule lets it through.");

        var error = await ToolCall.ErrorAsync(
            server.Client,
            "score_leads",
            new Dictionary<string, object?> { ["campaignId"] = server.RejectionCampaignId, ["weights"] = weights },
            server.Diagnostics);

        error.Code.ShouldBe("VALIDATION_FAILED", "Raw: " + error.RawJson);
        error.Details.ShouldContain(
            detail => detail.Mentions(ScoreFeatures.SegmentFit)
                || detail.Mentions(ScoreFeatures.Signals)
                || detail.Mentions(ScoreFeatures.Confidence),
            "the response has to name a weight that is out of range, or the caller is left checking six "
            + "numbers against a rule the message only hints at. Raw: " + error.RawJson);
    }

    [Theory]
    [InlineData("0.25")]
    [InlineData(null)]
    [InlineData(true)]
    public async Task A_non_numeric_weight_is_VALIDATION_FAILED_and_names_the_key(object? value)
    {
        // The branch with no coverage at all today: a quoted number, a null or a boolean. Reading it as 0
        // and letting the sum rule object would blame the wrong thing - the caller would be told their
        // weights sum to 0.75 when what they actually did was quote a number.
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "score_leads",
            new Dictionary<string, object?>
            {
                ["campaignId"] = server.RejectionCampaignId,
                ["weights"] = new Dictionary<string, object?>
                {
                    [ScoreFeatures.SegmentFit] = value,
                    [ScoreFeatures.SizeFit] = 0.15,
                    [ScoreFeatures.FacilityFit] = 0.20,
                    [ScoreFeatures.Signals] = 0.25,
                    [ScoreFeatures.Proximity] = 0.05,
                    [ScoreFeatures.Confidence] = 0.10,
                },
            },
            server.Diagnostics);

        error.Code.ShouldBe("VALIDATION_FAILED", "Raw: " + error.RawJson);
        error.Details.ShouldContain(
            detail => detail.Mentions(ScoreFeatures.SegmentFit),
            $"'{value ?? "null"}' is not a number and the message must say which key carried it, not "
            + "report a sum. Raw: " + error.RawJson);
    }

    [Fact]
    public async Task A_partial_override_echoes_every_feature_including_the_ones_left_at_zero()
    {
        // A partial object is legal precisely because the caller can see what it resolved to: an omitted
        // feature is a weight of 0, not its default, and {"signals": 1.0} is only safe to accept if the
        // response says so in all six numbers. The override test passes all six explicitly and the
        // all-six-echoed test is the default run where nothing is 0, so neither exercises this.
        var campaignId = await server.NewCampaignWithCandidatesAsync();

        var result = await server.ScoreLeadsAsync(
            campaignId,
            new Dictionary<string, object?> { [ScoreFeatures.Signals] = 1.0 });

        var echoed = result.GetProperty("weights");

        echoed.EnumerateObject().Select(property => property.Name).ShouldBe(
            ScoreFeatures.All,
            ignoreOrder: true,
            $"all six, whatever the caller sent. Got: {echoed}");

        echoed.GetProperty(ScoreFeatures.Signals).GetDouble().ShouldBe(
            1.0,
            ScoringWeights.Tolerance,
            $"Got: {echoed}");

        foreach (var feature in ScoreFeatures.All.Where(feature => feature != ScoreFeatures.Signals))
        {
            echoed.GetProperty(feature).GetDouble().ShouldBe(
                0d,
                ScoringWeights.Tolerance,
                $"'{feature}' was omitted, which can only mean 0 - mixing the defaults in would make this "
                + $"set sum to 1.75. Showing it as 0 is what makes the partial object safe. Got: {echoed}");
        }
    }

    [Fact]
    public async Task Scoring_moves_a_leads_timestamp_only_when_its_score_actually_changes()
    {
        // mcp-tools.md §score_leads: "A lead's updated_at moves only when its stored score, tier or
        // breakdown actually changes, so a re-run that changes nothing leaves every timestamp alone and
        // idempotence covers the timestamps too." Both directions matter: never moving it makes
        // get_lead.updatedAt stale the moment a campaign is scored, and always moving it makes every
        // re-run look like somebody edited 76 leads.
        var campaignId = await server.NewCampaignWithCandidatesAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.BayouFulfillment.Company);

        var beforeScoring = UpdatedAt(await server.GetLeadAsync(campaignId, leadId));

        // updatedAt is serialized to whole seconds, so the two writes have to fall in different seconds for
        // a "moved" assertion to mean anything. Waiting is the honest way round it: asserting >= instead
        // would be satisfied by an implementation that never touches the timestamp at all, which is the bug.
        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        await server.ScoreLeadsAsync(campaignId);
        var afterScoring = UpdatedAt(await server.GetLeadAsync(campaignId, leadId));

        afterScoring.ShouldBeGreaterThan(
            beforeScoring,
            $"the lead gained a score and a tier, which is a change. Before {beforeScoring:O}, after "
            + $"{afterScoring:O}.");

        await server.ScoreLeadsAsync(campaignId);
        var afterNoOp = UpdatedAt(await server.GetLeadAsync(campaignId, leadId));

        afterNoOp.ShouldBe(
            afterScoring,
            "the second run computed the same score from the same evidence, so nothing changed and nothing "
            + "should be stamped. A bump here makes every re-run look like a bulk edit, and the workbook "
            + $"round-trip in C8 reads this column. Before {afterScoring:O}, after {afterNoOp:O}.");
    }

    [Fact]
    public async Task The_weights_a_run_used_are_persisted_so_save_research_re_scores_on_the_same_scale()
    {
        // mcp-tools.md §score_leads: "The run's effective weights are persisted on the campaign
        // (campaigns.scoring_weights_json) and save_research re-scores with those, so a campaign is only
        // ever scored on one scale." Without it an override run followed by a single save_research leaves
        // one lead measured differently from the other 75 and silently incomparable - the list still sorts
        // by score, so the mis-scaled lead simply appears in the wrong place.
        var campaignId = await server.NewCampaignWithCandidatesAsync();

        // Almost all the weight on confidence, which is the one feature research cannot move. On this scale
        // Bayou Fulfillment reaches 75 (tier B) with its research saved; on the profile's it reaches 100
        // (tier A), so the two scales cannot be confused for one another.
        await server.ScoreLeadsAsync(campaignId, ConfidenceHeavyWeights);

        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.BayouFulfillment.Company);

        var before = await server.GetLeadAsync(campaignId, leadId);
        before.GetProperty("score").GetInt32().ShouldBe(
            66,
            "0.02×(1.0 + 0.5 + 0.4 + 0 + 1.0) + 0.9×0.665 = 0.6565 → 66, unresearched on the override "
            + $"scale. Got: {Trim(before)}");

        var saved = await server.SaveResearchAsync(campaignId, leadId, WorkedExamples.BayouFulfillment.Research);

        saved.GetProperty("score").GetInt32().ShouldBe(
            75,
            "0.02×5 + 0.9×0.665 = 0.6985 → 70, plus the document's +5 adjustment = 75. A 100 here means "
            + "save_research re-scored with the profile's weights instead of the campaign's, so this lead "
            + $"is on a different scale from every other lead in the list. Got: {saved}");
        saved.GetProperty("tier").GetString().ShouldBe(
            LeadTiers.B,
            $"on the profile scale the same research reaches tier A. Got: {saved}");
        saved.GetProperty("delta").GetInt32().ShouldBe(
            9,
            $"75 − 66, both measured on the override scale. Got: {saved}");
    }

    [Fact]
    public async Task A_later_run_with_no_weights_returns_the_campaign_to_the_profiles_scale()
    {
        // The direction the contract implies but does not spell out: score_leads decides the scale and
        // save_research follows it, so a run with no weights argument goes back to the profile's weights
        // (and persists those) rather than silently reusing the last override. Flagged in the C6 report as
        // my reading.
        var campaignId = await server.NewCampaignWithCandidatesAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.BayouFulfillment.Company);

        await server.ScoreLeadsAsync(campaignId, ConfidenceHeavyWeights);
        var onTheOverrideScale = await server.SaveResearchAsync(
            campaignId,
            leadId,
            WorkedExamples.BayouFulfillment.Research);

        var restored = await server.ScoreLeadsAsync(campaignId);

        restored.GetProperty("weights").GetProperty(ScoreFeatures.Confidence).GetDouble().ShouldBe(
            ScoringWeights.Default.Confidence,
            ScoringWeights.Tolerance,
            $"an omitted weights argument means the profile's weights. Got: {restored}");

        var lead = await server.GetLeadAsync(campaignId, leadId);

        onTheOverrideScale.GetProperty("score").GetInt32().ShouldBe(75);
        lead.GetProperty("score").GetInt32().ShouldBe(
            100,
            "the same research on the profile scale. If this is still 75 the override has become sticky and "
            + $"no call puts the campaign back. Got: {Trim(lead)}");
    }

    [Fact]
    public async Task The_three_worked_examples_land_in_tiers_A_A_and_B()
    {
        // Implementation-plan C6's headline test, through the real pipeline: find_candidates stores and
        // routes the leads, save_research stores the evidence and re-scores, and §7.6 decides the tier.
        var campaignId = await server.NewScoredCampaignAsync();

        var actual = new List<string>();

        foreach (var example in WorkedExamples.All)
        {
            var leadId = await server.LeadIdForAsync(campaignId, example.Company);
            var saved = await server.SaveResearchAsync(campaignId, leadId, example.Research);

            saved.GetProperty("tier").GetString().ShouldBe(
                example.ExpectedTier,
                $"docs/03 §8 puts {example} in tier {example.ExpectedTier}. "
                + $"src/tests/Fixtures/research/README.md has the arithmetic. Got: {saved}");

            saved.GetProperty("score").GetInt32().ShouldBe(
                example.ExpectedScore,
                $"{example}: §7.6's arithmetic over the fixture research and the stored lead. Got: {saved}");

            actual.Add(saved.GetProperty("tier").GetString() ?? "?");
        }

        actual.ShouldBe(
            [LeadTiers.A, LeadTiers.A, LeadTiers.B],
            "implementation-plan C6: 'the three worked examples ... land in tiers A, A, B'.");
    }

    [Fact]
    public async Task Re_scoring_after_research_keeps_the_researched_lead_where_it_was()
    {
        // save_research re-scores one lead; score_leads re-scores all of them. The two must agree, or a
        // marketer would watch a lead change tier just because somebody re-ran the scorer.
        var campaignId = await server.NewScoredCampaignAsync();
        var leadId = await server.LeadIdForAsync(campaignId, WorkedExamples.BayouFulfillment.Company);

        var saved = await server.SaveResearchAsync(
            campaignId,
            leadId,
            WorkedExamples.BayouFulfillment.Research);

        var afterFullRun = await server.ScoreLeadsAsync(campaignId);

        afterFullRun.GetProperty("tiers").GetProperty(LeadTiers.A).GetInt32().ShouldBe(
            1,
            "exactly one lead in the campaign has cited evidence now. "
            + $"Got: {afterFullRun}");

        var lead = await server.GetLeadAsync(campaignId, leadId);

        lead.GetProperty("score").GetInt32().ShouldBe(
            saved.GetProperty("score").GetInt32(),
            $"save_research and score_leads have to compute the same score. Got: {Trim(lead)}");
        lead.GetProperty("tier").GetString().ShouldBe(saved.GetProperty("tier").GetString());
    }

    [Fact]
    public async Task An_unknown_campaign_is_NOT_FOUND()
    {
        var error = await ToolCall.ErrorAsync(
            server.Client,
            "score_leads",
            new Dictionary<string, object?> { ["campaignId"] = "cmp_NOPE99" },
            server.Diagnostics);

        error.Code.ShouldBe("NOT_FOUND", "mcp-tools.md §Errors. Raw: " + error.RawJson);
    }

    /// <summary>
    /// A valid override that puts 0.9 on <c>confidence</c> - the one feature research cannot move - so a
    /// lead's score on this scale is unmistakably different from its score on the profile's.
    /// </summary>
    private static Dictionary<string, object?> ConfidenceHeavyWeights => new(StringComparer.Ordinal)
    {
        [ScoreFeatures.SegmentFit] = 0.02,
        [ScoreFeatures.SizeFit] = 0.02,
        [ScoreFeatures.FacilityFit] = 0.02,
        [ScoreFeatures.Signals] = 0.02,
        [ScoreFeatures.Proximity] = 0.02,
        [ScoreFeatures.Confidence] = 0.90,
    };

    /// <summary><c>get_lead</c>'s <c>updatedAt</c>, parsed from the wire format.</summary>
    private static DateTimeOffset UpdatedAt(JsonElement lead)
    {
        lead.TryGetProperty("updatedAt", out var value).ShouldBeTrue(
            $"technical-design §5.2 gives leads an updated_at, and get_lead returns it. Got: {Trim(lead)}");

        return DateTimeOffset.Parse(
            value.GetString() ?? string.Empty,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal);
    }

    private static string Trim(JsonElement payload)
    {
        var text = payload.ToString();
        return text.Length <= 1200 ? text : text[..1200] + "…";
    }
}
