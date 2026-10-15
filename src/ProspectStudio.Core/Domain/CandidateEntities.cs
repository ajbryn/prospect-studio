namespace ProspectStudio.Core.Domain;

/// <summary>
/// A company: the identity a lead hangs off (technical-design §5.2). Persistence-ignorant - the
/// mapping lives in an <c>IEntityTypeConfiguration&lt;Company&gt;</c> in Infrastructure/Storage.
/// </summary>
public sealed class Company
{
    public required string Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Normalized name (technical-design §7.1); all name matching happens on this column.</summary>
    public required string NameNorm { get; set; }

    /// <summary>Registrable domain, generic hosts excluded (§7.2); null when there is no website.</summary>
    public string? Domain { get; set; }
}

/// <summary>
/// One physical place from Overture, with its provenance columns (technical-design §5.2).
/// </summary>
public sealed class Site
{
    public required string Id { get; set; }

    public required string CompanyId { get; set; }

    /// <summary>The Overture <c>id</c>. Unique, which is what makes a re-run idempotent.</summary>
    public required string OvertureId { get; set; }

    public required string Name { get; set; }

    public string? Address { get; set; }

    public string? City { get; set; }

    public string? State { get; set; }

    /// <summary>Five digits: Overture publishes ZIP+4 on some rows and it is truncated on the way in.</summary>
    public string? Zip { get; set; }

    public required string CountyFips { get; set; }

    public double Lat { get; set; }

    public double Lon { get; set; }

    public string? Phone { get; set; }

    public string? Website { get; set; }

    /// <summary>Overture's <c>taxonomy.primary</c>: the leaf category.</summary>
    public string? TaxonomyPrimary { get; set; }

    /// <summary><c>taxonomy.hierarchy</c>, root first, as a single path string.</summary>
    public string? TaxonomyPath { get; set; }

    /// <summary>Overture's <c>basic_category</c>: a coarser rollup, often not the leaf.</summary>
    public string? BasicCategory { get; set; }

    public double Confidence { get; set; }

    /// <summary>The Overture release this row came from, e.g. <c>2026-09-23.1</c>.</summary>
    public required string Release { get; set; }
}

/// <summary>
/// The raw record a site came from, kept verbatim for provenance (technical-design §5.2). Duplicates
/// keep theirs too (§7.2), so every merge can be explained after the fact.
/// </summary>
public sealed class SourceRecord
{
    public required string Id { get; set; }

    public required string SiteId { get; set; }

    /// <summary>Where it came from, e.g. <c>overture</c>.</summary>
    public required string Source { get; set; }

    /// <summary>The identifier that source used.</summary>
    public required string SourceId { get; set; }

    public DateTimeOffset RetrievedAt { get; set; }

    public string? License { get; set; }

    /// <summary>The raw row as opaque JSON text; never filtered inside (CLAUDE.md).</summary>
    public string? PayloadJson { get; set; }
}

/// <summary>
/// A lead: one site inside one campaign (technical-design §5.2). C4 fills the identity and status
/// columns; scoring, research and dealer routing arrive in C5 and C6.
/// </summary>
public sealed class Lead
{
    /// <summary>Half of the composite key, with <see cref="Id"/>.</summary>
    public required string CampaignId { get; set; }

    /// <summary><c>L0001</c>, numbered per campaign.</summary>
    public required string Id { get; set; }

    public required string SiteId { get; set; }

    /// <summary>One of <see cref="Candidates.LeadStatuses"/>.</summary>
    public required string Status { get; set; }

    public string? SuppressionReason { get; set; }

    /// <summary>
    /// The <c>suppression</c> row that matched (§7.3: "store the reason <strong>and</strong> the
    /// matching row id"), so a suppression can be explained rather than only reported.
    /// </summary>
    public string? SuppressionId { get; set; }

    /// <summary>
    /// The status this lead held before suppression took it, so releasing it gives that status back
    /// (§7.3). Null when the lead has never been suppressed, which releases it as a plain candidate.
    /// </summary>
    /// <remarks>
    /// Suppression applies whatever the status is, <c>approved</c> included - a do-not-contact row
    /// arriving after approval is the case the list exists for. Without this column the round trip
    /// (suppress an approved lead, then drop the row from the list) would hand it back as a candidate
    /// and throw away a decision a person made, silently.
    /// </remarks>
    public string? PreSuppressionStatus { get; set; }

    public string? DealerId { get; set; }

    public string? BranchId { get; set; }

    /// <summary><c>auto</c>, <c>override</c> or <c>gap</c> (§7.4). Null until C5 assigns dealers.</summary>
    public string? Assignment { get; set; }

    /// <summary>
    /// The <see cref="Leads.LeadFeatures"/> §7.6 scored, as opaque JSON text. Null until the lead has
    /// been scored; never filtered inside (CLAUDE.md), which is why <see cref="Score"/> and
    /// <see cref="Tier"/> are columns of their own.
    /// </summary>
    public string? FeaturesJson { get; set; }

    /// <summary>
    /// 0-100 from §7.6, or null for a lead nothing has scored yet - including one <c>score_leads</c>
    /// skipped. A 0 would read as "scored, and hopeless", and <c>save_research</c>'s <c>delta</c> is
    /// specified as null rather than 0 for exactly that lead.
    /// </summary>
    public int? Score { get; set; }

    /// <summary>One of <see cref="Leads.LeadTiers"/>, or null while <see cref="Score"/> is null.</summary>
    public string? Tier { get; set; }

    /// <summary>
    /// The <see cref="Leads.ScoreBreakdown"/> that explains <see cref="Score"/>, as opaque JSON text:
    /// every feature with its value, weight and contribution, the instant it was computed against, and
    /// whether §7.6's tier-A cap bound.
    /// </summary>
    public string? ScoreBreakdownJson { get; set; }

    /// <summary>
    /// One of <see cref="Leads.ResearchStatuses"/>. <c>none</c> rather than null for a lead nobody has
    /// researched, so <c>list_leads</c>' filter partitions every lead in the campaign.
    /// </summary>
    public string ResearchStatus { get; set; } = Leads.ResearchStatuses.None;

    /// <summary>Whatever the marketer wants to remember about the lead (<c>update_leads</c>, the workbook).</summary>
    public string? Notes { get; set; }

    public string? ContactName { get; set; }

    public string? ContactTitle { get; set; }

    /// <summary>The cohort path from §7.8, e.g. <c>wave1/A</c>. Null until C13 assigns cohorts.</summary>
    public string? Cohort { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
