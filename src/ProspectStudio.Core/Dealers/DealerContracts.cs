using System.Text.Json.Serialization;
using ProspectStudio.Core.Campaigns;

namespace ProspectStudio.Core.Dealers;

/// <summary>The <c>territories.level</c> values technical-design §5.2 allows.</summary>
public static class TerritoryLevels
{
    public const string Zip = "zip";
    public const string County = "county";

    public static bool IsKnown(string? level) => level is Zip or County;
}

/// <summary>The <c>leads.assignment</c> values technical-design §5.2 and §7.4 allow.</summary>
public static class Assignments
{
    /// <summary>Routed by a territory rule.</summary>
    public const string Auto = "auto";

    /// <summary>Set by hand (<c>update_leads</c>, the workbook) and never overwritten by §7.4.</summary>
    public const string Override = "override";

    /// <summary>No territory rule matched, so there is no dealer for this lead.</summary>
    public const string Gap = "gap";
}

/// <summary>The <c>suppression.reason</c> values technical-design §5.2 allows.</summary>
public static class SuppressionReasons
{
    public const string Customer = "customer";
    public const string Dnc = "dnc";
    public const string Dealer = "dealer";
    public const string Competitor = "competitor";
    public const string Other = "other";

    public static IReadOnlyList<string> All { get; } = [Customer, Dnc, Dealer, Competitor, Other];

    public static bool IsKnown(string? reason) => reason is not null && All.Contains(reason);
}

/// <summary>Which rule of technical-design §7.3 matched a suppression row.</summary>
public static class SuppressionRules
{
    /// <summary>Registrable domain (the §7.2 rule), regardless of ZIP.</summary>
    public const string Domain = "domain";

    /// <summary>Exact <c>name_norm</c>, with the same ZIP when the suppression row has one.</summary>
    public const string Name = "name";

    /// <summary>Jaro-Winkler ≥ 0.92 on <c>name_norm</c> with the same ZIP.</summary>
    public const string Fuzzy = "fuzzy";
}

/// <summary>
/// One territory rule as §7.4 needs it. <see cref="Code"/> is a ZIP5 or a county FIPS depending on
/// <see cref="Level"/>.
/// </summary>
public sealed record TerritoryRule(
    string DealerId,
    string? BranchId,
    string Level,
    string Code,
    int Priority);

/// <summary>A branch's location, for §7.4's nearest-branch tie-break.</summary>
public sealed record BranchPoint(string DealerId, string BranchId, double Lat, double Lon);

/// <summary>
/// What §7.4 routes: a lead's ZIP5 (already truncated from any ZIP+4), its county and its point.
/// </summary>
public sealed record AssignmentSubject(string? Zip, string? CountyFips, double Lat, double Lon);

/// <summary>
/// The outcome of §7.4 for one lead. <see cref="DealerId"/> and <see cref="BranchId"/> are null when
/// <see cref="Assignment"/> is <see cref="Assignments.Gap"/>.
/// </summary>
/// <param name="MatchedLevel">
/// <see cref="TerritoryLevels.Zip"/> or <see cref="TerritoryLevels.County"/> - which kind of rule won,
/// or null for a gap. <strong>Not part of any tool contract, and not redundant.</strong> Without it
/// nothing can tell a ZIP override apart from a county default that happens to name the same dealer,
/// so an implementation that skipped the ZIP rule entirely would still pass most outcome assertions.
/// </param>
public sealed record TerritoryAssignment(
    string? DealerId,
    string? BranchId,
    string Assignment,
    string? MatchedLevel);

/// <summary>One suppression row as §7.3 needs it, with the id so a match can be recorded.</summary>
/// <param name="Domain">Registrable domain, or null when the row has no website.</param>
/// <param name="Zip">ZIP5, or null when the row has none - §7.3's name rules then skip the ZIP test.</param>
public sealed record SuppressionRule(
    string Id,
    string NameNorm,
    string? Domain,
    string? Zip,
    string Reason);

/// <summary>What §7.3 tests: a lead's normalized name, its registrable domain and its ZIP5.</summary>
public sealed record SuppressionSubject(string NameNorm, string? Domain, string? Zip);

/// <summary>
/// A suppression match. §7.3: "Store the reason <strong>and the matching row id</strong>."
/// </summary>
/// <param name="Rule">
/// One of <see cref="SuppressionRules"/>. Same reasoning as
/// <see cref="TerritoryAssignment.MatchedLevel"/>: the fuzzy rule also matches every pair the exact
/// rule catches, so without a recorded rule an implementation that dropped the exact-name path would
/// pass every outcome-level test.
/// </param>
/// <param name="Similarity">
/// The Jaro-Winkler score between the two <c>name_norm</c> values, so a near-miss can be explained.
/// <c>1.0</c> for an exact name; for a domain match it is still the name score, which may be low.
/// </param>
public sealed record SuppressionHit(
    string SuppressionId,
    string Reason,
    string Rule,
    double Similarity);

/// <summary>The <c>kind</c> values of <c>import_list</c> (mcp-tools.md §import_list).</summary>
public static class ImportListKinds
{
    public const string Dealers = "dealers";
    public const string Territories = "territories";
    public const string Suppression = "suppression";

    /// <summary>Accepted from C13 onward; <c>import_list</c> advertises it from the start.</summary>
    public const string Warranty = "warranty";

    public static IReadOnlyList<string> All { get; } = [Dealers, Territories, Suppression, Warranty];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);
}

/// <summary>
/// One row-level import failure. <paramref name="Row"/> is the <strong>spreadsheet</strong> row - the
/// header is row 1 - because that is the number the user sees when they open the file.
/// </summary>
public sealed record ImportRowError(int Row, string Message);

/// <summary>
/// What <c>import_list</c> returns (mcp-tools.md §import_list): rows created, rows that replaced an
/// existing row, and the rows that were skipped with the reason.
/// </summary>
/// <param name="Removed">
/// Stored rows the file no longer names, deleted because the caller asked for the file to be
/// authoritative. Zero for an ordinary upsert, and omitted from the wire shape when it is zero.
/// </param>
public sealed record ImportListResult(
    int Imported,
    int Updated,
    IReadOnlyList<ImportRowError> Errors,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    int Removed = 0)
{
    /// <summary>
    /// Rows at most that <see cref="Errors"/> carries. A file that is broken throughout would
    /// otherwise push the response past the ~4,000-token budget (CLAUDE.md), and the first fifty row
    /// numbers are enough to see what is wrong with it.
    /// </summary>
    public const int MaxReportedErrors = 50;

    /// <summary>
    /// How many rows were rejected in total, which is larger than <see cref="Errors"/> when the list
    /// was capped at <see cref="MaxReportedErrors"/>. Omitted from the wire shape when nothing failed.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ErrorCount { get; init; }

    /// <summary>The result with <paramref name="errors"/> capped and counted.</summary>
    public static ImportListResult From(
        int imported,
        int updated,
        IReadOnlyList<ImportRowError> errors,
        int removed = 0)
    {
        ArgumentNullException.ThrowIfNull(errors);

        return new ImportListResult(
            imported,
            updated,
            errors.Count <= MaxReportedErrors ? errors : [.. errors.Take(MaxReportedErrors)],
            removed)
        {
            ErrorCount = errors.Count,
        };
    }
}

/// <summary>One row of <c>list_dealers</c> (mcp-tools.md §list_dealers).</summary>
public sealed record DealerSummary(string Id, string Name, int Branches, int TerritoryRows);

/// <summary>
/// The list counts <c>get_status.ready</c> reports (mcp-tools.md §get_status). C0 shipped these as
/// hardcoded zeros; C5 is where they become facts.
/// </summary>
public sealed record ListCounts(int Dealers, int Branches, int Territories, int Suppression);

/// <summary>What one <c>assign_dealers</c> run did (mcp-tools.md §assign_dealers / apply_suppression).</summary>
/// <param name="Assigned">Leads that now have a dealer, however they got it.</param>
/// <param name="Changed">
/// Leads this run actually changed, so <c>0</c> on an idempotent re-run. This is the figure the tool
/// reports as "counts changed".
/// </param>
/// <param name="Gaps">Leads with <see cref="Assignments.Gap"/>: in a county no territory covers.</param>
/// <param name="Overrides">Leads left alone because they carry <see cref="Assignments.Override"/>.</param>
/// <param name="ByDealer">
/// Leads per dealer, in <c>get_campaign</c>'s own <see cref="DealerLeadCount"/> shape so the two
/// breakdowns cannot drift apart. An overridden lead counts under the dealer it was moved to.
/// </param>
public sealed record AssignDealersResult(
    int Assigned,
    int Changed,
    int Gaps,
    int Overrides,
    IReadOnlyList<DealerLeadCount> ByDealer);

/// <summary>What one <c>apply_suppression</c> run did.</summary>
/// <param name="Suppressed">Leads now at status <c>suppressed</c>.</param>
/// <param name="Changed">Leads this run changed, so <c>0</c> on an idempotent re-run.</param>
/// <param name="ByReason">Counts per <see cref="SuppressionReasons"/> value, as the tool reports them.</param>
/// <param name="ApprovedSuppressed">
/// Leads this run took out of <see cref="Candidates.LeadStatuses.Approved"/>. Reported on its own
/// because it is the one number the user has to see: a do-not-contact request that arrives after
/// approval overrides that approval, and their mailing list just got shorter. A suppression that
/// changed nobody's approved list leaves it at zero, where the wire shape omits it.
/// </param>
/// <param name="DecisionsRestored">
/// The mirror: leads this run released from suppression back to a status somebody had decided -
/// <c>approved</c>, <c>review</c>, <c>hold</c> or <c>rejected</c> - because the list stopped naming
/// them. A mailing list growing back is as much a change as one shrinking, so it is not left to be
/// inferred from a total. Releases to plain <c>candidate</c> are ordinary and not counted here.
/// </param>
public sealed record ApplySuppressionResult(
    int Suppressed,
    int Changed,
    IReadOnlyDictionary<string, int> ByReason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    int ApprovedSuppressed = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    int DecisionsRestored = 0);

/// <summary>
/// A lead's routing state, as little of it as the C5 tests need. <see cref="SuppressionId"/> is the
/// matching suppression row (§7.3), which §5.2's <c>leads</c> column list does not mention - see the
/// C5 decisions note.
/// </summary>
public sealed record RoutedLead(
    string LeadId,
    string OvertureId,
    string Status,
    string? DealerId,
    string? BranchId,
    string? Assignment,
    string? SuppressionReason,
    string? SuppressionId);

/// <summary>
/// Reading and writing <c>dealers</c>, <c>dealer_branches</c>, <c>territories</c> and
/// <c>suppression</c> (technical-design §5.2). Core defines it; Infrastructure implements it with EF.
/// </summary>
public interface IDealerStore
{
    /// <summary>The <c>list_dealers</c> rows, with branch and territory-row counts.</summary>
    Task<IReadOnlyList<DealerSummary>> ListDealersAsync(CancellationToken cancellationToken);

    /// <summary>Row counts for <c>get_status.ready</c>.</summary>
    Task<ListCounts> CountListsAsync(CancellationToken cancellationToken);

    /// <summary>Every territory rule, for §7.4.</summary>
    Task<IReadOnlyList<TerritoryRule>> GetTerritoriesAsync(CancellationToken cancellationToken);

    /// <summary>Every branch's location, for §7.4's nearest-branch tie-break.</summary>
    Task<IReadOnlyList<BranchPoint>> GetBranchesAsync(CancellationToken cancellationToken);

    /// <summary>Every suppression row, for §7.3.</summary>
    Task<IReadOnlyList<SuppressionRule>> GetSuppressionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The union of one dealer's territory ZIPs and county FIPS, for <c>resolve_geography</c> type
    /// <c>dealer</c> (technical-design §6.2). Null when no such dealer exists.
    /// </summary>
    Task<DealerTerritoryScope?> FindTerritoryScopeAsync(string dealerId, CancellationToken cancellationToken);
}

/// <summary>A dealer's territory as a geography scope (technical-design §6.2).</summary>
public sealed record DealerTerritoryScope(
    string DealerId,
    string DealerName,
    IReadOnlyList<string> Zips,
    IReadOnlyList<string> CountyFips);

/// <summary>
/// Reading one of the three business lists off disk and into the database (mcp-tools.md
/// §import_list). CSV and XLSX, with the same headers on the first sheet.
/// </summary>
public interface IListImporter
{
    /// <summary>
    /// Imports <paramref name="path"/> as <paramref name="kind"/>. A bad row is reported in
    /// <see cref="ImportListResult.Errors"/> and the rest of the file still imports; re-importing the
    /// same file is idempotent.
    /// </summary>
    /// <param name="reason">
    /// The default <c>suppression.reason</c> for rows whose own <c>reason</c> column is blank
    /// (mcp-tools.md §import_list). Ignored for the other kinds.
    /// </param>
    Task<ImportListResult> ImportAsync(
        string kind,
        string path,
        string? reason,
        CancellationToken cancellationToken);

    /// <param name="replace">
    /// Makes the file authoritative for <c>suppression</c>: rows it no longer names are deleted and
    /// counted in <see cref="ImportListResult.Removed"/>. The overload without it upserts, because a
    /// stale entry only costs a lead while dropping a stale <c>dnc</c> row risks contacting someone who
    /// asked not to be - over-suppression is the safe direction. Ignored for the other kinds, whose
    /// rows are keyed by the ids their files carry.
    /// </param>
    /// <remarks>
    /// Two overloads rather than one method with an optional <paramref name="replace"/>: an optional
    /// parameter in front of the token would force the token to be optional too, and every store
    /// interface in this codebase requires its <c>CancellationToken</c> explicitly. That is a property
    /// worth three lines - it means every I/O call is cancellable because a caller decided so, and one
    /// defaulted token is the shape the next interface gets copied from.
    /// </remarks>
    Task<ImportListResult> ImportAsync(
        string kind,
        string path,
        string? reason,
        bool replace,
        CancellationToken cancellationToken);
}

/// <summary>
/// Routing a campaign's leads: <c>assign_dealers</c> (§7.4) and <c>apply_suppression</c> (§7.3). Both
/// are safe to re-run and both are what <c>find_candidates</c> now calls automatically.
/// </summary>
public interface ILeadRoutingStore
{
    /// <summary>
    /// §7.4 over every lead at status <c>candidate</c>. Leads carrying
    /// <see cref="Assignments.Override"/> are left exactly as they are.
    /// </summary>
    Task<AssignDealersResult> AssignDealersAsync(string campaignId, CancellationToken cancellationToken);

    /// <summary>
    /// §7.3 over every lead at status <c>candidate</c>, recording the reason and the matching
    /// suppression row id.
    /// </summary>
    Task<ApplySuppressionResult> ApplySuppressionAsync(string campaignId, CancellationToken cancellationToken);

    /// <summary>The campaign's leads with their routing columns, for tests and for the summaries.</summary>
    Task<IReadOnlyList<RoutedLead>> ListRoutedLeadsAsync(string campaignId, CancellationToken cancellationToken);
}
