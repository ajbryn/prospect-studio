using System.Text.Json;

namespace ProspectStudio.Core.SearchProfiles;

/// <summary>One problem with a submitted document: where it is and what is wrong with it.</summary>
public sealed record ValidationProblem(string Pointer, string Message);

/// <summary>
/// Validates a search profile against <c>schemas/search-profile.schema.json</c> (embedded in this
/// assembly) and applies the extra <c>scoringWeights</c> sum rule from implementation-plan C1, which
/// JSON Schema cannot express.
/// </summary>
public sealed class SearchProfileValidator
{
    public const string SchemaResourceName = "ProspectStudio.Core.Schemas.search-profile.schema.json";

    /// <summary>
    /// Built once for the process. <see cref="EmbeddedSchema.Load"/> registers the schema under its
    /// <c>$id</c> in a global registry that refuses the same <c>$id</c> twice, so building it per
    /// instance turns a second validator - a scoped registration, a job, a second window in the desktop
    /// app - into an exception. The built schema is immutable and evaluates concurrently.
    /// </summary>
    private static readonly SchemaProblems Schema =
        new(EmbeddedSchema.Load(SchemaResourceName), "search profile");

    public IReadOnlyList<ValidationProblem> Validate(JsonElement profile)
    {
        var problems = Schema.Validate(profile);

        // The sum rule is only worth reporting once the document is otherwise a profile: a half-written
        // scoringWeights object in a profile that fails the schema anyway is noise.
        return problems.Count > 0 ? problems : ScoringWeightsRule.Check(profile);
    }
}

/// <summary>
/// The one search-profile rule JSON Schema cannot express: the six scoring weights must sum to 1.0
/// (implementation-plan C1), so a profile cannot quietly score everything at 90 %.
/// </summary>
internal static class ScoringWeightsRule
{
    public const string Pointer = "/scoringWeights";

    /// <summary>
    /// Delegates to <see cref="Leads.ScoringWeights.Validate"/>, which is the single home for the rule:
    /// <c>score_leads</c> accepts weights directly, so a second copy here would let one of the two paths
    /// drift and quietly accept a set that scores everything a tenth low.
    /// </summary>
    public static IReadOnlyList<ValidationProblem> Check(JsonElement profile) =>
        profile.ValueKind == JsonValueKind.Object && profile.TryGetProperty("scoringWeights", out var weights)
            ? Leads.ScoringWeights.Validate(weights, Pointer)
            : [];
}
