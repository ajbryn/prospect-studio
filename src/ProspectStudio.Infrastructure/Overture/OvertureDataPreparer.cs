using ProspectStudio.Core.Reference;

namespace ProspectStudio.Infrastructure.Overture;

/// <summary>
/// The Overture half of the setup pipeline (technical-design §6.1): one extract per state under
/// <c>overture\&lt;release&gt;\places_&lt;ST&gt;.parquet</c>, skipped when it is already there.
/// </summary>
/// <remarks>
/// Skipping is not a nicety here. The Texas extract is about 70 seconds and 205 MB from S3, so a step
/// that redid finished work would make <c>prepare_data</c> unusable and would put the network inside
/// every test that calls it (CLAUDE.md).
/// </remarks>
public sealed class OvertureDataPreparer(OvertureDataFiles files, IOvertureExtractSource source)
{
    /// <summary>
    /// Written to <c>.part</c> and moved into place, because a step decides it has nothing to do by
    /// seeing a non-empty file: a run that died mid-write would otherwise leave a truncated Parquet
    /// that every later run skips as finished.
    /// </summary>
    private const string PartialSuffix = ".part";

    public Task<IReadOnlyList<OvertureStepResult>> PrepareAsync(
        IReadOnlyList<string> states,
        bool force,
        CancellationToken cancellationToken) =>
        PrepareAsync(states, force, report: null, cancellationToken);

    public async Task<IReadOnlyList<OvertureStepResult>> PrepareAsync(
        IReadOnlyList<string> states,
        bool force,
        OvertureStepReporter? report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(states);

        var results = new List<OvertureStepResult>(states.Count);
        if (states.Count == 0)
        {
            return results;
        }

        var release = files.Release;
        var done = 0;

        foreach (var state in states)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destination = files.PlacesParquet(state);
            OvertureStepResult result;

            if (!force && files.HasState(state))
            {
                result = new OvertureStepResult(
                    OvertureSteps.Places,
                    state,
                    OvertureSteps.FileFor(state),
                    Skipped: true,
                    Rows: 0,
                    release,
                    RetrievedAt: null);
            }
            else
            {
                Directory.CreateDirectory(files.Directory);

                var partial = destination + PartialSuffix;
                var rows = await source
                    .ExtractAsync(state, release, partial, cancellationToken)
                    .ConfigureAwait(false);

                File.Move(partial, destination, overwrite: true);

                result = new OvertureStepResult(
                    OvertureSteps.Places,
                    state,
                    OvertureSteps.FileFor(state),
                    Skipped: false,
                    rows,
                    release,
                    DateTimeOffset.UtcNow);
            }

            results.Add(result);
            done++;

            if (report is not null)
            {
                await report(result, (double)done / states.Count, cancellationToken).ConfigureAwait(false);
            }
        }

        return results;
    }

    /// <summary>
    /// The newest published release, when it is not the one this server is configured to use. Only
    /// worth asking after something was actually extracted, so a skipped run never reaches the network.
    /// </summary>
    public async Task<string?> FindNewerReleaseAsync(CancellationToken cancellationToken)
    {
        var latest = await source.FindLatestReleaseAsync(cancellationToken).ConfigureAwait(false);

        return latest is { Length: > 0 } && !string.Equals(latest, files.Release, StringComparison.Ordinal)
            ? latest
            : null;
    }
}
