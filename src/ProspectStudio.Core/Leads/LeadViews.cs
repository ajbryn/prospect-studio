using System.Text.Json.Nodes;

namespace ProspectStudio.Core.Leads;

/// <summary>
/// The input of <c>list_leads</c> as the caller sends it (mcp-tools.md §list_leads). Every filter is
/// optional; <see cref="Status"/> and <see cref="Tier"/> are arrays ("any of these") while
/// <see cref="DealerId"/> and <see cref="ResearchStatus"/> take a single value.
/// </summary>
public sealed record ListLeadsRequest(
    string CampaignId,
    IReadOnlyList<string>? Status = null,
    IReadOnlyList<string>? Tier = null,
    string? DealerId = null,
    int? MinScore = null,
    string? ResearchStatus = null,
    string? Sort = null,
    int? Limit = null,
    int? Offset = null);

/// <summary>The same filters, validated and defaulted, as the store applies them in SQL.</summary>
public sealed record LeadQuery(
    string CampaignId,
    IReadOnlyList<string> Statuses,
    IReadOnlyList<string> Tiers,
    string? DealerId,
    int? MinScore,
    string? ResearchStatus,
    string Sort,
    int Limit,
    int Offset);

/// <summary>
/// One row as the store reads it. The segment and the top signal are missing on purpose: both are
/// decided in Core - the segment from the saved profile, the top signal from the campaign's
/// <c>signals</c> rows - so neither needs a column of its own or a query inside JSON.
/// </summary>
public sealed record LeadListRecord(
    string Id,
    string Name,
    string? City,
    string? TaxonomyPrimary,
    string? TaxonomyPath,
    int? Score,
    string? Tier,
    string? Dealer,
    string Status,
    string ResearchStatus);

/// <summary>One page of <see cref="LeadListRecord"/>, with the total before paging.</summary>
public sealed record LeadListPage(int Total, IReadOnlyList<LeadListRecord> Records);

/// <summary>
/// One row of <c>list_leads</c>' output, exactly the ten fields mcp-tools.md §list_leads shows: the
/// whole lead is what <c>get_lead</c> is for, and a page of 25 has to fit in about 4,000 tokens.
/// </summary>
public sealed record LeadRow(
    string Id,
    string Name,
    string? City,
    string? Segment,
    int? Score,
    string? Tier,
    string? Dealer,
    string Status,
    string Research,
    string? TopSignal);

public sealed record LeadPage(int Total, IReadOnlyList<LeadRow> Rows);

/// <summary>A lead named for a message: its id, which is what a retry needs, and its company name.</summary>
public sealed record LeadName(string LeadId, string Name);

/// <summary>One <c>signals</c> row, with the lead it belongs to.</summary>
public sealed record SignalRow(string LeadId, string Type, string Text, string Url, string Date);

/// <summary>One cited signal as <c>get_lead</c> returns it.</summary>
public sealed record SignalView(string Type, string Text, string Url, string Date);

/// <summary>Everything the store knows about one lead, for <c>get_lead</c>.</summary>
public sealed record LeadRecord(
    string Id,
    string Status,
    string Name,
    string? Address,
    string? City,
    string? State,
    string? Zip,
    string? Website,
    string? Phone,
    string? TaxonomyPrimary,
    string? TaxonomyPath,
    double Confidence,
    string OvertureId,
    string Release,
    string? FeaturesJson,
    int? Score,
    string? Tier,
    string? ScoreBreakdownJson,
    string ResearchStatus,
    string? Notes,
    string? ContactName,
    string? ContactTitle,
    string? Cohort,
    string? DealerId,
    string? Dealer,
    string? BranchId,
    string? Branch,
    string? Assignment,
    string? SuppressionReason,
    string? ResearchJson,
    IReadOnlyList<SignalRow> Signals,
    DateTimeOffset UpdatedAt);

/// <summary>
/// The full lead <c>get_lead</c> returns (mcp-tools.md §get_lead). <see cref="Code"/>,
/// <see cref="Cohort"/> and <see cref="WebExcerpt"/> are present and null until C11, C13 and C7 fill
/// them, so those chunks change a value rather than the response shape.
/// </summary>
/// <param name="ScoreDetail">
/// The rest of the stored <see cref="ScoreBreakdown"/>: the base before the adjustment, the adjustment
/// applied, the instant the signal window was measured from, which employee minimum <c>sizeFit</c>
/// used, and the score §7.6's tier-A cap held the lead back from. Without it a capped score reads as a
/// number that is mysteriously lower than its own breakdown adds up to.
/// </param>
public sealed record LeadDetail(
    string Id,
    string Name,
    string Status,
    string? Address,
    string? City,
    string? State,
    string? Zip,
    string? Website,
    string? Phone,
    string? Category,
    double Confidence,
    string OvertureId,
    string Release,
    string? Segment,
    JsonNode? Features,
    int? Score,
    string? Tier,
    IReadOnlyList<ScoreFeatureContribution> ScoreBreakdown,
    JsonNode? ScoreDetail,
    JsonNode? Research,
    IReadOnlyList<SignalView> Signals,
    string ResearchStatus,
    string? DealerId,
    string? Dealer,
    string? BranchId,
    string? Branch,
    string? Assignment,
    string? SuppressionReason,
    string? Notes,
    string? ContactName,
    string? ContactTitle,
    string? Code,
    string? Cohort,
    string? WebExcerpt,
    DateTimeOffset UpdatedAt);

/// <summary>One entry of <c>update_leads</c>' <c>updates</c> array (mcp-tools.md §update_leads).</summary>
public sealed record LeadUpdate(
    string LeadId,
    string? Status = null,
    string? DealerId = null,
    string? Notes = null,
    string? ContactName = null,
    string? ContactTitle = null);

/// <summary>
/// One row <c>update_leads</c> refused, named so the marketer is never left believing a change landed.
/// </summary>
public sealed record LeadUpdateError(string LeadId, string Message);

/// <summary>What <c>update_leads</c> returns: <c>{ updated, errors }</c>.</summary>
public sealed record UpdateLeadsResult(int Updated, IReadOnlyList<LeadUpdateError> Errors);

/// <summary>
/// What <c>score_leads</c> returns (mcp-tools.md §score_leads). <see cref="Skipped"/> counts the
/// <c>suppressed</c> leads it leaves alone - real leads compliance removed, which stay in the
/// denominator - while <c>duplicate</c> rows are not leads and appear in neither number.
/// <see cref="Tiers"/> covers the scored leads only.
/// </summary>
public sealed record ScoreLeadsResult(
    int Scored,
    int Skipped,
    IReadOnlyDictionary<string, int> Tiers,
    IReadOnlyDictionary<string, double> Weights);

/// <summary>
/// What <c>save_research</c> returns. <see cref="Delta"/> is signed and null when the lead had no
/// previous score, which is a different statement from "the research changed nothing".
/// </summary>
public sealed record SaveResearchResult(bool Saved, int Score, string Tier, int? Delta);

/// <summary>A score to write back, with the two JSON documents that explain it.</summary>
public sealed record LeadScoreWrite(
    string LeadId,
    int Score,
    string Tier,
    string FeaturesJson,
    string ScoreBreakdownJson);

/// <summary>A lead as <c>update_leads</c> finds it, before deciding what may change.</summary>
public sealed record LeadMutationTarget(
    string LeadId,
    string Status,
    string? DealerId,
    string? BranchId,
    string? Assignment,
    double Lat,
    double Lon);

/// <summary>
/// The columns one <c>update_leads</c> row changes. A null member is "leave it alone", which is why
/// nothing here can clear a value - no tool contract asks to.
/// </summary>
public sealed record LeadMutation(
    string LeadId,
    string? Status = null,
    string? DealerId = null,
    string? BranchId = null,
    string? Assignment = null,
    string? Notes = null,
    string? ContactName = null,
    string? ContactTitle = null);

/// <summary>One <c>signals</c> row to store, extracted out of a research document.</summary>
public sealed record SignalWrite(string Id, string Type, string Text, string Url, string Date);

/// <summary>
/// One research document to store, with the fields §5.2 keeps as columns beside it.
/// </summary>
/// <param name="LlmAdjustment">
/// A copy of the document's own <c>llmAdjustment</c>. Denormalized so scoring never filters inside the
/// JSON (CLAUDE.md); the two must always agree, which is why they are written together.
/// </param>
public sealed record ResearchWrite(
    string ResearchJson,
    int LlmAdjustment,
    string ResearchStatus,
    DateTimeOffset SavedAt,
    IReadOnlyList<SignalWrite> Signals);

/// <summary>
/// What a lead already has stored from an earlier scoring run, so a re-run can tell a real change from a
/// no-op without reading the row a second time.
/// </summary>
/// <remarks>
/// It is not evidence - nothing here is scored. It travels with the evidence because the alternative is a
/// second query over the same rows, and <c>score_leads</c> needs the comparison for every lead in the
/// campaign: §7.6 is deterministic, so a re-run over unchanged evidence must leave <c>updated_at</c>
/// alone, or every re-run looks like somebody edited 76 leads.
/// </remarks>
public sealed record StoredScore(
    int? Score,
    string? Tier,
    string? FeaturesJson,
    string? ScoreBreakdownJson);

/// <summary>
/// What §7.6 scores one lead from, as the store reads it: the site's own columns, the branch it is
/// routed to, the saved research document and the extracted signal rows.
/// </summary>
/// <param name="LlmAdjustment">
/// From the <c>research.llm_adjustment</c> column rather than from inside
/// <paramref name="ResearchJson"/>, which is what that column exists for.
/// </param>
public sealed record LeadEvidence(
    string LeadId,
    string Status,
    string Name,
    string? TaxonomyPrimary,
    string? TaxonomyPath,
    double Confidence,
    double Lat,
    double Lon,
    string? DealerId,
    string? BranchId,
    double? BranchLat,
    double? BranchLon,
    string? ResearchJson,
    int LlmAdjustment,
    IReadOnlyList<SignalFact> Signals,
    StoredScore Stored);

/// <summary>
/// Reading and writing <c>leads</c>, <c>research</c> and <c>signals</c> (technical-design §5.2) for the
/// tools of mcp-tools.md §Leads. Core defines it; Infrastructure implements it with EF Core, filtering
/// and paging on real columns only (CLAUDE.md).
/// </summary>
public interface ILeadStore
{
    /// <summary>One page of leads matching <paramref name="query"/>, with the total before paging.</summary>
    Task<LeadListPage> ListAsync(LeadQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// The signals of the named leads. Read as rows for the page in hand rather than ordered in SQL:
    /// <c>signals.date</c> is TEXT because the schema admits <c>YYYY-MM</c>, and picking "the latest" by
    /// string comparison would be exactly the lexicographic shortcut the provider-neutrality rule exists
    /// to stop.
    /// </summary>
    Task<IReadOnlyList<SignalRow>> ReadSignalsAsync(
        string campaignId,
        IReadOnlyList<string> leadIds,
        CancellationToken cancellationToken);

    /// <summary>One lead with everything <c>get_lead</c> shows, or null when there is no such lead.</summary>
    Task<LeadRecord?> FindAsync(string campaignId, string leadId, CancellationToken cancellationToken);

    /// <summary>
    /// The lead §7.2's dedupe kept for the company a <c>duplicate</c> row belongs to - the lead a caller
    /// who reached for the duplicate actually wants - or null when there is none.
    /// </summary>
    /// <remarks>
    /// No column records this: §7.2 collapses a dedupe group into one <c>companies</c> row, so the primary
    /// is the non-<c>duplicate</c> lead whose site shares the duplicate's <c>company_id</c>.
    /// </remarks>
    Task<LeadName?> FindPrimaryAsync(string campaignId, string duplicateLeadId, CancellationToken cancellationToken);

    /// <summary>
    /// What §7.6 scores, for every lead in the campaign or for one of them when
    /// <paramref name="leadId"/> is given.
    /// </summary>
    Task<IReadOnlyList<LeadEvidence>> ReadEvidenceAsync(
        string campaignId,
        string? leadId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes scores, tiers and the two JSON documents back, in batches (§5.3), stamping
    /// <c>updated_at</c> with <paramref name="updatedAt"/>. Only the leads whose score actually changed
    /// should be passed: the caller decides what counts as a change, and a row written here is a row
    /// whose timestamp moves.
    /// </summary>
    Task<int> SaveScoresAsync(
        string campaignId,
        IReadOnlyList<LeadScoreWrite> scores,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken);

    /// <summary>The named leads as <c>update_leads</c> needs to see them before changing anything.</summary>
    Task<IReadOnlyList<LeadMutationTarget>> FindTargetsAsync(
        string campaignId,
        IReadOnlyList<string> leadIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies the mutations <c>update_leads</c> decided on, stamping <c>updated_at</c> with
    /// <paramref name="updatedAt"/>; returns how many rows changed. The timestamp is passed in rather than
    /// read from the wall clock, the way <c>ICampaignStore</c>'s writers take theirs, so every writer of
    /// that column uses the one injected clock.
    /// </summary>
    Task<int> ApplyMutationsAsync(
        string campaignId,
        IReadOnlyList<LeadMutation> mutations,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the lead's research document, its signal rows and its <c>research_status</c>
    /// <strong>and</strong> writes the score computed from them, all in one transaction. Saving twice must
    /// leave one document and one set of signals, or a re-save would double the evidence §7.6 counts.
    /// </summary>
    /// <remarks>
    /// The score goes in the same transaction deliberately: research stored against a score that was never
    /// updated is a lead whose number disagrees with its own evidence, and nothing afterwards would notice.
    /// </remarks>
    Task SaveResearchAsync(
        string campaignId,
        string leadId,
        ResearchWrite research,
        LeadScoreWrite score,
        CancellationToken cancellationToken);
}
