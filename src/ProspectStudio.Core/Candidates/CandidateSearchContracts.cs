using ProspectStudio.Core.Geography;

namespace ProspectStudio.Core.Candidates;

/// <summary>
/// The input of <c>find_candidates</c> (mcp-tools.md §find_candidates). Everything but the campaign
/// defaults to the saved search profile, so a skill can call it with the campaign alone.
/// </summary>
/// <param name="ExcludedCategories">
/// Overrides the profile's <c>exclusions.overtureCategories</c> when it is not null. An
/// <strong>empty</strong> list means "exclude nothing", not "fall back to the profile" - otherwise a
/// caller could never widen a search.
/// </param>
public sealed record FindCandidatesRequest(
    string CampaignId,
    GeoResolveRequest? Geo = null,
    IReadOnlyList<string>? Categories = null,
    IReadOnlyList<string>? Keywords = null,
    IReadOnlyList<string>? ExcludedCategories = null,
    double? MinConfidence = null,
    int? Limit = null,
    bool Replace = false);

/// <summary>One row of the <c>byCategory</c> breakdown.</summary>
public sealed record CategoryLeadCount(string Category, int Count);

/// <summary>
/// One row of the <c>byDealer</c> breakdown. Always empty in C4: dealer assignment is C5, but the key
/// is present so a skill cannot tell "nothing assigned" from "this server does not assign".
/// </summary>
public sealed record DealerCandidateCount(string Dealer, int Count);

/// <summary>A sample lead: a few fields for Claude to look at, never a whole lead (NFR-2).</summary>
public sealed record CandidateSample(
    string LeadId,
    string Name,
    string? Category,
    string? City,
    string? Zip,
    double Confidence);

/// <summary>
/// What <c>find_candidates</c> returns. <c>found</c> is <c>stored + duplicates</c>, matching the
/// arithmetic in mcp-tools.md's own example; <c>suppressed</c> is reported separately per reason and is
/// not part of <c>stored</c>.
/// </summary>
public sealed record FindCandidatesSummary(
    int Found,
    int Stored,
    int Duplicates,
    IReadOnlyDictionary<string, int> Suppressed,
    int CoverageGaps,
    IReadOnlyList<CategoryLeadCount> ByCategory,
    IReadOnlyList<DealerCandidateCount> ByDealer,
    IReadOnlyList<CandidateSample> Sample);
