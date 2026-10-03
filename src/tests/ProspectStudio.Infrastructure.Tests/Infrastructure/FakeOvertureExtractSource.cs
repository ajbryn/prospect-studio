using ProspectStudio.Core.Reference;

namespace ProspectStudio.Infrastructure.Tests.Infrastructure;

/// <summary>
/// A stand-in for the S3 extract, so the <em>extract-and-write</em> direction of the Overture step is
/// covered offline instead of only by the opt-in network test - the same reason
/// <see cref="FakeReferenceFileSource"/> exists for the Census steps.
/// </summary>
/// <remarks>
/// It writes two bytes rather than a real Parquet file: nothing here reads the output, and the
/// preparer's job is the <c>.part</c>-then-move discipline and the skip decision, neither of which
/// cares what the bytes are.
/// </remarks>
internal sealed class FakeOvertureExtractSource(int rows = 7, string? latestRelease = null)
    : IOvertureExtractSource
{
    /// <summary>Every destination it was asked to write, in order, so a test can see the <c>.part</c>.</summary>
    public List<string> Destinations { get; } = [];

    /// <summary>The states it was asked for, in order. Empty means the step skipped.</summary>
    public List<string> States { get; } = [];

    public int Calls => Destinations.Count;

    /// <summary>
    /// When set, the source creates the <c>.part</c> file and <em>then</em> throws - the shape of a
    /// download that dies halfway, which is the only thing the <c>.part</c> discipline protects against.
    /// </summary>
    public Exception? ThrowAfterWriting { get; set; }

    public Task<string?> FindLatestReleaseAsync(CancellationToken cancellationToken) =>
        Task.FromResult(latestRelease);

    public async Task<int> ExtractAsync(
        string state,
        string release,
        string destination,
        CancellationToken cancellationToken)
    {
        States.Add(state);
        Destinations.Add(destination);

        await File.WriteAllBytesAsync(destination, [0x50, 0x41], cancellationToken);

        return ThrowAfterWriting is null ? rows : throw ThrowAfterWriting;
    }
}
