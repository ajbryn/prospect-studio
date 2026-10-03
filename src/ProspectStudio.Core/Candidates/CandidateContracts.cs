namespace ProspectStudio.Core.Candidates;

/// <summary>
/// The <c>taxonomy</c> STRUCT of an Overture Places row. <see cref="Primary"/> is the leaf and the
/// last element of <see cref="Hierarchy"/>; <c>basic_category</c> is a coarser rollup and often
/// <em>not</em> the leaf (technical-design §6.1, verified against release <c>2026-09-23.1</c>).
/// </summary>
public sealed record PlaceTaxonomy(
    string? Primary,
    IReadOnlyList<string> Hierarchy,
    IReadOnlyList<string> Alternates);

/// <summary>
/// One element of Overture's <c>addresses</c> struct list. <see cref="Region"/> is plain <c>TX</c>,
/// never <c>US-TX</c>; <see cref="Postcode"/> is sometimes ZIP+4; <see cref="Freeform"/> is absent on
/// 99,004 Texas rows.
/// </summary>
public sealed record PlaceAddress(
    string? Freeform,
    string? Locality,
    string? Postcode,
    string? Region,
    string? Country);

/// <summary>
/// One row of the local Overture Places extract, in the shape technical-design §6.1 writes. There is
/// no <c>categories</c> column any more and no county column: the county comes from the spatial join
/// in §6.3, which is why <see cref="CountyFips"/> is filled by the query rather than by the file.
/// </summary>
public sealed record OverturePlace(
    string Id,
    string? Name,
    string? BasicCategory,
    PlaceTaxonomy Taxonomy,
    double Confidence,
    IReadOnlyList<string> Websites,
    IReadOnlyList<string> Phones,
    IReadOnlyList<PlaceAddress> Addresses,
    double Lat,
    double Lon,
    string CountyFips);

/// <summary>
/// What <c>find_candidates</c> asks the Parquet for (technical-design §6.3). Matching is
/// <c>list_has_any(taxonomy.hierarchy, categories)</c> <strong>or</strong>
/// <c>taxonomy.primary IN (categories)</c> <strong>or</strong> a name-keyword match, so a place whose
/// only signal is its name still comes back.
/// </summary>
/// <param name="ExcludedCategories">
/// The profile's <c>exclusions.overtureCategories</c>, or <c>find_candidates</c>'
/// <c>excludedCategories</c> override. A place in one of these is dropped even when a keyword matched
/// its name, which is what keeps "Warehouse Grill &amp; Bar" out of a lift campaign.
/// </param>
/// <param name="ExcludedKeywords">
/// The profile's <c>exclusions.keywords</c>. A place whose name contains one is dropped even when its
/// category was a target - "Coastal Equipment Rental Services" is a distributor by category and still
/// the wrong lead.
/// </param>
public sealed record CandidateQuery(
    IReadOnlyList<string> CountyFips,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Keywords,
    double MinConfidence,
    IReadOnlyList<string> ExcludedCategories,
    IReadOnlyList<string> ExcludedKeywords,
    int Limit);

/// <summary>
/// A place as the <c>sites</c> table holds it (technical-design §5.2): the first address, website and
/// phone picked out of Overture's lists, the postcode cut back to five digits, and
/// <see cref="NameNorm"/> from §7.1.
/// </summary>
/// <param name="PayloadJson">
/// The raw Overture row, kept verbatim for the <c>source_records</c> table. Duplicates keep theirs
/// too (§7.2), so a merge can always be explained.
/// </param>
public sealed record CandidateSite(
    string OvertureId,
    string Name,
    string NameNorm,
    string? Address,
    string? City,
    string? State,
    string? Zip,
    string CountyFips,
    double Lat,
    double Lon,
    string? Phone,
    string? Website,
    string? TaxonomyPrimary,
    string? TaxonomyPath,
    string? BasicCategory,
    double Confidence,
    string Release,
    string PayloadJson);

/// <summary>Which rule of technical-design §7.2 collapsed a group.</summary>
public static class DedupeRules
{
    /// <summary>Rule 1: the registrable domain of the website, generic hosts ignored.</summary>
    public const string Domain = "domain";

    /// <summary>Rule 2: <c>name_norm</c> plus geohash-7.</summary>
    public const string NamePlace = "name-place";

    /// <summary>Rule 3: Jaro-Winkler ≥ 0.92 on <c>name_norm</c> within 200 m.</summary>
    public const string Fuzzy = "fuzzy";

    /// <summary>A group of one: nothing merged into it.</summary>
    public const string None = "none";
}

/// <summary>
/// One company after dedupe: the record that becomes the lead, and the records it absorbed, which are
/// stored as sites marked <c>duplicate</c> with their source records intact (§7.2).
/// </summary>
/// <param name="Rule">
/// Which <see cref="DedupeRules"/> rule merged the group - the first one in §7.2's order that fired.
/// <strong>Do not remove this as redundant.</strong> It is not part of any tool contract, but it is
/// the only way §7.2's three rules can be tested apart from one another: rule 3 (Jaro-Winkler within
/// 200 m) also matches every pair rule 2 catches, so without a recorded rule an implementation that
/// simply omitted rule 2 would pass every outcome-level test. It also lets a human see why two
/// records were merged. For a group assembled from edges of more than one kind (§7.2 is transitive),
/// the value is the earliest rule involved.
/// </param>
public sealed record CandidateGroup(
    CandidateSite Primary,
    IReadOnlyList<CandidateSite> Duplicates,
    string Rule);

/// <summary>What one <c>find_candidates</c> write did.</summary>
/// <param name="Companies">Companies this call <strong>inserted</strong>, so <c>0</c> on a re-run.</param>
/// <param name="Sites">Sites this call <strong>inserted</strong>, so <c>0</c> on a re-run.</param>
/// <param name="SourceRecords">Source records this call <strong>inserted</strong>, so <c>0</c> on a re-run.</param>
/// <param name="Leads">Leads that now exist for the campaign, new or already there.</param>
/// <param name="NewLeads">Leads this call created, which is <c>0</c> on an idempotent re-run.</param>
/// <param name="Duplicates">Records the groups absorbed, which is a property of the input either way.</param>
public sealed record CandidateStoreResult(
    int Companies,
    int Sites,
    int SourceRecords,
    int Leads,
    int NewLeads,
    int Duplicates);

/// <summary>A stored lead, as little of it as the C4 tests need.</summary>
public sealed record StoredLead(string LeadId, string OvertureId, string Status, string Name);

/// <summary>The <c>leads.status</c> values technical-design §5.2 lists.</summary>
public static class LeadStatuses
{
    public const string Candidate = "candidate";
    public const string Suppressed = "suppressed";
    public const string Duplicate = "duplicate";
    public const string Review = "review";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Hold = "hold";
}

/// <summary>One row of <c>lookup_overture_categories</c>' output (mcp-tools.md §lookup_overture_categories).</summary>
public sealed record OvertureCategoryCount(string Category, IReadOnlyList<string> Path, int CountInState);

/// <summary>
/// The local Overture extract as candidate search needs it. Core defines it; Infrastructure reads
/// <c>overture\&lt;release&gt;\places_&lt;ST&gt;.parquet</c> with DuckDB.
/// </summary>
public interface IPlacesSource
{
    /// <summary>False until the state's extract exists, which is <c>NOT_READY</c>.</summary>
    bool IsReady(string state);

    /// <summary>The places matching <paramref name="query"/>, with their county from the spatial join.</summary>
    Task<IReadOnlyList<OverturePlace>> FindAsync(
        string state,
        CandidateQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Category counts over the whole state extract, newest-release only, for
    /// <c>lookup_overture_categories</c>. Cached, because it is a full scan.
    /// </summary>
    Task<IReadOnlyList<OvertureCategoryCount>> CountCategoriesAsync(
        string state,
        string? query,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>
/// Candidate persistence: <c>companies</c>, <c>sites</c>, <c>source_records</c> and <c>leads</c>
/// (technical-design §5.2). Core defines it, Infrastructure implements it with EF Core in batches of
/// about 500 (§5.3) - 5,000 candidates must land in under 10 seconds, which rules out a
/// <c>SaveChanges</c> per row.
/// </summary>
public interface ICandidateStore
{
    /// <summary>
    /// Stores the groups as companies, sites, source records and leads. Idempotent: a second call with
    /// the same groups adds nothing, so <c>find_candidates</c> can be re-run safely (NFR-3).
    /// </summary>
    Task<CandidateStoreResult> StoreAsync(
        string campaignId,
        IReadOnlyList<CandidateGroup> groups,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredLead>> ListLeadsAsync(string campaignId, CancellationToken cancellationToken);

    /// <summary>
    /// The lead id for each of <paramref name="overtureIds"/> that the campaign holds, keyed by
    /// Overture id. It exists so a ten-row sample does not have to read five thousand leads.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> FindLeadIdsAsync(
        string campaignId,
        IReadOnlyList<string> overtureIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// <c>replace: true</c> on <c>find_candidates</c>: drops candidate leads that have no research
    /// yet, so a fresh search is not polluted by an old one. Returns how many went.
    /// </summary>
    Task<int> ClearCandidatesWithoutResearchAsync(string campaignId, CancellationToken cancellationToken);
}
