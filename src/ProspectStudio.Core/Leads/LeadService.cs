using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProspectStudio.Core.Campaigns;
using ProspectStudio.Core.Candidates;
using ProspectStudio.Core.Dealers;
using ProspectStudio.Core.Domain;
using ProspectStudio.Core.Json;

namespace ProspectStudio.Core.Leads;

/// <summary>
/// The rules behind mcp-tools.md §Leads: <c>list_leads</c>, <c>get_lead</c>, <c>update_leads</c>,
/// <c>score_leads</c> and <c>save_research</c>. Filtering and paging happen in the store on real
/// columns; everything that is a decision - which filters are legal, what a lead's segment is, which
/// signal is worth showing, what §7.6 scores and what <c>update_leads</c> may change - happens here.
/// </summary>
public sealed class LeadService(
    ICampaignStore campaigns,
    ILeadStore leads,
    IDealerStore dealers,
    ResearchValidator research,
    TimeProvider clock)
{
    /// <summary>The largest page <c>list_leads</c> will return, so a response stays inside NFR-2.</summary>
    public const int MaxPageSize = 100;

    /// <summary>
    /// How much of a signal's text a <c>list_leads</c> row carries. The schema allows 240 characters,
    /// and 25 of those would be most of the response budget for something <c>get_lead</c> shows in full.
    /// </summary>
    public const int TopSignalTextLength = 90;

    /// <summary>The <c>/weights</c> pointer <c>score_leads</c> reports its sum rule at.</summary>
    public const string WeightsPointer = "/weights";

    public async Task<LeadPage> ListAsync(ListLeadsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var campaign = await FindCampaignAsync(request.CampaignId, cancellationToken).ConfigureAwait(false);
        var query = Validate(request);

        var page = await leads.ListAsync(query, cancellationToken).ConfigureAwait(false);
        var segments = ProfileSegments.From(Profile(campaign, out var document));
        using (document)
        {
            var signals = await leads
                .ReadSignalsAsync(
                    campaign.Id,
                    [.. page.Records.Select(record => record.Id)],
                    cancellationToken)
                .ConfigureAwait(false);

            var byLead = signals
                .GroupBy(signal => signal.LeadId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

            // The same instant §7.6's recency window is measured from, so a row's headline signal is one
            // that would score right now rather than one that scored when the lead was last scored.
            var asOf = clock.GetUtcNow();

            return new LeadPage(
                page.Total,
                [
                    .. page.Records.Select(record => new LeadRow(
                        record.Id,
                        record.Name,
                        record.City,
                        segments.Match(record.Name, record.TaxonomyPrimary, record.TaxonomyPath).Name,
                        record.Score,
                        record.Tier,
                        record.Dealer,
                        record.Status,
                        record.ResearchStatus,
                        TopSignal(byLead.TryGetValue(record.Id, out var rows) ? rows : [], asOf))),
                ]);
        }
    }

    public async Task<LeadDetail> GetAsync(
        string campaignId,
        string leadId,
        bool includeWebExcerpt,
        CancellationToken cancellationToken)
    {
        var campaign = await FindCampaignAsync(campaignId, cancellationToken).ConfigureAwait(false);
        var record = await leads.FindAsync(campaign.Id, Trimmed(leadId), cancellationToken).ConfigureAwait(false)
            ?? throw new LeadNotFoundException(campaign.Id, Trimmed(leadId));

        var segments = ProfileSegments.From(Profile(campaign, out var document));
        using (document)
        {
            var breakdown = Node(record.ScoreBreakdownJson);

            return new LeadDetail(
                record.Id,
                record.Name,
                record.Status,
                record.Address,
                record.City,
                record.State,
                record.Zip,
                record.Website,
                record.Phone,
                record.TaxonomyPrimary,
                record.Confidence,
                record.OvertureId,
                record.Release,
                segments.Match(record.Name, record.TaxonomyPrimary, record.TaxonomyPath).Name,
                Node(record.FeaturesJson),
                record.Score,
                record.Tier,
                Contributions(breakdown),
                ScoreDetail(breakdown),
                Node(record.ResearchJson),
                [.. record.Signals.Select(signal => new SignalView(signal.Type, signal.Text, signal.Url, signal.Date))],
                record.ResearchStatus,
                record.DealerId,
                record.Dealer,
                record.BranchId,
                record.Branch,
                record.Assignment,
                record.SuppressionReason,
                record.Notes,
                record.ContactName,
                record.ContactTitle,

                // C11 mints tracking codes and C13 assigns cohorts; the keys are here from C6 so those
                // chunks change a value rather than the shape of this response (mcp-tools.md §get_lead).
                Code: null,
                Cohort: record.Cohort,

                // C7 is what fetches websites, so there is nothing to return whether the caller asked
                // for an excerpt or not - and an empty string would read as "fetched, no text". The key
                // is here from C6 so C7 changes a value rather than the shape (mcp-tools.md §get_lead).
                WebExcerpt: null,
                record.UpdatedAt);
        }
    }

    public async Task<UpdateLeadsResult> UpdateAsync(
        string campaignId,
        IReadOnlyList<LeadUpdate> updates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updates);

        var campaign = await FindCampaignAsync(campaignId, cancellationToken).ConfigureAwait(false);

        if (updates.Count == 0)
        {
            throw new LeadRequestException(
                "No updates were passed.",
                "Pass updates as a JSON array, for example [{\"leadId\": \"L0007\", \"status\": \"approved\"}].");
        }

        var requested = updates
            .Select(update => Trimmed(update.LeadId))
            .Where(leadId => leadId.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var targets = (await leads.FindTargetsAsync(campaign.Id, requested, cancellationToken).ConfigureAwait(false))
            .ToDictionary(target => target.LeadId, StringComparer.Ordinal);
        var branches = await dealers.GetBranchesAsync(cancellationToken).ConfigureAwait(false);

        var mutations = new List<LeadMutation>();
        var errors = new List<LeadUpdateError>();

        foreach (var update in updates)
        {
            var leadId = Trimmed(update.LeadId);

            if (leadId.Length == 0)
            {
                errors.Add(new LeadUpdateError(string.Empty, "An update has no leadId."));
                continue;
            }

            if (!targets.TryGetValue(leadId, out var target))
            {
                errors.Add(new LeadUpdateError(leadId, $"There is no lead '{leadId}' in this campaign."));
                continue;
            }

            if (Refuse(update, target, branches, out var refusal, out var branchId))
            {
                errors.Add(new LeadUpdateError(leadId, refusal));
                continue;
            }

            var dealerId = Trimmed(update.DealerId) is { Length: > 0 } dealer ? dealer : null;

            var mutation = new LeadMutation(
                leadId,
                Status: Trimmed(update.Status) is { Length: > 0 } status ? status : null,
                DealerId: dealerId,
                BranchId: dealerId is null ? null : branchId,

                // §7.4: a dealer chosen by hand is an override, and neither assign_dealers nor
                // apply_suppression may move it back.
                Assignment: dealerId is null ? null : Assignments.Override,
                Notes: update.Notes,
                ContactName: update.ContactName,
                ContactTitle: update.ContactTitle);

            if (mutation is { Status: null, DealerId: null, Notes: null, ContactName: null, ContactTitle: null })
            {
                errors.Add(new LeadUpdateError(
                    leadId,
                    "Nothing to change: pass status, dealerId, notes, contactName or contactTitle."));
                continue;
            }

            mutations.Add(mutation);
        }

        var updated = mutations.Count == 0
            ? 0
            : await leads
                .ApplyMutationsAsync(campaign.Id, mutations, clock.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);

        return new UpdateLeadsResult(updated, errors);
    }

    public async Task<ScoreLeadsResult> ScoreAsync(
        string campaignId,
        JsonElement? weights,
        CancellationToken cancellationToken)
    {
        var campaign = await FindCampaignAsync(campaignId, cancellationToken).ConfigureAwait(false);

        var profile = Profile(campaign, out var document);
        using (document)
        {
            var scorer = new LeadScorer(Weights(weights, profile), clock);
            var extractor = new FeatureExtractor(ProfileSegments.From(profile));

            var evidence = await leads
                .ReadEvidenceAsync(campaign.Id, leadId: null, cancellationToken)
                .ConfigureAwait(false);

            var tiers = LeadTiers.All.ToDictionary(tier => tier, _ => 0, StringComparer.Ordinal);
            var changed = new List<LeadScoreWrite>();
            var scored = 0;
            var skipped = 0;

            foreach (var lead in evidence)
            {
                // A duplicate is the same company as another lead, kept so §7.2's merge can be
                // explained. It is outside the count 'scored + skipped' adds up to, because counting it
                // either way would count that company twice. The store leaves them out of the evidence
                // too; the rule is stated here because this is where the decision lives.
                if (IsProvenanceOnly(lead.Status))
                {
                    continue;
                }

                // mcp-tools.md §score_leads: a suppressed lead is never mailed, so scoring it would put
                // companies the marketer cannot contact into a tier count they read as a work queue. The
                // shortfall is reported rather than left to be noticed.
                if (IsSuppressed(lead.Status))
                {
                    skipped++;
                    continue;
                }

                var write = Scored(lead, extractor, scorer);
                scored++;
                tiers[write.Tier] = tiers[write.Tier] + 1;

                // Every lead is scored; only the ones whose score actually moved are written. §7.6 is
                // deterministic, so a re-run over unchanged evidence would otherwise stamp updated_at on
                // every lead in the campaign and make an idempotent call look like a bulk edit - which is
                // the column C8's workbook round-trip reads to find what a person changed.
                if (HasChanged(lead.Stored, write))
                {
                    changed.Add(write);
                }
            }

            await leads
                .SaveScoresAsync(campaign.Id, changed, clock.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);

            // The scale this run used, recorded on the campaign so save_research re-scores one lead the
            // same way as the other 75 (mcp-tools.md §score_leads). It is a record of the last run and
            // not a sticky setting: a run with no weights argument resolves to the profile's weights and
            // writes those back, so an override is always undone by re-running without one.
            var effective = scorer.Weights.ByFeature();
            await campaigns
                .SaveScoringWeightsAsync(
                    campaign.Id,
                    JsonSerializer.Serialize(effective, ProspectStudioJson.Options),
                    clock.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);

            // 'scored' is what the run measured, not what it wrote: a re-run that changes nothing has still
            // scored every lead, and reporting the write count would make idempotence look like failure.
            return new ScoreLeadsResult(scored, skipped, tiers, effective);
        }
    }

    public async Task<SaveResearchResult> SaveResearchAsync(
        string campaignId,
        string leadId,
        JsonElement document,
        CancellationToken cancellationToken)
    {
        var campaign = await FindCampaignAsync(campaignId, cancellationToken).ConfigureAwait(false);
        var id = Trimmed(leadId);

        // The lead is checked before the document: mcp-tools.md §Errors wants NOT_FOUND for an unknown
        // lead, because the document is fine and the lead is not.
        var existing = await leads.FindAsync(campaign.Id, id, cancellationToken).ConfigureAwait(false)
            ?? throw new LeadNotFoundException(campaign.Id, id);

        // Refused here, before anything is written. A duplicate row exists and is listable - list_leads
        // with status ["duplicate"] hands out its id - so NOT_FOUND would be a false statement about it,
        // and a refusal landing after the write would be worse than either: the research, its signals, the
        // research_status and updated_at would all stay behind on a row no scoring run ever looks at, so
        // the marketer's work would vanish from every list with no error they could act on. This is the
        // only point that still holds the lead's status before any write, and where validation already is.
        if (IsProvenanceOnly(existing.Status))
        {
            throw await DuplicateRefusalAsync(campaign.Id, id, cancellationToken).ConfigureAwait(false);
        }

        var problems = research.Validate(document);
        if (problems.Count > 0)
        {
            throw new ResearchInvalidException(problems);
        }

        var saved = clock.GetUtcNow();
        var write = new ResearchWrite(
            document.GetRawText(),
            Adjustment(document),
            ResearchStatuses.ForResearchStatus(Status(document)),
            saved,
            Signals(document));

        var profile = Profile(campaign, out var profileDocument);
        using (profileDocument)
        {
            // The campaign's own scale, not the profile's: score_leads decides which weights a campaign
            // is measured on and save_research follows. Re-scoring from the profile after an override run
            // would leave this one lead incomparable with every other lead in the same list - and the
            // list still sorts by score, so it would simply appear in the wrong place.
            var scorer = new LeadScorer(Scale(campaign, profile), clock);
            var extractor = new FeatureExtractor(ProfileSegments.From(profile));

            var evidence = (await leads.ReadEvidenceAsync(campaign.Id, id, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault() ?? throw new LeadNotFoundException(campaign.Id, id);

            // Scored from the submitted document rather than from a read-back of it, so the research and
            // the score it produces go in one transaction. The substitution is exactly what the store would
            // return afterwards - the document, its adjustment and its signals are what was just
            // submitted, and nothing else about the lead moved - and §7.6 counts signals rather than
            // reading their rows, so the stored order cannot matter.
            var score = Scored(
                evidence with
                {
                    ResearchJson = write.ResearchJson,
                    LlmAdjustment = write.LlmAdjustment,
                    Signals = [.. write.Signals.Select(signal => new SignalFact(signal.Type, signal.Date))],
                },
                extractor,
                scorer);

            await leads.SaveResearchAsync(campaign.Id, id, write, score, cancellationToken).ConfigureAwait(false);

            return new SaveResearchResult(
                Saved: true,
                score.Score,
                score.Tier,

                // Null rather than 0 when nothing had scored the lead: "the research changed nothing" is
                // a different and false statement (mcp-tools.md §save_research).
                Delta: existing.Score is { } previous ? score.Score - previous : null);
        }
    }

    /// <summary>
    /// Why a <c>duplicate</c> row cannot carry research, naming the lead §7.2 kept so the caller can retry
    /// against it. The id is in both halves because that is what a retry needs; the name is there because
    /// it is what the caller recognises.
    /// </summary>
    private async Task<LeadRequestException> DuplicateRefusalAsync(
        string campaignId,
        string leadId,
        CancellationToken cancellationToken)
    {
        var primary = await leads.FindPrimaryAsync(campaignId, leadId, cancellationToken).ConfigureAwait(false);

        return primary is null
            ? new LeadRequestException(
                $"Lead '{leadId}' is a duplicate record, kept so the dedupe can be explained, so it cannot "
                + "carry research of its own.",
                "Research the lead the dedupe kept for this company instead; list_leads shows it.")
            : new LeadRequestException(
                $"Lead '{leadId}' is a duplicate of '{primary.LeadId}' ({primary.Name}), so it cannot carry "
                + "research of its own.",
                $"Call save_research for '{primary.LeadId}' instead.");
    }

    /// <summary>
    /// Whether a freshly computed score differs from what the lead already holds. The breakdown's
    /// <c>asOf</c> is excluded on purpose: it records the instant §7.6's twelve-month window was measured
    /// from, so it moves on every run by definition, and counting that as a change would make an
    /// idempotent <c>score_leads</c> stamp every lead in the campaign every time it ran.
    /// </summary>
    private static bool HasChanged(StoredScore stored, LeadScoreWrite write) =>
        stored.Score != write.Score
        || !string.Equals(stored.Tier, write.Tier, StringComparison.Ordinal)
        || !string.Equals(stored.FeaturesJson, write.FeaturesJson, StringComparison.Ordinal)
        || !string.Equals(
            WithoutAsOf(stored.ScoreBreakdownJson),
            WithoutAsOf(write.ScoreBreakdownJson),
            StringComparison.Ordinal);

    /// <summary>
    /// A breakdown with its <c>asOf</c> removed, for comparing two of them. Both sides go through this, so
    /// they are written by the same writer and a difference in the text is a difference in the score's
    /// explanation rather than in how some double was rendered.
    /// </summary>
    private static string WithoutAsOf(string? breakdownJson)
    {
        if (string.IsNullOrWhiteSpace(breakdownJson))
        {
            return string.Empty;
        }

        try
        {
            if (JsonNode.Parse(breakdownJson) is not JsonObject breakdown)
            {
                return breakdownJson;
            }

            breakdown.Remove("asOf");
            return breakdown.ToJsonString();
        }
        catch (JsonException)
        {
            // A stored breakdown that no longer parses counts as different, so the row is rewritten.
            return breakdownJson;
        }
    }

    /// <summary>
    /// The scored lead, with the two documents that explain it. <c>features_json</c> and
    /// <c>score_breakdown_json</c> are written together so a stored score always has a breakdown that
    /// adds up to it.
    /// </summary>
    private static LeadScoreWrite Scored(LeadEvidence lead, FeatureExtractor extractor, LeadScorer scorer)
    {
        var features = extractor.Extract(lead);
        var breakdown = scorer.Score(features);

        return new LeadScoreWrite(
            lead.LeadId,
            breakdown.Score,
            breakdown.Tier,
            JsonSerializer.Serialize(features, ProspectStudioJson.Options),
            JsonSerializer.Serialize(breakdown, ProspectStudioJson.Options));
    }

    /// <summary>
    /// A lead <c>score_leads</c> counts as skipped: a company the marketer has and may not contact.
    /// </summary>
    private static bool IsSuppressed(string status) =>
        string.Equals(status, LeadStatuses.Suppressed, StringComparison.Ordinal);

    /// <summary>
    /// A lead that is provenance rather than a prospect. Deliberately distinct from
    /// <see cref="IsSuppressed"/>, because the two readings of "never mailed" differ: a suppressed lead
    /// is a real company the marketer is holding back and is counted as skipped, while a duplicate is the
    /// same company as another lead and must not be counted at all.
    /// </summary>
    private static bool IsProvenanceOnly(string status) =>
        string.Equals(status, LeadStatuses.Duplicate, StringComparison.Ordinal);

    /// <summary>
    /// Whether one <c>update_leads</c> row has to be refused, and why. A refused row changes nothing at
    /// all - not even the half of it that was fine - so a caller is never left with a lead that took the
    /// notes but not the dealer.
    /// </summary>
    private static bool Refuse(
        LeadUpdate update,
        LeadMutationTarget target,
        IReadOnlyList<BranchPoint> branches,
        out string refusal,
        out string? branchId)
    {
        refusal = string.Empty;
        branchId = null;

        if (Trimmed(update.Status) is { Length: > 0 } status)
        {
            if (!LeadStatuses.IsKnown(status))
            {
                refusal = $"'{status}' is not a lead status. Use one of: {string.Join(", ", LeadStatuses.All)}.";
                return true;
            }

            // §7.3 is a compliance rule, not a preference: a company is suppressed because a list says
            // not to contact it, and letting a status change undo that would take one tool call and
            // leave no record. The way back is to drop the row from the list and re-run
            // apply_suppression, which restores the status the lead had before.
            if (string.Equals(target.Status, LeadStatuses.Suppressed, StringComparison.Ordinal)
                && !string.Equals(status, LeadStatuses.Suppressed, StringComparison.Ordinal))
            {
                refusal = $"Lead '{target.LeadId}' is suppressed, so its status cannot be set to '{status}'. "
                    + "Remove the company from the suppression list and run apply_suppression instead.";
                return true;
            }
        }

        if (Trimmed(update.DealerId) is { Length: > 0 } dealerId)
        {
            var mine = branches.Where(branch => string.Equals(branch.DealerId, dealerId, StringComparison.Ordinal)).ToList();

            if (mine.Count == 0)
            {
                refusal = $"There is no dealer '{dealerId}'. Use list_dealers to see the dealers that exist.";
                return true;
            }

            // A lead with a dealer but no branch has no address to mail from, so the dealer's nearest
            // branch comes with the change - which is also §7.4's own tie-break.
            branchId = mine
                .OrderBy(branch => Geohash.DistanceMeters(target.Lat, target.Lon, branch.Lat, branch.Lon))
                .ThenBy(branch => branch.BranchId, StringComparer.Ordinal)
                .First()
                .BranchId;
        }

        return false;
    }

    /// <summary>
    /// The weights in force: the caller's, validated against §7.6's sum rule, or the saved profile's,
    /// or §7.6's defaults.
    /// </summary>
    private static ScoringWeights Weights(JsonElement? weights, JsonElement profile)
    {
        if (weights is not { } given || given.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return ScoringWeights.FromProfile(profile);
        }

        if (given.ValueKind != JsonValueKind.Object)
        {
            throw new LeadRequestException(
                "weights must be an object of feature names to numbers.",
                "Pass weights as {\"segmentFit\": 0.25, \"sizeFit\": 0.15, \"facilityFit\": 0.20, "
                + "\"signals\": 0.25, \"proximity\": 0.05, \"confidence\": 0.10}, or omit it.");
        }

        var problems = ScoringWeights.Validate(given, WeightsPointer);
        if (problems.Count > 0)
        {
            throw new ScoringWeightsInvalidException(problems);
        }

        return ScoringWeights.FromJson(given);
    }

    /// <summary>
    /// The scale a campaign is measured on: the weights its last <c>score_leads</c> run persisted, or the
    /// profile's when nothing has scored it yet.
    /// </summary>
    private static ScoringWeights Scale(Campaign campaign, JsonElement profile)
    {
        if (campaign.ScoringWeightsJson is not { Length: > 0 } stored)
        {
            return ScoringWeights.FromProfile(profile);
        }

        // FromJson copies the six numbers out, so nothing outlives the document.
        using var document = JsonDocument.Parse(stored);
        return ScoringWeights.FromJson(document.RootElement);
    }

    private async Task<Campaign> FindCampaignAsync(string campaignId, CancellationToken cancellationToken)
    {
        var id = Trimmed(campaignId);

        return await campaigns.FindAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new CampaignNotFoundException(id);
    }

    /// <summary>
    /// The campaign's saved profile. <paramref name="document"/> owns the memory the returned element
    /// points into, so every caller disposes it once it has finished reading.
    /// </summary>
    private static JsonElement Profile(Campaign campaign, out JsonDocument? document)
    {
        document = campaign.ProfileJson is { Length: > 0 } saved ? JsonDocument.Parse(saved) : null;
        return document?.RootElement ?? default;
    }

    /// <summary>
    /// Every filter checked and defaulted. An unrecognised <c>sort</c> or <c>researchStatus</c> is
    /// <c>VALIDATION_FAILED</c> rather than a silent default: a list in another order, or one that
    /// matched nothing, would otherwise read as an answer (mcp-tools.md §list_leads).
    /// </summary>
    private static LeadQuery Validate(ListLeadsRequest request)
    {
        var statuses = Known(
            request.Status,
            LeadStatuses.IsKnown,
            "status",
            LeadStatuses.All);
        var tiers = Known(request.Tier, LeadTiers.IsKnown, "tier", LeadTiers.All);

        var sort = Trimmed(request.Sort) is { Length: > 0 } asked ? asked : LeadSorts.ScoreDesc;
        if (!LeadSorts.IsKnown(sort))
        {
            throw new LeadRequestException(
                $"'{sort}' is not a sort list_leads knows.",
                $"Use one of: {string.Join(", ", LeadSorts.All)}.");
        }

        string? researchStatus = null;
        if (Trimmed(request.ResearchStatus) is { Length: > 0 } wanted)
        {
            if (!ResearchStatuses.IsKnown(wanted))
            {
                throw new LeadRequestException(
                    $"'{wanted}' is not a research status.",
                    $"Use one of: {string.Join(", ", ResearchStatuses.All)}. A research document's own "
                    + "status is 'researched', which the lead column records as 'saved'.");
            }

            researchStatus = wanted;
        }

        var limit = request.Limit ?? LeadResponseLimits.DefaultPageSize;
        if (limit is < 1 or > MaxPageSize)
        {
            throw new LeadRequestException(
                $"limit must be between 1 and {MaxPageSize}.",
                $"Ask for {LeadResponseLimits.DefaultPageSize} rows at a time and page with offset.");
        }

        var offset = request.Offset ?? 0;
        if (offset < 0)
        {
            throw new LeadRequestException("offset cannot be negative.", "Start at 0.");
        }

        return new LeadQuery(
            Trimmed(request.CampaignId),
            statuses,
            tiers,
            Trimmed(request.DealerId) is { Length: > 0 } dealerId ? dealerId : null,
            request.MinScore,
            researchStatus,
            sort,
            limit,
            offset);
    }

    private static IReadOnlyList<string> Known(
        IReadOnlyList<string>? values,
        Func<string, bool> isKnown,
        string name,
        IReadOnlyList<string> allowed)
    {
        if (values is null || values.Count == 0)
        {
            return [];
        }

        var wanted = values
            .Select(Trimmed)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var unknown = wanted.Where(value => !isKnown(value)).ToList();
        if (unknown.Count > 0)
        {
            throw new LeadRequestException(
                $"{name} has {(unknown.Count == 1 ? "a value" : "values")} list_leads does not know: "
                + $"{string.Join(", ", unknown.Select(value => $"'{value}'"))}.",
                $"Use any of: {string.Join(", ", allowed)}.");
        }

        return wanted;
    }

    /// <summary>
    /// The signal a <c>list_leads</c> row shows: one that <strong>actually scored</strong> in preference to
    /// one that did not, and the most recent of those. Dated, because "Permit: addition" with no date could
    /// be from 2019.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Qualification comes from <see cref="LeadScorer.Qualifies"/> rather than from a date rule written
    /// here, because the row and its own score have to agree: a permit mistyped into next month is not
    /// credited by §7.6, and naming it would advertise a transcription error as the reason for a score that
    /// came from a different signal. "Newest row" was not good enough, and neither was "newest buying
    /// signal" - the two signals can be the same type.
    /// </para>
    /// <para>
    /// The fallback is deliberate: a lead whose only signal is stale or a <c>registry</c> entry still shows
    /// it, with its date. Hiding it would make a researched lead with no usable evidence look exactly like
    /// one nobody has researched, which is the more misleading of the two.
    /// </para>
    /// </remarks>
    private static string? TopSignal(IReadOnlyList<SignalRow> signals, DateTimeOffset asOf)
    {
        if (signals.Count == 0)
        {
            return null;
        }

        var top = signals
            .OrderByDescending(signal => LeadScorer.Qualifies(new SignalFact(signal.Type, signal.Date), asOf))
            .ThenByDescending(signal => YearMonth(signal.Date))
            .ThenBy(signal => signal.Type, StringComparer.Ordinal)
            .First();

        var text = top.Text.Length <= TopSignalTextLength
            ? top.Text
            : top.Text[..TopSignalTextLength].TrimEnd() + "…";

        return $"{Capitalized(top.Type)}: {text} ({top.Date})";
    }

    /// <summary>
    /// A date as a sortable year-month number, computed here rather than by ordering the text column in
    /// SQL: the column is TEXT because the schema admits <c>YYYY-MM</c>, and leaning on lexicographic
    /// ISO ordering is the kind of reasoning the provider-neutrality rule exists to stop.
    /// </summary>
    private static int YearMonth(string date)
    {
        if (date.Length < 7 || date[4] != '-')
        {
            return 0;
        }

        return int.TryParse(date.AsSpan(0, 4), CultureInfo.InvariantCulture, out var year)
            && int.TryParse(date.AsSpan(5, 2), CultureInfo.InvariantCulture, out var month)
                ? (year * 12) + month
                : 0;
    }

    private static string Capitalized(string type) =>
        type.Length == 0 ? type : char.ToUpperInvariant(type[0]) + type[1..];

    private static string Status(JsonElement document) =>
        document.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
            ? status.GetString() ?? ResearchStatuses.DocumentNoSignal
            : ResearchStatuses.DocumentNoSignal;

    private static int Adjustment(JsonElement document) =>
        document.TryGetProperty("llmAdjustment", out var adjustment)
        && adjustment.ValueKind == JsonValueKind.Number
        && adjustment.TryGetInt32(out var value)
            ? value
            : 0;

    /// <summary>
    /// The document's cited signals as rows, so §7.6 and <c>list_leads</c>' <c>topSignal</c> read them as
    /// columns rather than reaching inside the JSON.
    /// </summary>
    private static IReadOnlyList<SignalWrite> Signals(JsonElement document)
    {
        if (!document.TryGetProperty("signals", out var signals) || signals.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. signals
                .EnumerateArray()
                .Where(signal => signal.ValueKind == JsonValueKind.Object)
                .Select(signal => new SignalWrite(
                    LeadIds.Signal(),
                    Text(signal, "type"),
                    Text(signal, "text"),
                    Text(signal, "url"),
                    Text(signal, "date"))),
        ];
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static JsonNode? Node(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);

    /// <summary>
    /// The per-feature rows of a stored breakdown, which is what <c>get_lead</c> returns as
    /// <c>scoreBreakdown[]</c>.
    /// </summary>
    private static IReadOnlyList<ScoreFeatureContribution> Contributions(JsonNode? breakdown)
    {
        if (breakdown?["features"] is not JsonArray features)
        {
            return [];
        }

        return
        [
            .. features
                .OfType<JsonNode>()
                .Select(row => new ScoreFeatureContribution(
                    row["feature"]?.GetValue<string>() ?? string.Empty,
                    row["value"]?.GetValue<double>() ?? 0d,
                    row["weight"]?.GetValue<double>() ?? 0d,
                    row["contribution"]?.GetValue<double>() ?? 0d)),
        ];
    }

    /// <summary>
    /// The rest of the stored breakdown - the base, the adjustment applied, the instant the signal window
    /// was measured from, the employee minimum <c>sizeFit</c> used and the score §7.6's cap held the lead
    /// back from. A capped score is otherwise lower than its own breakdown adds up to, with nothing to
    /// explain the difference.
    /// </summary>
    private static JsonNode? ScoreDetail(JsonNode? breakdown)
    {
        if (breakdown is not JsonObject stored)
        {
            return null;
        }

        var detail = new JsonObject();
        foreach (var name in new[] { "baseScore", "llmAdjustment", "asOf", "sizeMinimum", "cappedFrom" })
        {
            if (stored.TryGetPropertyValue(name, out var value))
            {
                detail[name] = value?.DeepClone();
            }
        }

        return detail.Count == 0 ? null : detail;
    }

    private static string Trimmed(string? value) => value?.Trim() ?? string.Empty;
}
