using System.Text.Json;
using Json.Schema;

namespace ProspectStudio.Core.SearchProfiles;

/// <summary>
/// Turns a failed JSON Schema evaluation into the problems a caller can act on, each at a JSON pointer
/// into the document they submitted (mcp-tools.md §save_search_profile, §save_research).
/// </summary>
/// <param name="noun">
/// What the document is, for the few messages that name it ("search profile", "research document").
/// </param>
/// <remarks>
/// Shared by <see cref="SearchProfileValidator"/> and <see cref="Leads.ResearchValidator"/> rather than
/// written twice: the awkward parts - synthesizing the pointer of a <em>missing</em> member, and
/// collapsing the branches of an <c>anyOf</c> so they do not read as "all of these are required" - are
/// exactly the parts a second copy would get subtly differently.
/// </remarks>
internal sealed class SchemaProblems(JsonSchema schema, string noun)
{
    /// <summary>
    /// Problems at most in one response, so a document that is broken throughout cannot push a tool
    /// error past the ~4,000-token budget (CLAUDE.md).
    /// </summary>
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

    /// <summary>The problems with <paramref name="document"/>, or an empty list when it is valid.</summary>
    public IReadOnlyList<ValidationProblem> Validate(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object)
        {
            return [new ValidationProblem(string.Empty, $"The {noun} must be a JSON object.")];
        }

        var results = schema.Evaluate(document, Evaluation);
        if (results.IsValid)
        {
            return [];
        }

        var problems = new List<ValidationProblem>();
        var alternatives = new List<AlternativeGroup>();
        var satisfied = Satisfied(results);
        Collect(results, document, problems, satisfied, alternatives);

        // Resolving a group can walk into a branch that holds an anyOf of its own, which appends to
        // this list, so it is indexed rather than enumerated.
        for (var index = 0; index < alternatives.Count; index++)
        {
            alternatives[index].Resolve(document, problems, satisfied, alternatives);
        }

        return Report(problems);
    }

    /// <summary>
    /// Deduplicates, and says so when there are more problems than one response should carry, so the
    /// count in the error's hint is never a truncated number presented as the whole story.
    /// </summary>
    private IReadOnlyList<ValidationProblem> Report(List<ValidationProblem> problems)
    {
        var distinct = problems.DistinctBy(problem => (problem.Pointer, problem.Message)).ToList();

        if (distinct.Count == 0)
        {
            return [new ValidationProblem(string.Empty, $"The {noun} does not match its schema.")];
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
    private void Collect(
        EvaluationResults results,
        JsonElement document,
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
                group = new AlternativeGroup(this, pointer, applicator);
                alternatives.Add(group);
            }

            group.Add(results);
            return;
        }

        CollectFrom(results, document, problems, satisfied, alternatives);
    }

    /// <summary>
    /// The problems a node itself reports, plus those of everything failing below it. Split out from
    /// <see cref="Collect"/> so a branch of an <c>anyOf</c> can be walked on its own once the group it
    /// belongs to has decided that its branches have something specific to say.
    /// </summary>
    private void CollectFrom(
        EvaluationResults results,
        JsonElement document,
        List<ValidationProblem> problems,
        HashSet<(string Instance, string Applicator)> satisfied,
        List<AlternativeGroup> alternatives)
    {
        var pointer = results.InstanceLocation.ToString();

        if (results.Errors is not null)
        {
            foreach (var (keyword, message) in results.Errors)
            {
                problems.AddRange(Describe(keyword, message, pointer, results.EvaluationPath.ToString(), document));
            }
        }

        foreach (var nested in results.Details ?? [])
        {
            Collect(nested, document, problems, satisfied, alternatives);
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

    private IEnumerable<ValidationProblem> Describe(
        string keyword,
        string message,
        string pointer,
        string evaluationPath,
        JsonElement document)
    {
        if (string.Equals(keyword, "required", StringComparison.Ordinal))
        {
            // JSON Schema reports a missing member against the object that should hold it, so the
            // pointer of the member itself is synthesized here (mcp-tools.md §save_search_profile).
            var missing = MissingProperties(message, pointer, document);
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
            return [new ValidationProblem(pointer, $"'{name}' is not a property the {noun} allows.")];
        }

        if (string.Equals(keyword, "pattern", StringComparison.Ordinal)
            && JsonPointers.TryResolve(document, pointer, out var value)
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
    private static List<string> MissingProperties(string message, string pointer, JsonElement document)
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

        var present = JsonPointers.TryResolve(document, pointer, out var instance) && instance.ValueKind == JsonValueKind.Object
            ? instance
            : default;

        return [.. names.Where(name => present.ValueKind != JsonValueKind.Object || !present.TryGetProperty(name, out _))];
    }

    private static string Sentence(string message) =>
        message.Length > 0 && !message.EndsWith('.') ? message + "." : message;

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
    private sealed class AlternativeGroup(SchemaProblems owner, string pointer, string applicator)
    {
        private readonly List<EvaluationResults> _branches = [];

        public bool Matches(string candidatePointer, string candidateApplicator) =>
            string.Equals(pointer, candidatePointer, StringComparison.Ordinal)
            && string.Equals(applicator, candidateApplicator, StringComparison.Ordinal);

        public void Add(EvaluationResults branch) => _branches.Add(branch);

        public void Resolve(
            JsonElement document,
            List<ValidationProblem> problems,
            HashSet<(string Instance, string Applicator)> satisfied,
            List<AlternativeGroup> alternatives)
        {
            var missing = new List<string>();
            foreach (var branch in _branches)
            {
                if (!MissingMembersOnly(branch, document, out var names))
                {
                    foreach (var reported in _branches)
                    {
                        owner.CollectFrom(reported, document, problems, satisfied, alternatives);
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
        private bool MissingMembersOnly(EvaluationResults branch, JsonElement document, out List<string> missing)
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

                missing.AddRange(MissingProperties(message, pointer, document));
            }

            return missing.Count > 0;
        }
    }
}

/// <summary>Reads a schema that travels inside this assembly as an embedded resource.</summary>
internal static class EmbeddedSchema
{
    public static JsonSchema Load(string resourceName)
    {
        using var stream = typeof(EmbeddedSchema).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The embedded schema '{resourceName}' is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }
}
