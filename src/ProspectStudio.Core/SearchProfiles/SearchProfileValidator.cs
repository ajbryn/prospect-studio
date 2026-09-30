using System.Globalization;
using System.Text.Json;
using Json.Schema;

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

    private const int MaxProblems = 25;

    /// <summary>
    /// <c>IncludeApplicatorErrors = false</c> keeps the generic ancestor messages ("some properties
    /// did not match") out of the results, so every problem points at the value that is actually
    /// wrong.
    /// </summary>
    private static readonly EvaluationOptions Evaluation = new()
    {
        OutputFormat = OutputFormat.Hierarchical,
        IncludeApplicatorErrors = false,
    };

    /// <summary>
    /// Built once for the process. <see cref="JsonSchema.FromText"/> registers the schema under its
    /// <c>$id</c> in a global registry and refuses to register the same <c>$id</c> twice, so building
    /// it per instance turns a second validator - a scoped registration, a job, a second window in the
    /// desktop app - into an exception. The built schema is immutable and evaluates concurrently.
    /// </summary>
    private static readonly JsonSchema Schema = JsonSchema.FromText(ReadEmbeddedSchema());

    public IReadOnlyList<ValidationProblem> Validate(JsonElement profile)
    {
        if (profile.ValueKind != JsonValueKind.Object)
        {
            return [new ValidationProblem(string.Empty, "The profile must be a JSON object.")];
        }

        var results = Schema.Evaluate(profile, Evaluation);
        if (!results.IsValid)
        {
            var problems = new List<ValidationProblem>();
            var alternatives = new List<AlternativeGroup>();
            var satisfied = Satisfied(results);
            Collect(results, profile, problems, satisfied, alternatives);

            // Resolving a group can walk into a branch that holds an anyOf of its own, which appends to
            // this list, so it is indexed rather than enumerated.
            for (var index = 0; index < alternatives.Count; index++)
            {
                alternatives[index].Resolve(profile, problems, satisfied, alternatives);
            }

            return Report(problems);
        }

        return ScoringWeightsRule.Check(profile);
    }

    /// <summary>
    /// Deduplicates, and says so when there are more problems than one response should carry, so the
    /// count in the error's hint is never a truncated number presented as the whole story.
    /// </summary>
    private static IReadOnlyList<ValidationProblem> Report(List<ValidationProblem> problems)
    {
        var distinct = problems.DistinctBy(problem => (problem.Pointer, problem.Message)).ToList();

        if (distinct.Count == 0)
        {
            return [new ValidationProblem(string.Empty, "The profile does not match the search-profile schema.")];
        }

        if (distinct.Count <= MaxProblems)
        {
            return distinct;
        }

        var listed = distinct.Take(MaxProblems - 1).ToList();
        listed.Add(new ValidationProblem(
            string.Empty,
            $"{distinct.Count - listed.Count} further problem(s) are not listed; fix these and validate again."));
        return listed;
    }

    /// <summary>
    /// Walks the failing branch of the evaluation tree, collecting the problems Claude can act on.
    /// Two kinds of noise are filtered out. It descends only through invalid nodes, and it treats the
    /// branches of an <c>anyOf</c>/<c>oneOf</c> as one group: the library reports every failed branch,
    /// even of a keyword that <em>passed</em> because another branch matched, and reporting them
    /// one by one would read as "both of these are required" when only one of them is.
    /// </summary>
    private static void Collect(
        EvaluationResults results,
        JsonElement profile,
        List<ValidationProblem> problems,
        HashSet<(string Instance, string Applicator)> satisfied,
        List<AlternativeGroup> alternatives)
    {
        if (results.IsValid)
        {
            return;
        }

        var pointer = results.InstanceLocation.ToString();
        var evaluationPath = results.EvaluationPath.ToString();

        if (TryAlternativeBranch(evaluationPath, out var applicator))
        {
            if (satisfied.Contains((pointer, applicator)))
            {
                return;
            }

            var group = alternatives.Find(candidate => candidate.Matches(pointer, applicator));
            if (group is null)
            {
                group = new AlternativeGroup(pointer, applicator);
                alternatives.Add(group);
            }

            group.Add(results);
            return;
        }

        CollectFrom(results, profile, problems, satisfied, alternatives);
    }

    /// <summary>
    /// The problems a node itself reports, plus those of everything failing below it. Split out from
    /// <see cref="Collect"/> so a branch of an <c>anyOf</c> can be walked on its own once the group it
    /// belongs to has decided that its branches have something specific to say.
    /// </summary>
    private static void CollectFrom(
        EvaluationResults results,
        JsonElement profile,
        List<ValidationProblem> problems,
        HashSet<(string Instance, string Applicator)> satisfied,
        List<AlternativeGroup> alternatives)
    {
        var pointer = results.InstanceLocation.ToString();

        if (results.Errors is not null)
        {
            foreach (var (keyword, message) in results.Errors)
            {
                problems.AddRange(Describe(keyword, message, pointer, results.EvaluationPath.ToString(), profile));
            }
        }

        foreach (var nested in results.Details ?? [])
        {
            Collect(nested, profile, problems, satisfied, alternatives);
        }
    }

    /// <summary>
    /// Every <c>anyOf</c>/<c>oneOf</c> that one of its branches did satisfy. A matching branch can be
    /// parented anywhere in the tree, so the whole tree is scanned, valid nodes included, before any
    /// problem is reported.
    /// </summary>
    private static HashSet<(string Instance, string Applicator)> Satisfied(EvaluationResults results)
    {
        var satisfied = new HashSet<(string, string)>();
        Scan(results);
        return satisfied;

        void Scan(EvaluationResults node)
        {
            if (node.IsValid && TryAlternativeBranch(node.EvaluationPath.ToString(), out var applicator))
            {
                satisfied.Add((node.InstanceLocation.ToString(), applicator));
            }

            foreach (var nested in node.Details ?? [])
            {
                Scan(nested);
            }
        }
    }

    /// <summary>
    /// True when the evaluation path is one branch of an <c>anyOf</c>/<c>oneOf</c>, for example
    /// <c>/properties/geography/anyOf/1</c>; <paramref name="applicator"/> is the keyword the branch
    /// belongs to, which identifies its siblings.
    /// </summary>
    private static bool TryAlternativeBranch(string evaluationPath, out string applicator)
    {
        applicator = string.Empty;

        var lastSeparator = evaluationPath.LastIndexOf('/');
        if (lastSeparator <= 0 || !int.TryParse(evaluationPath.AsSpan(lastSeparator + 1), out _))
        {
            return false;
        }

        var head = evaluationPath[..lastSeparator];
        if (!head.EndsWith("/anyOf", StringComparison.Ordinal) && !head.EndsWith("/oneOf", StringComparison.Ordinal))
        {
            return false;
        }

        applicator = head;
        return true;
    }

    private static IEnumerable<ValidationProblem> Describe(
        string keyword,
        string message,
        string pointer,
        string evaluationPath,
        JsonElement profile)
    {
        if (string.Equals(keyword, "required", StringComparison.Ordinal))
        {
            // JSON Schema reports a missing member against the object that should hold it, so the
            // pointer of the member itself is synthesized here (mcp-tools.md §save_search_profile).
            var missing = MissingProperties(message, pointer, profile);
            if (missing.Count > 0)
            {
                return missing.Select(name => new ValidationProblem(
                    JsonPointers.Append(pointer, name),
                    $"'{name}' is required."));
            }
        }

        if (evaluationPath.EndsWith("/additionalProperties", StringComparison.Ordinal))
        {
            var name = pointer[(pointer.LastIndexOf('/') + 1)..];
            return [new ValidationProblem(pointer, $"'{name}' is not a property the search profile allows.")];
        }

        if (string.Equals(keyword, "pattern", StringComparison.Ordinal)
            && JsonPointers.TryResolve(profile, pointer, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return [new ValidationProblem(pointer, $"'{value.GetString()}' does not match the pattern this field requires.")];
        }

        return [new ValidationProblem(pointer, Sentence(message))];
    }

    /// <summary>
    /// The members named in a <c>required</c> message that the submitted document really is missing.
    /// The message carries them as a JSON array, for example
    /// <c>Required properties ["segments"] are not present</c>.
    /// </summary>
    private static List<string> MissingProperties(string message, string pointer, JsonElement profile)
    {
        var start = message.IndexOf('[', StringComparison.Ordinal);
        var end = message.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return [];
        }

        string[]? names;
        try
        {
            names = JsonSerializer.Deserialize<string[]>(message[start..(end + 1)]);
        }
        catch (JsonException)
        {
            return [];
        }

        if (names is null)
        {
            return [];
        }

        var present = JsonPointers.TryResolve(profile, pointer, out var instance) && instance.ValueKind == JsonValueKind.Object
            ? instance
            : default;

        return [.. names.Where(name => present.ValueKind != JsonValueKind.Object || !present.TryGetProperty(name, out _))];
    }

    private static string Sentence(string message) =>
        message.Length > 0 && !message.EndsWith('.') ? message + "." : message;

    private static string ReadEmbeddedSchema()
    {
        using var stream = typeof(SearchProfileValidator).Assembly.GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException($"The embedded schema '{SchemaResourceName}' is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The failed branches of one <c>anyOf</c>/<c>oneOf</c>.
    /// </summary>
    /// <remarks>
    /// When every branch failed purely because a member was missing, that is the "either this or that"
    /// case and it collapses to one problem at the value itself: a problem per branch would read as
    /// "both are required". When a branch failed for any other reason it has something a caller can act
    /// on - "this array needs at least one item" - and that beats a summary naming no field, so the
    /// branches are then reported at their own pointers.
    /// </remarks>
    private sealed class AlternativeGroup(string pointer, string applicator)
    {
        private readonly List<EvaluationResults> _branches = [];

        public bool Matches(string candidatePointer, string candidateApplicator) =>
            string.Equals(pointer, candidatePointer, StringComparison.Ordinal)
            && string.Equals(applicator, candidateApplicator, StringComparison.Ordinal);

        public void Add(EvaluationResults branch) => _branches.Add(branch);

        public void Resolve(
            JsonElement profile,
            List<ValidationProblem> problems,
            HashSet<(string Instance, string Applicator)> satisfied,
            List<AlternativeGroup> alternatives)
        {
            var missing = new List<string>();
            foreach (var branch in _branches)
            {
                if (!MissingMembersOnly(branch, profile, out var names))
                {
                    foreach (var reported in _branches)
                    {
                        CollectFrom(reported, profile, problems, satisfied, alternatives);
                    }

                    return;
                }

                missing.AddRange(names.Where(name => !missing.Contains(name, StringComparer.Ordinal)));
            }

            if (missing.Count == 0)
            {
                problems.Add(new ValidationProblem(pointer, "This value matches none of the shapes the schema allows."));
                return;
            }

            problems.Add(new ValidationProblem(pointer, missing.Count switch
            {
                1 => $"'{missing[0]}' is required.",
                2 => $"Either '{missing[0]}' or '{missing[1]}' is required.",
                _ => $"One of {string.Join(", ", missing.Take(missing.Count - 1).Select(name => $"'{name}'"))} or '{missing[^1]}' is required.",
            }));
        }

        /// <summary>
        /// True when a branch failed for one reason only: members the submitted value does not have.
        /// Anything below the branch, or any other keyword on it, means it has something specific to say.
        /// </summary>
        private bool MissingMembersOnly(EvaluationResults branch, JsonElement profile, out List<string> missing)
        {
            missing = [];

            if (branch.Details is not null && branch.Details.Exists(nested => !nested.IsValid))
            {
                return false;
            }

            if (branch.Errors is null || branch.Errors.Count == 0)
            {
                return false;
            }

            foreach (var (keyword, message) in branch.Errors)
            {
                if (!string.Equals(keyword, "required", StringComparison.Ordinal))
                {
                    return false;
                }

                missing.AddRange(MissingProperties(message, pointer, profile));
            }

            return missing.Count > 0;
        }
    }
}

/// <summary>
/// The one search-profile rule JSON Schema cannot express: the six scoring weights must sum to 1.0
/// (implementation-plan C1), so a profile cannot quietly score everything at 90 %.
/// </summary>
internal static class ScoringWeightsRule
{
    public const string Pointer = "/scoringWeights";
    public const double Tolerance = 0.001;

    public static IReadOnlyList<ValidationProblem> Check(JsonElement profile)
    {
        if (!profile.TryGetProperty("scoringWeights", out var weights) || weights.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var sum = 0d;
        foreach (var weight in weights.EnumerateObject())
        {
            if (weight.Value.ValueKind != JsonValueKind.Number || !weight.Value.TryGetDouble(out var value))
            {
                return [];
            }

            sum += value;
        }

        return Math.Abs(sum - 1d) <= Tolerance
            ? []
            : [new ValidationProblem(
                Pointer,
                $"The scoring weights must sum to 1.0 (±{Tolerance.ToString("0.###", CultureInfo.InvariantCulture)}); "
                + $"these sum to {sum.ToString("0.###", CultureInfo.InvariantCulture)}.")];
    }
}
