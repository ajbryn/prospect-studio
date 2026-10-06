namespace ProspectStudio.Core.Leads;

/// <summary>
/// The six feature names of technical-design §7.6, which are also the keys
/// <c>score_breakdown_json</c> and the <c>scoringWeights</c> object use.
/// </summary>
public static class ScoreFeatures
{
    public const string SegmentFit = "segmentFit";
    public const string SizeFit = "sizeFit";
    public const string FacilityFit = "facilityFit";
    public const string Signals = "signals";
    public const string Proximity = "proximity";
    public const string Confidence = "confidence";

    /// <summary>In the order §7.6's table lists them, so a breakdown reads like the table.</summary>
    public static IReadOnlyList<string> All { get; } =
        [SegmentFit, SizeFit, FacilityFit, Signals, Proximity, Confidence];
}

/// <summary>
/// How a lead matched a profile segment (technical-design §7.6, <c>segmentFit</c>). The strongest
/// available evidence wins; <see cref="WebsiteKeyword"/> cannot occur before chunk C7 has fetched a
/// website.
/// </summary>
public enum SegmentMatch
{
    /// <summary>No segment claimed it: §7.6's "otherwise".</summary>
    None = 0,

    /// <summary>A segment keyword appears in the website text (C7).</summary>
    WebsiteKeyword = 1,

    /// <summary>A segment keyword appears in the company name.</summary>
    NameKeyword = 2,

    /// <summary>The Overture taxonomy matched a segment's <c>overtureCategories</c>.</summary>
    Taxonomy = 3,
}

/// <summary>
/// The <c>facilityFit.level</c> values of <c>schemas/research.schema.json</c>. Technical-design §7.6
/// maps them to 1.0 / 0.75 / 0.5 / 0.4 and prefers them over the website-keyword count.
/// </summary>
public static class FacilityFitLevels
{
    public const string High = "high";
    public const string Medium = "medium";
    public const string Low = "low";
    public const string Unknown = "unknown";

    public static IReadOnlyList<string> All { get; } = [High, Medium, Low, Unknown];

    public static bool IsKnown(string? level) => level is not null && All.Contains(level);
}

/// <summary>
/// The <c>signals[].type</c> values of <c>schemas/research.schema.json</c>, and which of them
/// technical-design §7.6 counts as a <strong>buying</strong> signal.
/// </summary>
public static class SignalTypes
{
    public const string Permit = "permit";
    public const string Hiring = "hiring";
    public const string Expansion = "expansion";
    public const string News = "news";
    public const string Funding = "funding";
    public const string Contract = "contract";
    public const string Registry = "registry";
    public const string Other = "other";

    public static IReadOnlyList<string> All { get; } =
        [Permit, Hiring, Expansion, News, Funding, Contract, Registry, Other];

    /// <summary>
    /// §7.6: "Buying signals (<c>permit</c>, <c>hiring</c>, <c>expansion</c>, <c>news</c>,
    /// <c>funding</c>, <c>contract</c>; not <c>registry</c>/<c>other</c>)". A registry entry says a
    /// company exists, not that it is about to buy anything, which is why the two worked examples with
    /// permits and job posts reach tier A and the one with only an establishment record does not.
    /// </summary>
    public static IReadOnlyList<string> Buying { get; } = [Permit, Hiring, Expansion, News, Funding, Contract];

    public static bool IsBuying(string? type) => type is not null && Buying.Contains(type);
}

/// <summary>
/// One <c>signals</c> row as the scorer needs it: its type and its date.
/// </summary>
/// <param name="Date">
/// <c>YYYY-MM</c> or <c>YYYY-MM-DD</c> exactly as the research document carried it. It stays text all
/// the way through, because <c>YYYY-MM</c> is not a date and turning it into one would invent a day
/// (see the C6 decisions note). The 12-month window is therefore compared at whole-month granularity.
/// </param>
public sealed record SignalFact(string Type, string Date);

/// <summary>
/// The evidence technical-design §7.6 scores, as the <c>FeatureExtractor</c> gathers it from the site,
/// the profile, the saved research, the dealer branch and (from C7) the fetched website.
/// </summary>
/// <remarks>
/// Every member is optional or defaulted to its "no evidence" value, so a lead with nothing known
/// scores the floor rather than failing. §7.6 reads <see cref="ResearchFacilityLevel"/> in preference
/// to <see cref="FacilityKeywordCount"/>; the keyword path cannot produce anything before C7.
/// </remarks>
public sealed record LeadFeatures
{
    /// <summary>How the lead matched a segment (<c>segmentFit</c>).</summary>
    public SegmentMatch SegmentMatch { get; init; } = SegmentMatch.None;

    /// <summary>The matched segment's name, for the <c>list_leads</c> row. Not scored.</summary>
    public string? SegmentName { get; init; }

    /// <summary>Research <c>employeeEstimate.value</c>; null when nothing is known (<c>sizeFit</c> unknown).</summary>
    public int? EmployeeEstimate { get; init; }

    /// <summary>The profile's <c>size.employeesMin</c>; null when the profile sets no minimum.</summary>
    public int? ProfileMinEmployees { get; init; }

    /// <summary>
    /// The matched segment's own <c>minEmployees</c>, which §7.6 prefers over
    /// <see cref="ProfileMinEmployees"/> when the segment sets one. Null when it does not.
    /// </summary>
    /// <remarks>
    /// The sample profile models exactly this: "Facilities &amp; property management" wants 50 employees
    /// where the profile as a whole wants 20. Reading only the profile-wide number would silently ignore
    /// the more specific one, and a 25-person property manager would score <c>sizeFit</c> 1.0 instead of
    /// 0.5.
    /// </remarks>
    public int? SegmentMinEmployees { get; init; }

    /// <summary>Research <c>facilityFit.level</c>, one of <see cref="FacilityFitLevels"/>; null when there is no research.</summary>
    public string? ResearchFacilityLevel { get; init; }

    /// <summary>
    /// Distinct profile facility keywords found in the website text (C7). Null means there is no
    /// website text at all, which §7.6 scores 0.4 rather than 0.2.
    /// </summary>
    public int? FacilityKeywordCount { get; init; }

    /// <summary>The lead's stored signals, buying and otherwise; the scorer filters and dates them.</summary>
    public IReadOnlyList<SignalFact> Signals { get; init; } = [];

    /// <summary>Miles to the assigned branch; null when the lead has no dealer (<c>proximity</c> = 0).</summary>
    public double? BranchDistanceMiles { get; init; }

    /// <summary>Overture's <c>confidence</c> for the site, 0–1.</summary>
    public double OvertureConfidence { get; init; }

    /// <summary>Whether C7 reached the website. False before C7 has run, so <c>confidence</c> is 0.7 × Overture.</summary>
    public bool WebsiteReachable { get; init; }

    /// <summary>Research <c>llmAdjustment</c>; §7.6 clamps it to ±15 before adding it.</summary>
    public int LlmAdjustment { get; init; }
}

/// <summary>
/// One row of <c>score_breakdown_json</c>: a feature, the 0–1 value it scored, the weight applied and
/// the product. §7.6 keeps these "so Claude can explain any score".
/// </summary>
public sealed record ScoreFeatureContribution(string Feature, double Value, double Weight, double Contribution);

/// <summary>Where the employee minimum <c>sizeFit</c> compared against came from (§7.6).</summary>
public static class SizeMinimumSources
{
    /// <summary>The matched segment's own <c>minEmployees</c>, which wins when it is set.</summary>
    public const string Segment = "segment";

    /// <summary>The profile's <c>size.employeesMin</c>.</summary>
    public const string Profile = "profile";

    /// <summary>Neither was set, so <c>sizeFit</c> has nothing to compare against.</summary>
    public const string None = "none";
}

/// <summary>
/// Which employee minimum <c>sizeFit</c> was measured against, and where it came from, recorded in the
/// breakdown because a <c>sizeFit</c> of 0.5 is otherwise unexplainable - 25 employees is half of 50 and
/// well over 20, and nothing in the score says which number was in play.
/// </summary>
public sealed record SizeMinimum(int? Value, string Source);

/// <summary>
/// A computed score with everything needed to explain it: the per-feature contributions, the base
/// before the LLM adjustment, the clamped adjustment, the final score and its tier.
/// </summary>
/// <param name="AsOf">
/// The instant the score was computed, read from the injected <see cref="TimeProvider"/>. It is part of
/// the breakdown because §7.6's <c>signals</c> feature only credits signals inside a twelve-month
/// window, so the same evidence scores differently at different times. Without it a stored score cannot
/// be explained or reproduced later (see the C6 decisions note).
/// </param>
/// <param name="CappedFrom">
/// The score §7.6's tier-A guard took away: the value <c>base + adjustment</c> reached before the lead
/// was capped at <see cref="LeadTiers.ScoreCapWithoutSignals"/> for having no qualifying buying signal.
/// Null when the cap did not bind. It exists so a capped score reads as "79, held back from 90 for want
/// of evidence" rather than as a number that is mysteriously lower than the arithmetic suggests.
/// </param>
public sealed record ScoreBreakdown(
    IReadOnlyList<ScoreFeatureContribution> Features,
    int BaseScore,
    int LlmAdjustment,
    int Score,
    string Tier,
    DateTimeOffset AsOf,
    SizeMinimum SizeMinimum,
    int? CappedFrom);

/// <summary>
/// §7.6's tier thresholds and the guard that keeps tier A out of reach without cited evidence: every
/// feature maxed with no buying signals sums to exactly 0.75, so <strong>75</strong> is the base ceiling,
/// and <see cref="ScoreCapWithoutSignals"/> stops a positive <c>llmAdjustment</c> carrying such a lead
/// past it.
/// </summary>
public static class LeadTiers
{
    public const string A = "A";
    public const string B = "B";
    public const string C = "C";

    public const int MinimumA = 80;
    public const int MinimumB = 65;

    /// <summary>The ceiling of <c>round(100 × Σ wᵢ·fᵢ)</c> when no buying signal is in the window.</summary>
    public const int BaseCeilingWithoutSignals = 75;

    /// <summary>
    /// The final score a lead with <strong>zero qualifying buying signals</strong> cannot pass, whatever
    /// its <c>llmAdjustment</c>: the top of tier B.
    /// </summary>
    /// <remarks>
    /// Without it the guard is only arithmetic, and the one input a person can nudge defeats it: a base
    /// of 75 plus an adjustment of +5 reaches 80 and +15 reaches 90, which is tier A on no cited evidence
    /// at all - through exactly the subjective mechanism the guard exists to contain. Negative
    /// adjustments always apply in full; the cap only ever lowers a score.
    /// </remarks>
    public const int ScoreCapWithoutSignals = 79;

    public static IReadOnlyList<string> All { get; } = [A, B, C];

    public static bool IsKnown(string? tier) => tier is not null && All.Contains(tier);

    /// <summary>The tier for a final score: A ≥ 80, B ≥ 65, C below.</summary>
    public static string For(int score) =>
        score >= MinimumA ? A
        : score >= MinimumB ? B
        : C;
}

/// <summary>
/// The six weights of technical-design §7.6. They must sum to 1.0 ± 0.001, whether they arrive in a
/// search profile (<c>scoringWeights</c>, enforced since C1) or directly on <c>score_leads</c>.
/// </summary>
public sealed record ScoringWeights(
    double SegmentFit,
    double SizeFit,
    double FacilityFit,
    double Signals,
    double Proximity,
    double Confidence)
{
    /// <summary>The tolerance C1 established for the sum rule.</summary>
    public const double Tolerance = 0.001;

    /// <summary>
    /// The smallest and largest a single weight may be, inclusive, which is what
    /// <c>search-profile.schema.json</c> already says with <c>minimum</c>/<c>maximum</c>. A weight is a
    /// share of the score: <c>0</c> switches a feature off and <c>1</c> gives it the whole score, so
    /// anything outside that is not a share of anything.
    /// </summary>
    public const double MinWeight = 0d;

    /// <inheritdoc cref="MinWeight"/>
    public const double MaxWeight = 1d;

    /// <summary>§7.6's "Default weight" column.</summary>
    public static ScoringWeights Default { get; } = new(0.25, 0.15, 0.20, 0.25, 0.05, 0.10);

    public double Sum => SegmentFit + SizeFit + FacilityFit + Signals + Proximity + Confidence;

    /// <summary>The weight of one <see cref="ScoreFeatures"/> name.</summary>
    public double For(string feature) => feature switch
    {
        ScoreFeatures.SegmentFit => SegmentFit,
        ScoreFeatures.SizeFit => SizeFit,
        ScoreFeatures.FacilityFit => FacilityFit,
        ScoreFeatures.Signals => Signals,
        ScoreFeatures.Proximity => Proximity,
        ScoreFeatures.Confidence => Confidence,
        _ => throw new ArgumentOutOfRangeException(
            nameof(feature),
            feature,
            $"'{feature}' is not one of technical-design §7.6's features: {string.Join(", ", ScoreFeatures.All)}."),
    };

    /// <summary>
    /// The weights of a saved search profile, or <see cref="Default"/> when it names none.
    /// </summary>
    public static ScoringWeights FromProfile(System.Text.Json.JsonElement profile) =>
        profile.ValueKind == System.Text.Json.JsonValueKind.Object
        && profile.TryGetProperty("scoringWeights", out var weights)
            ? FromJson(weights)
            : Default;

    /// <summary>
    /// The weights a caller passed, or <see cref="Default"/> when <paramref name="weights"/> is absent.
    /// </summary>
    /// <remarks>
    /// A member the object leaves out is <strong>zero</strong>, not its default: the sum rule accepts
    /// <c>{"signals": 1.0}</c>, which can only mean every other feature is worth nothing. Mixing the
    /// defaults in would make that set sum to 1.75 and score every lead high.
    /// </remarks>
    public static ScoringWeights FromJson(System.Text.Json.JsonElement? weights)
    {
        if (weights is not { ValueKind: System.Text.Json.JsonValueKind.Object } given)
        {
            return Default;
        }

        return new ScoringWeights(
            Weight(given, ScoreFeatures.SegmentFit),
            Weight(given, ScoreFeatures.SizeFit),
            Weight(given, ScoreFeatures.FacilityFit),
            Weight(given, ScoreFeatures.Signals),
            Weight(given, ScoreFeatures.Proximity),
            Weight(given, ScoreFeatures.Confidence));
    }

    /// <summary>
    /// The weights as the <c>{feature: weight}</c> object <c>score_leads</c> echoes back and the campaign
    /// stores in <c>scoring_weights_json</c>. One shape for both, so the scale a caller is shown is
    /// exactly the one <c>save_research</c> later reads, and <see cref="FromJson"/> round-trips it.
    /// </summary>
    public IReadOnlyDictionary<string, double> ByFeature() =>
        ScoreFeatures.All.ToDictionary(feature => feature, For, StringComparer.Ordinal);

    private static double Weight(System.Text.Json.JsonElement weights, string feature) =>
        weights.TryGetProperty(feature, out var value)
        && value.ValueKind == System.Text.Json.JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? number
            : 0d;

    /// <summary>
    /// The rules a set of weights has to satisfy - every key one of §7.6's six features, every value a
    /// number, and the six summing to 1.0 - reported at <paramref name="pointer"/> so the caller is told
    /// where the offending object is: <c>/scoringWeights</c> for a profile, <c>/weights</c> for
    /// <c>score_leads</c>. An offending <strong>key</strong> is reported at its own pointer inside that
    /// object, because "the weights are not valid" leaves the caller comparing six names by eye.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the single home for the rule C1 added to <c>save_search_profile</c>; C1's own
    /// <c>ScoringWeightsRule</c> delegates here rather than keeping a second copy, or the two
    /// paths can drift and one of them will quietly accept weights that score everything at 90 %.
    /// </para>
    /// <para>
    /// <strong>The known-keys check is not redundant with the sum rule - it is the only thing that can
    /// catch a typo.</strong> <c>{"signals": 0.5, "signalz": 0.5}</c> sums to exactly 1.0, so the sum
    /// rule passes it; <see cref="FromJson"/> then resolves the misspelling to nothing, <c>signals</c>
    /// keeps a weight of 0.5 instead of 1.0, and every lead in the campaign is scored on a scale nobody
    /// asked for. A profile is protected by <c>search-profile.schema.json</c>, which sets
    /// <c>additionalProperties: false</c>; <c>score_leads</c>' <c>weights</c> argument has no schema at
    /// all, so this check is all that stands in front of it. A <strong>partial</strong> object of known
    /// keys stays legal: §7.6's features are the vocabulary, not a required set, and
    /// <see cref="FromJson"/> reads an absent one as 0.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SearchProfiles.ValidationProblem> Validate(
        System.Text.Json.JsonElement weights,
        string pointer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pointer);

        if (weights.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            // Anything else is the schema's business (for a profile) or the tool's (for score_leads);
            // a sum rule has nothing to say about a value that is not a set of weights.
            return [];
        }

        var problems = new List<SearchProfiles.ValidationProblem>();
        var sum = 0d;

        foreach (var weight in weights.EnumerateObject())
        {
            if (!ScoreFeatures.All.Contains(weight.Name, StringComparer.Ordinal))
            {
                problems.Add(new SearchProfiles.ValidationProblem(
                    SearchProfiles.JsonPointers.Append(pointer, weight.Name),
                    $"'{weight.Name}' is not one of the scoring features: {string.Join(", ", ScoreFeatures.All)}."));
                continue;
            }

            if (weight.Value.ValueKind != System.Text.Json.JsonValueKind.Number
                || !weight.Value.TryGetDouble(out var value))
            {
                problems.Add(new SearchProfiles.ValidationProblem(
                    SearchProfiles.JsonPointers.Append(pointer, weight.Name),
                    $"'{weight.Name}' must be a number between {Format(MinWeight)} and {Format(MaxWeight)}."));
                continue;
            }

            // A weight is a share of the score, so 2.0 and −1.0 are not weights. The sum rule cannot catch
            // either, because any set holding a weight above 1 needs a negative one to reach 1.0 - the two
            // mistakes hide each other.
            if (value < MinWeight || value > MaxWeight)
            {
                problems.Add(new SearchProfiles.ValidationProblem(
                    SearchProfiles.JsonPointers.Append(pointer, weight.Name),
                    $"'{weight.Name}' is {Format(value)}; a weight is a share of the score, so it has to be "
                    + $"between {Format(MinWeight)} and {Format(MaxWeight)}."));
                continue;
            }

            sum += value;
        }

        // The sum is only worth reporting once every key is a feature with a number behind it: the weight
        // behind a mistyped key is weight the caller believes they assigned, so a total computed from it
        // would confirm a scale that was never applied.
        if (problems.Count > 0)
        {
            return problems;
        }

        return Math.Abs(sum - 1d) <= Tolerance
            ? []
            : [new SearchProfiles.ValidationProblem(
                pointer,
                $"The scoring weights must sum to 1.0 (±{Format(Tolerance)}); these sum to {Format(sum)}.")];
    }

    private static string Format(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Technical-design §7.6's deterministic scorer:
/// <c>score = clamp(round(100 × Σ wᵢ·fᵢ) + clamp(llmAdjustment, −15, +15), 0, 100)</c>.
/// </summary>
/// <param name="clock">
/// The reference instant for §7.6's 12-month signal window. It is injected rather than read from
/// <c>DateTimeOffset.UtcNow</c> so a scoring test cannot change its answer as the calendar moves: the
/// worked examples' permit is dated 2026-07, and a suite pinned to wall time would pass today and fail
/// next summer with no code change.
/// </param>
public sealed class LeadScorer(ScoringWeights weights, TimeProvider clock)
{
    /// <summary>
    /// How many whole calendar months back §7.6's window reaches, counting the current month as 0. A
    /// signal counts when <c>(now.Year*12 + now.Month) − (date.Year*12 + date.Month)</c> is 0 through
    /// 11 inclusive.
    /// </summary>
    /// <remarks>
    /// Whole months, not days: <c>signals[].date</c> admits <c>YYYY-MM</c>, and a day-precise window
    /// would have to invent a day for it - reading "2025-10" as the 1st puts it outside a 12-month
    /// window that the 31st falls inside, for an event that genuinely happened somewhere in that month.
    /// A <strong>future</strong> date (a negative difference) does not count: it is far more often a typo
    /// or a mis-transcribed citation than a genuine filed-for-later project, and crediting it would let a
    /// bad date inflate a score.
    /// </remarks>
    public const int WindowMonths = 12;

    /// <summary>
    /// §7.6's midpoint rule. Stated because .NET's <c>Math.Round</c> default is banker's rounding, which
    /// would send 79.5 to 80 and 80.5 to 80 - the same midpoint landing on either side of the tier A
    /// threshold depending on the digit before it.
    /// </summary>
    public const MidpointRounding Rounding = MidpointRounding.AwayFromZero;

    /// <summary>
    /// §7.6's <c>clamp(llmAdjustment, −15, +15)</c>. <c>schemas/research.schema.json</c> caps the field
    /// too; this is the second line of defence for a value that reached the scorer by another route.
    /// </summary>
    public const int MaxAdjustment = 15;

    /// <summary>
    /// How many decimal places the <strong>stored</strong> feature values and contributions keep (§7.6).
    /// Four is enough to explain a 0-100 score to a reader and short enough that
    /// <c>0.08000000000000002</c> never reaches one. The score is computed from full precision, so this
    /// rounding is cosmetic by construction - applying it before the weighted sum would change scores.
    /// </summary>
    public const int StoredDecimals = 4;

    /// <summary>The weights in force, which <c>score_leads</c> echoes back.</summary>
    public ScoringWeights Weights { get; } = weights;

    /// <summary>The score for one lead's features, with the breakdown that explains it.</summary>
    public ScoreBreakdown Score(LeadFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);

        var asOf = clock.GetUtcNow();
        var minimum = ApplicableMinimum(features);
        var qualifying = QualifyingSignals(features.Signals, asOf);

        var values = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [ScoreFeatures.SegmentFit] = SegmentFit(features.SegmentMatch),
            [ScoreFeatures.SizeFit] = SizeFit(features.EmployeeEstimate, minimum.Value),
            [ScoreFeatures.FacilityFit] = FacilityFit(features.ResearchFacilityLevel, features.FacilityKeywordCount),
            [ScoreFeatures.Signals] = Signals(qualifying),
            [ScoreFeatures.Proximity] = Proximity(features.BranchDistanceMiles),
            [ScoreFeatures.Confidence] = Confidence(features.OvertureConfidence, features.WebsiteReachable),
        };

        // Summed in the order §7.6's table lists them, which is the order ScoreFeatures.All keeps.
        // Floating-point addition is not associative, so a different order can land either side of an
        // exact midpoint - and one documented feature set sums to exactly 64.5, where the two sides are
        // different tiers.
        var contributions = new List<ScoreFeatureContribution>(ScoreFeatures.All.Count);
        var total = 0d;

        foreach (var feature in ScoreFeatures.All)
        {
            var value = values[feature];
            var weight = Weights.For(feature);
            var contribution = value * weight;

            // The score accumulates at full precision; only the stored explanation is rounded. §7.6 is
            // explicit that the two are different: a reader of the breakdown should not meet
            // 0.08000000000000002, and the score must not be computed from a rounded value either.
            total += contribution;

            contributions.Add(new ScoreFeatureContribution(
                feature,
                Stored(value),
                weight,
                Stored(contribution)));
        }

        var baseScore = (int)Math.Round(100 * total, Rounding);
        var adjustment = Math.Clamp(features.LlmAdjustment, -MaxAdjustment, MaxAdjustment);
        var adjusted = Math.Clamp(baseScore + adjustment, 0, 100);

        // §7.6's tier-A guard: min(score, 79), which only ever lowers a score. Reading it as "set 79
        // when there are no signals" would raise a lead that scored 71 on evidence of other kinds.
        var score = qualifying == 0 ? Math.Min(adjusted, LeadTiers.ScoreCapWithoutSignals) : adjusted;

        return new ScoreBreakdown(
            contributions,
            baseScore,
            adjustment,
            score,
            LeadTiers.For(score),
            asOf,
            minimum,
            score < adjusted ? adjusted : null);
    }

    /// <summary>
    /// One number of the stored breakdown, rounded to <see cref="StoredDecimals"/> places. Never applied
    /// before the weighted sum: the rounding exists so <c>score_breakdown_json</c> explains a score to a
    /// person, not to change the score.
    /// </summary>
    private static double Stored(double value) => Math.Round(value, StoredDecimals, Rounding);

    /// <summary>§7.6: "Taxonomy match … = 1.0; name keyword = 0.8; website keyword = 0.6; otherwise 0.2".</summary>
    private static double SegmentFit(SegmentMatch match) => match switch
    {
        SegmentMatch.Taxonomy => 1.0,
        SegmentMatch.NameKeyword => 0.8,
        SegmentMatch.WebsiteKeyword => 0.6,
        _ => 0.2,
    };

    /// <summary>
    /// §7.6: "≥ min = 1.0; ≥ 50 % of min = 0.5; below = 0.1; unknown = 0.5". With no minimum to compare
    /// against there is no "below" either, so that is unknown too.
    /// </summary>
    private static double SizeFit(int? employees, int? minimum) =>
        employees is not { } value || minimum is not { } floor ? 0.5
        : value >= floor ? 1.0
        : value >= floor / 2d ? 0.5
        : 0.1;

    /// <summary>
    /// §7.6: the research <c>facilityFit.level</c> wins when there is one; otherwise the website keyword
    /// count, where <strong>no website text at all</strong> (null) is 0.4 rather than the 0.2 that
    /// "looked, found none" scores. Nothing fills the keyword count before chunk C7.
    /// </summary>
    private static double FacilityFit(string? researchLevel, int? keywords) =>
        researchLevel switch
        {
            FacilityFitLevels.High => 1.0,
            FacilityFitLevels.Medium => 0.75,
            FacilityFitLevels.Low => 0.5,
            FacilityFitLevels.Unknown => 0.4,
            _ => keywords switch
            {
                null => 0.4,
                0 => 0.2,
                1 => 0.5,
                2 => 0.75,
                _ => 1.0,
            },
        };

    /// <summary>§7.6: "0 = 0, 1 = 0.6, ≥ 2 = 1.0".</summary>
    private static double Signals(int qualifying) => qualifying switch
    {
        0 => 0d,
        1 => 0.6,
        _ => 1.0,
    };

    /// <summary>§7.6: "≤ 25 mi = 1.0, ≤ 50 mi = 0.6, farther = 0.3, no dealer = 0".</summary>
    private static double Proximity(double? miles) =>
        miles is not { } distance ? 0d
        : distance <= 25 ? 1.0
        : distance <= 50 ? 0.6
        : 0.3;

    /// <summary>§7.6: "0.7 × Overture confidence + 0.3 × (website reachable)".</summary>
    private static double Confidence(double overture, bool websiteReachable) =>
        (0.7 * overture) + (websiteReachable ? 0.3 : 0d);

    /// <summary>
    /// §7.6's applicable minimum: the matched segment's <c>minEmployees</c> when it sets one, otherwise
    /// the profile's <c>size.employeesMin</c>. Recorded in the breakdown because a <c>sizeFit</c> of 0.5
    /// means different things against 20 and against 50.
    /// </summary>
    private static SizeMinimum ApplicableMinimum(LeadFeatures features) =>
        features.SegmentMinEmployees is { } segment ? new SizeMinimum(segment, SizeMinimumSources.Segment)
        : features.ProfileMinEmployees is { } profile ? new SizeMinimum(profile, SizeMinimumSources.Profile)
        : new SizeMinimum(null, SizeMinimumSources.None);

    /// <summary>
    /// Whether §7.6 credits this signal as of <paramref name="asOf"/>: a <strong>buying</strong> type,
    /// dated in one of the twelve calendar months ending with that month.
    /// </summary>
    /// <remarks>
    /// Public because <c>list_leads</c>' <c>topSignal</c> has to name a signal that actually scored, and a
    /// second date rule written next to this one is how the headline and the score drift apart - which is
    /// exactly the bug that made this method public: a permit mistyped into next month was advertised as
    /// the reason for a score computed from a different permit six months old.
    /// </remarks>
    public static bool Qualifies(SignalFact signal, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return SignalTypes.IsBuying(signal.Type) && IsInWindow(signal.Date, asOf);
    }

    /// <summary>
    /// The buying signals inside §7.6's window: a type the design credits, dated in one of the twelve
    /// calendar months ending with <paramref name="asOf"/>'s own month.
    /// </summary>
    private static int QualifyingSignals(IReadOnlyList<SignalFact> signals, DateTimeOffset asOf) =>
        signals.Count(signal => Qualifies(signal, asOf));

    /// <summary>
    /// Whether <paramref name="date"/> - <c>YYYY-MM</c> or <c>YYYY-MM-DD</c> - is 0 to 11 whole months
    /// before <paramref name="asOf"/>. A future date (a negative difference) is not credited, and a date
    /// the schema would not have allowed counts for nothing rather than throwing.
    /// </summary>
    private static bool IsInWindow(string? date, DateTimeOffset asOf)
    {
        if (!TryReadYearMonth(date, out var year, out var month))
        {
            return false;
        }

        var now = asOf.UtcDateTime;
        var months = ((now.Year * 12) + now.Month) - ((year * 12) + month);

        return months >= 0 && months < WindowMonths;
    }

    /// <summary>
    /// The year and month of a <c>signals.date</c>. The day is deliberately ignored: the column stays
    /// text because the schema admits a month without one, and inventing a day would make the same event
    /// score differently at two precisions.
    /// </summary>
    private static bool TryReadYearMonth(string? date, out int year, out int month)
    {
        year = 0;
        month = 0;

        if (date is null || date.Length < 7 || date[4] != '-')
        {
            return false;
        }

        return int.TryParse(date.AsSpan(0, 4), System.Globalization.CultureInfo.InvariantCulture, out year)
            && int.TryParse(date.AsSpan(5, 2), System.Globalization.CultureInfo.InvariantCulture, out month)
            && month is >= 1 and <= 12;
    }
}
