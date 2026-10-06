using System.Text.Json;
using ProspectStudio.Core.Leads;

namespace ProspectStudio.Tests.Shared;

/// <summary>
/// One of the three worked examples in
/// <c>docs/03 §8</c>, wired to the fixture row and the research document it is scored from.
/// </summary>
/// <param name="ExpectedScore">
/// The score technical-design §7.6's arithmetic produces. Only example 3's <strong>71</strong> is a
/// number docs/03 §8 itself gives; the other two illustrative figures (92, 88) do not reproduce under
/// either reading of <c>facilityFit</c>, so for those the tier is the contract and the score is pinned
/// only as a regression guard. <c>src/tests/Fixtures/research/README.md</c> has the full table.
/// </param>
/// <param name="FromTheDocument">
/// True when <see cref="ExpectedScore"/> is the figure docs/03 §8 publishes, so a failure message can
/// say whether the spec or the arithmetic moved.
/// </param>
internal sealed record WorkedExample(
    int Number,
    string Company,
    string PlaceId,
    string ResearchPath,
    string SegmentName,
    string DealerId,
    string BranchId,
    int ExpectedScore,
    string ExpectedTier,
    bool FromTheDocument = false)
{
    public SamplePlace Place => SamplePlaces.Row(PlaceId);

    public JsonElement Research => SampleResearch.Read(ResearchPath);

    /// <summary>The branch §7.4 routes this lead to, for §7.6's <c>proximity</c> band.</summary>
    public SampleDealer Branch => SampleDealers.Dealers.Single(dealer => dealer.BranchId == BranchId);

    /// <summary>Great-circle miles from the site to its assigned branch.</summary>
    public double BranchDistanceMiles =>
        WorkedExamples.MilesBetween(Place.Lat, Place.Lon, Branch.Lat, Branch.Lon);

    /// <summary>
    /// The features §7.6 scores, read out of the research document and the places row rather than
    /// written down, so an edit to either fixture moves the test with it.
    /// </summary>
    /// <remarks>
    /// This mirrors what the production <c>FeatureExtractor</c> must produce; it does not replace it.
    /// The end-to-end path - extractor and scorer together, over a real campaign - is covered by the
    /// <c>score_leads</c> tests in ProspectStudio.Mcp.Tests, which assert the same three tiers.
    /// </remarks>
    public LeadFeatures Features
    {
        get
        {
            var research = Research;
            var profile = SampleProfile.Load();

            return new LeadFeatures
            {
                // Every one of the three matches a segment by Overture category, not by name keyword;
                // ResearchFixtureIntegrityTests proves that against the sample profile.
                SegmentMatch = SegmentMatch.Taxonomy,
                SegmentName = SegmentName,
                EmployeeEstimate = research.TryGetProperty("employeeEstimate", out var employees)
                    ? employees.GetProperty("value").GetInt32()
                    : null,
                ProfileMinEmployees = profile.MinEmployees,

                // Null for all three: none of them matched the one segment that sets a minimum of its own
                // ("Facilities & property management", 50), so §7.6's applicable minimum is the profile's 20.
                SegmentMinEmployees = profile.SegmentMinEmployees.TryGetValue(SegmentName, out var segmentMinimum)
                    ? segmentMinimum
                    : null,
                ResearchFacilityLevel = research.TryGetProperty("facilityFit", out var facility)
                    ? facility.GetProperty("level").GetString()
                    : null,

                // Null, not zero: chunk C7 is what fetches websites, so in C6 there is no website text
                // at all and §7.6 scores that 0.4 rather than 0.2. The research level wins anyway.
                FacilityKeywordCount = null,
                Signals = Signals(research),
                BranchDistanceMiles = BranchDistanceMiles,
                OvertureConfidence = Place.Confidence,
                WebsiteReachable = false,
                LlmAdjustment = research.TryGetProperty("llmAdjustment", out var adjustment)
                    ? adjustment.GetInt32()
                    : 0,
            };
        }
    }

    public override string ToString() => $"example {Number} ({Company})";

    private static IReadOnlyList<SignalFact> Signals(JsonElement research) =>
        research.TryGetProperty("signals", out var signals) && signals.ValueKind == JsonValueKind.Array
            ? [.. signals.EnumerateArray().Select(signal => new SignalFact(
                signal.GetProperty("type").GetString() ?? string.Empty,
                signal.GetProperty("date").GetString() ?? string.Empty))]
            : [];
}

/// <summary>
/// The three worked examples of
/// <c>docs/03 §8</c>, which implementation-plan C6 requires to land in tiers A, A and B.
/// </summary>
internal static class WorkedExamples
{
    /// <summary>Example 1. Its research document is the spec pack's own <c>sample-research-valid.json</c>.</summary>
    public static WorkedExample BayouFulfillment { get; } = new(
        Number: 1,
        Company: "Bayou Fulfillment Co.",
        PlaceId: "fx_0001",
        ResearchPath: SampleResearch.ValidPath,
        SegmentName: "Warehousing & 3PL",
        DealerId: "gulf",
        BranchId: "gulf-west",

        // 0.9665 -> 97, +5 adjustment -> 102, clamped to 100. docs/03 §8 says 92, which does not
        // reproduce under either reading of facilityFit; the tier is what C6 requires.
        ExpectedScore: 100,
        ExpectedTier: LeadTiers.A);

    /// <summary>Example 2.</summary>
    public static WorkedExample GulfCoastSign { get; } = new(
        Number: 2,
        Company: "Gulf Coast Sign & Lighting",
        PlaceId: "fx_0002",
        ResearchPath: SampleResearch.GulfCoastSignPath,
        SegmentName: "Electrical, HVAC & sign contractors",
        DealerId: "bay",
        BranchId: "bay-pas",

        // 0.863 -> 86, no adjustment. docs/03 §8 says 88.
        ExpectedScore: 86,
        ExpectedTier: LeadTiers.A);

    /// <summary>
    /// Example 3, and the one that settles §7.6's <c>facilityFit</c> ruling: 0.713 → <strong>71</strong>,
    /// exactly the figure docs/03 §8 publishes, reachable only with the research level.
    /// </summary>
    public static WorkedExample WestparkMetalFab { get; } = new(
        Number: 3,
        Company: "Westpark Metal Fab",
        PlaceId: "fx_0007",
        ResearchPath: SampleResearch.WestparkMetalFabPath,
        SegmentName: "High-bay manufacturing",
        DealerId: "gulf",
        BranchId: "gulf-west",
        ExpectedScore: 71,
        ExpectedTier: LeadTiers.B,
        FromTheDocument: true);

    /// <summary>
    /// The score Westpark gets from the keyword path instead - 0.593 → 59, tier C. Pinned so the
    /// ruling in §7.6 cannot be reverted without a red test saying which number changed.
    /// </summary>
    public const int WestparkScoreFromTheKeywordPath = 59;

    public static IReadOnlyList<WorkedExample> All { get; } =
        [BayouFulfillment, GulfCoastSign, WestparkMetalFab];

    /// <summary>A valid <c>no_signal</c> document and the fixture row it belongs to.</summary>
    public const string NoSignalPlaceId = "fx_0011";

    /// <summary>Theory data: one row per worked example.</summary>
    public static TheoryData<string> Numbers() =>
        [.. All.Select(example => example.PlaceId)];

    public static WorkedExample ByPlaceId(string placeId) =>
        All.Single(example => example.PlaceId == placeId);

    /// <summary>Great-circle distance in statute miles (§7.4's haversine, for §7.6's bands).</summary>
    public static double MilesBetween(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusMiles = 3958.7613;

        var dLat = Radians(lat2 - lat1);
        var dLon = Radians(lon2 - lon1);
        var a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
            + (Math.Cos(Radians(lat1)) * Math.Cos(Radians(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));

        return earthRadiusMiles * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        static double Radians(double degrees) => degrees * Math.PI / 180d;
    }
}
