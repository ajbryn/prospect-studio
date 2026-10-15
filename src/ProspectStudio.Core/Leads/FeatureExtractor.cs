using System.Text.Json;
using ProspectStudio.Core.Candidates;

namespace ProspectStudio.Core.Leads;

/// <summary>One segment of a saved search profile, as §7.6's <c>segmentFit</c> and <c>sizeFit</c> read it.</summary>
/// <param name="MinEmployees">
/// The segment's own <c>minEmployees</c>, which §7.6 prefers over the profile's <c>size.employeesMin</c>
/// for a lead that matched this segment. Null when the segment sets none.
/// </param>
public sealed record ProfileSegment(
    string Name,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Keywords,
    int? MinEmployees);

/// <summary>How a lead matched the profile, and what that segment says about size.</summary>
public sealed record SegmentHit(SegmentMatch Match, string? Name, int? MinEmployees)
{
    /// <summary>No segment claimed the lead: §7.6's "otherwise", which scores 0.2.</summary>
    public static SegmentHit None { get; } = new(SegmentMatch.None, null, null);
}

/// <summary>
/// The parts of a saved search profile §7.6 scores against: each segment's categories, keywords and
/// employee minimum, plus the profile-wide <c>size.employeesMin</c>. The profile itself stays an opaque
/// document - it is read here, never queried in the database (CLAUDE.md).
/// </summary>
public sealed class ProfileSegments
{
    private ProfileSegments(IReadOnlyList<ProfileSegment> segments, int? minEmployees)
    {
        Segments = segments;
        MinEmployees = minEmployees;
    }

    /// <summary>A profile with no segments at all, for a campaign that has not saved one yet.</summary>
    public static ProfileSegments Empty { get; } = new([], null);

    public IReadOnlyList<ProfileSegment> Segments { get; }

    /// <summary>The profile's <c>size.employeesMin</c>, or null when it sets none.</summary>
    public int? MinEmployees { get; }

    public static ProfileSegments From(JsonElement profile)
    {
        if (profile.ValueKind != JsonValueKind.Object)
        {
            return Empty;
        }

        var segments = new List<ProfileSegment>();

        if (profile.TryGetProperty("segments", out var listed) && listed.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in listed.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object))
            {
                segments.Add(new ProfileSegment(
                    segment.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                    Strings(segment, "overtureCategories"),
                    Strings(segment, "keywords"),
                    segment.TryGetProperty("minEmployees", out var minimum)
                    && minimum.ValueKind == JsonValueKind.Number
                        ? minimum.GetInt32()
                        : null));
            }
        }

        var employeesMin = profile.TryGetProperty("size", out var size)
            && size.ValueKind == JsonValueKind.Object
            && size.TryGetProperty("employeesMin", out var value)
            && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : (int?)null;

        return new ProfileSegments(segments, employeesMin);
    }

    /// <summary>
    /// The segment that claims this lead, by §7.6's precedence: an Overture taxonomy match beats a name
    /// keyword, which beats a website keyword (C7). The first segment in profile order wins a tie, which
    /// is the order <c>find_candidates</c> reads them in too.
    /// </summary>
    public SegmentHit Match(string? name, string? taxonomyPrimary, string? taxonomyPath)
    {
        var categories = Categories(taxonomyPrimary, taxonomyPath);

        foreach (var segment in Segments)
        {
            if (segment.Categories.Any(category => categories.Contains(category, StringComparer.OrdinalIgnoreCase)))
            {
                return new SegmentHit(SegmentMatch.Taxonomy, segment.Name, segment.MinEmployees);
            }
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            foreach (var segment in Segments)
            {
                if (segment.Keywords.Any(keyword =>
                        keyword.Length > 0 && name.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                {
                    return new SegmentHit(SegmentMatch.NameKeyword, segment.Name, segment.MinEmployees);
                }
            }
        }

        return SegmentHit.None;
    }

    /// <summary>
    /// The categories a lead can be matched on: its leaf <c>taxonomy_primary</c> and every interior node
    /// of <c>taxonomy_path</c>, which is how <c>find_candidates</c> selected it in the first place (§6.3).
    /// </summary>
    private static List<string> Categories(string? taxonomyPrimary, string? taxonomyPath)
    {
        var categories = new List<string>();

        if (!string.IsNullOrWhiteSpace(taxonomyPrimary))
        {
            categories.Add(taxonomyPrimary);
        }

        if (!string.IsNullOrWhiteSpace(taxonomyPath))
        {
            categories.AddRange(taxonomyPath
                .Split(PlaceFields.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return categories;
    }

    private static IReadOnlyList<string> Strings(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ?
            [
                .. value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(item => item.Length > 0),
            ]
            : [];
}

/// <summary>
/// Gathers the evidence technical-design §7.6 scores: the segment a lead matched, what the research says
/// about its size and facility, its cited signals, how far it is from the branch it is routed to and how
/// confident Overture was.
/// </summary>
/// <remarks>
/// Two features cannot be filled before chunk C7 fetches websites, and both are deliberately left at
/// their "nothing known" value rather than at zero: <c>FacilityKeywordCount</c> is null, which §7.6
/// scores 0.4 ("no website text") rather than the 0.2 that "looked and found none" scores, and
/// <c>WebsiteReachable</c> is false, so <c>confidence</c> is 0.7 × Overture with nothing added. C6 scores
/// are therefore up to three points lower than C7's will be, which is recorded in the decisions log so
/// the drift is not read as a regression.
/// </remarks>
public sealed class FeatureExtractor(ProfileSegments profile)
{
    /// <summary>Statute miles, for §7.6's proximity bands.</summary>
    public const double MetersPerMile = 1609.344;

    public ProfileSegments Profile { get; } = profile;

    /// <summary>The segment a lead matched, which is also what <c>list_leads</c> shows in its row.</summary>
    public SegmentHit Segment(string? name, string? taxonomyPrimary, string? taxonomyPath) =>
        Profile.Match(name, taxonomyPrimary, taxonomyPath);

    public LeadFeatures Extract(LeadEvidence lead)
    {
        ArgumentNullException.ThrowIfNull(lead);

        var segment = Segment(lead.Name, lead.TaxonomyPrimary, lead.TaxonomyPath);
        var research = ResearchFacts.From(lead.ResearchJson);

        return new LeadFeatures
        {
            SegmentMatch = segment.Match,
            SegmentName = segment.Name,
            EmployeeEstimate = research.EmployeeEstimate,
            ProfileMinEmployees = Profile.MinEmployees,
            SegmentMinEmployees = segment.MinEmployees,
            ResearchFacilityLevel = research.FacilityLevel,

            // C7 fills these two; see the remarks above for why neither is zero.
            FacilityKeywordCount = null,
            WebsiteReachable = false,

            Signals = lead.Signals,
            BranchDistanceMiles = Miles(lead),
            OvertureConfidence = lead.Confidence,
            LlmAdjustment = lead.LlmAdjustment,
        };
    }

    /// <summary>
    /// Great-circle miles to the branch the lead is routed to, or null when it has no dealer - which
    /// §7.6 scores 0, because a lead no dealer would be handed is worth nothing to one.
    /// </summary>
    private static double? Miles(LeadEvidence lead) =>
        lead.BranchLat is { } lat && lead.BranchLon is { } lon
            ? Geohash.DistanceMeters(lead.Lat, lead.Lon, lat, lon) / MetersPerMile
            : null;
}

/// <summary>
/// The two facts §7.6 reads out of a research document. Everything else about the document stays opaque:
/// <c>llmAdjustment</c> comes from its own column, and the signals come from the <c>signals</c> rows.
/// </summary>
public sealed record ResearchFacts(string? FacilityLevel, int? EmployeeEstimate)
{
    public static ResearchFacts None { get; } = new(null, null);

    public static ResearchFacts From(string? researchJson)
    {
        if (string.IsNullOrWhiteSpace(researchJson))
        {
            return None;
        }

        try
        {
            using var document = JsonDocument.Parse(researchJson);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return None;
            }

            var level = root.TryGetProperty("facilityFit", out var facility)
                && facility.ValueKind == JsonValueKind.Object
                && facility.TryGetProperty("level", out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;

            var employees = root.TryGetProperty("employeeEstimate", out var estimate)
                && estimate.ValueKind == JsonValueKind.Object
                && estimate.TryGetProperty("value", out var count)
                && count.ValueKind == JsonValueKind.Number
                    ? count.GetInt32()
                    : (int?)null;

            return new ResearchFacts(FacilityFitLevels.IsKnown(level) ? level : null, employees);
        }
        catch (JsonException)
        {
            // A stored document that no longer parses must not stop a campaign re-scoring; the lead
            // simply scores as though nobody had researched it.
            return None;
        }
    }
}
