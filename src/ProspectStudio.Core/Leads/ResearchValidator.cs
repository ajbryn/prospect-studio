using System.Text.Json;
using ProspectStudio.Core.SearchProfiles;

namespace ProspectStudio.Core.Leads;

/// <summary>
/// Validates a research document against <c>schemas/research.schema.json</c> (embedded in this
/// assembly, the way <see cref="SearchProfileValidator"/> embeds its own), so <c>save_research</c>
/// works from a published server with no spec pack next to it.
/// </summary>
/// <remarks>
/// The schema already carries every rule mcp-tools.md §save_research lists, including the coupling of
/// <c>status: "no_signal"</c> to an empty <c>signals</c> array - its <c>allOf/if/then</c> applies
/// <c>maxItems: 0</c> to <c>signals</c> when <c>status</c> is <c>no_signal</c>. Unlike C1's
/// <c>scoringWeights</c> sum, there is no extra code-level rule to add here.
/// </remarks>
public sealed class ResearchValidator
{
    public const string SchemaResourceName = "ProspectStudio.Core.Schemas.research.schema.json";

    /// <summary>
    /// Built once for the process, for the reason <see cref="SearchProfileValidator"/> gives: the schema
    /// registry refuses the same <c>$id</c> twice, so a per-instance build would make a second validator
    /// throw.
    /// </summary>
    private static readonly SchemaProblems Schema =
        new(EmbeddedSchema.Load(SchemaResourceName), "research document");

    /// <summary>
    /// The problems with <paramref name="research"/>, each at a JSON pointer into the submitted
    /// document, or an empty list when it is valid. <c>save_research</c> turns a non-empty list into
    /// <c>VALIDATION_FAILED</c> with <c>details[]</c>.
    /// </summary>
    public IReadOnlyList<ValidationProblem> Validate(JsonElement research) => Schema.Validate(research);
}
