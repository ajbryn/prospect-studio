using ProspectStudio.Core.Reference;
using ProspectStudio.Tests.Shared;

namespace ProspectStudio.Infrastructure.Tests.Infrastructure;

/// <summary>
/// Hands the pipeline the committed trimmed sources instead of downloading from census.gov, and counts
/// how often each step asked - which is how the idempotence tests tell "skipped" from "did the work
/// again" without inspecting files.
/// </summary>
internal sealed class FakeReferenceFileSource : IReferenceFileSource
{
    private readonly Dictionary<string, int> _calls = ReferenceSteps.All.ToDictionary(step => step, _ => 0);

    public static DateTimeOffset RetrievedAt { get; } = new(2026, 9, 30, 11, 30, 0, TimeSpan.Zero);

    public IReadOnlyDictionary<string, int> Calls => _calls;

    public int TotalCalls => _calls.Values.Sum();

    public Task<ReferenceFile> GetAsync(string step, CancellationToken cancellationToken)
    {
        _calls[step] = _calls.TryGetValue(step, out var count) ? count + 1 : 1;

        var path = step switch
        {
            ReferenceSteps.Counties => RepoFixtures.CountyShapefileZipSource,
            ReferenceSteps.Cbsa => RepoFixtures.CbsaXlsxSource,
            ReferenceSteps.Zcta => RepoFixtures.ZctaCountyTextSource,
            _ => throw new ArgumentOutOfRangeException(nameof(step), step, "Not a reference-data step."),
        };

        return Task.FromResult(new ReferenceFile(path, new Uri(ReferenceDataFixture.SourceUrlFor(step)), RetrievedAt));
    }
}
