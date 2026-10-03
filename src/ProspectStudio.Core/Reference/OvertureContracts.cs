namespace ProspectStudio.Core.Reference;

/// <summary>
/// The Overture step of the setup pipeline (technical-design §6.1) and the file layout it writes under
/// <c>PROSPECT_STUDIO_DATA\overture</c> (§5.1). Named here so the tool, the CLI verb, the tests and
/// <c>get_status</c> all agree.
/// </summary>
public static class OvertureSteps
{
    /// <summary>One step per state, so the job can report and resume per state.</summary>
    public const string Places = "overture";

    /// <summary>The folder under <c>PROSPECT_STUDIO_DATA</c>.</summary>
    public const string FolderName = "overture";

    /// <summary><c>places_&lt;ST&gt;.parquet</c>, inside a per-release folder.</summary>
    public static string FileFor(string state) =>
        $"places_{(state ?? throw new ArgumentNullException(nameof(state))).ToUpperInvariant()}.parquet";
}

/// <summary>
/// Which Overture extracts are on disk. Core defines it so <c>get_status</c> and
/// <c>find_candidates</c> can report <c>NOT_READY</c> without knowing about DuckDB or file paths.
/// </summary>
public interface IOvertureDataInventory
{
    /// <summary>The release the server is configured to use (<c>PS_OVERTURE_RELEASE</c>).</summary>
    string Release { get; }

    /// <summary>The release folder, whether or not it exists yet.</summary>
    string Directory { get; }

    /// <summary>The two-letter states with a complete extract for <see cref="Release"/>, sorted.</summary>
    IReadOnlyList<string> States { get; }

    /// <summary>True when that state's extract exists and is not empty.</summary>
    bool HasState(string state);

    /// <summary>The path of a state's extract, whether or not it exists.</summary>
    string PlacesParquet(string state);
}

/// <summary>What the Overture step did for one state (mcp-tools.md §prepare_data).</summary>
/// <param name="Skipped">
/// True when the extract was already there and <c>force</c> was not set. It is the only reason the
/// test suite can run the step at all: the source is a 205 MB download from S3.
/// </param>
public sealed record OvertureStepResult(
    string Step,
    string State,
    string File,
    bool Skipped,
    int Rows,
    string Release,
    DateTimeOffset? RetrievedAt);

/// <summary>
/// Where the Overture step gets its data. The real implementation reads Overture on anonymous S3 with
/// DuckDB; tests substitute it, so the extract-and-write path is covered offline instead of only by
/// the opt-in network test - the same seam <see cref="IReferenceFileSource"/> gives the Census steps.
/// </summary>
public interface IOvertureExtractSource
{
    /// <summary>
    /// The newest published release, or null when the catalog cannot say. Called only when an extract
    /// actually has to be written, so a run that skips every state stays offline.
    /// </summary>
    Task<string?> FindLatestReleaseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="state"/>'s places for <paramref name="release"/> to
    /// <paramref name="destination"/> and returns the row count.
    /// </summary>
    Task<int> ExtractAsync(
        string state,
        string release,
        string destination,
        CancellationToken cancellationToken);
}

/// <summary>
/// How the Overture step reports one finished state, so <c>prepare_data</c> can write progress to the
/// <c>jobs</c> table and the <c>setup</c> verb can print a line per state.
/// </summary>
public delegate Task OvertureStepReporter(
    OvertureStepResult step,
    double progress,
    CancellationToken cancellationToken);
