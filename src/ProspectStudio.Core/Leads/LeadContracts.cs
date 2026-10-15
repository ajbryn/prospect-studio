namespace ProspectStudio.Core.Leads;

/// <summary>
/// The <c>leads.research_status</c> values technical-design §5.2 lists. Note that <see cref="Saved"/>
/// corresponds to a research document whose own <c>status</c> is <c>researched</c> - the two names
/// differ deliberately, so neither should be "corrected" to match the other.
/// </summary>
/// <remarks>
/// The three-way split is what makes <c>list_leads</c>' <c>researchStatus</c> filter useful: a
/// <see cref="NoSignal"/> lead has been researched and found nothing, so it will never reach tier A,
/// and it must not look the same as a <see cref="Saved"/> one or as a lead nobody has looked at.
/// </remarks>
public static class ResearchStatuses
{
    /// <summary>Nobody has researched this lead yet.</summary>
    public const string None = "none";

    /// <summary>Research was saved with <c>status: "researched"</c>.</summary>
    public const string Saved = "saved";

    /// <summary>Research was saved with <c>status: "no_signal"</c>: looked at, nothing found.</summary>
    public const string NoSignal = "no_signal";

    public static IReadOnlyList<string> All { get; } = [None, Saved, NoSignal];

    public static bool IsKnown(string? status) => status is not null && All.Contains(status);

    /// <summary>The <c>status</c> value a research document carries when it found something.</summary>
    public const string DocumentResearched = "researched";

    /// <summary>The <c>status</c> value a research document carries when it found nothing.</summary>
    public const string DocumentNoSignal = "no_signal";

    /// <summary>The lead status for a research document's own <c>status</c> value.</summary>
    public static string ForResearchStatus(string documentStatus) => documentStatus switch
    {
        DocumentResearched => Saved,
        DocumentNoSignal => NoSignal,
        _ => throw new ArgumentOutOfRangeException(
            nameof(documentStatus),
            documentStatus,
            "poc/schemas/research.schema.json allows 'researched' and 'no_signal'."),
    };
}

/// <summary>
/// The <c>sort</c> values of <c>list_leads</c>. mcp-tools.md §list_leads documents
/// <see cref="ScoreDesc"/>, which is also the default.
/// </summary>
public static class LeadSorts
{
    /// <summary>Highest score first. Ties break on lead id so paging is stable.</summary>
    public const string ScoreDesc = "score_desc";

    /// <summary>Lowest score first, for working up from the rejects.</summary>
    public const string ScoreAsc = "score_asc";

    /// <summary>By company name, for finding one lead by eye.</summary>
    public const string NameAsc = "name_asc";

    public static IReadOnlyList<string> All { get; } = [ScoreDesc, ScoreAsc, NameAsc];

    public static bool IsKnown(string? sort) => sort is not null && All.Contains(sort);
}

/// <summary>Limits on what one lead response carries, so a page stays inside the token budget.</summary>
public static class LeadResponseLimits
{
    /// <summary>mcp-tools.md §list_leads: the documented page size.</summary>
    public const int DefaultPageSize = 25;

    /// <summary>mcp-tools.md §get_lead: "If requested, a web excerpt (≤ 1,500 chars)."</summary>
    public const int MaxWebExcerpt = 1500;
}
