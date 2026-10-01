using System.Text.Json;
using ProspectStudio.Core.Reference;

namespace ProspectStudio.Infrastructure.Reference;

/// <summary>
/// <c>refdata\manifest.json</c>: which step wrote which file, from where and when (POC-2). Reading it
/// back is how a skipped step still reports its source and vintage without re-downloading anything.
/// </summary>
public sealed record ReferenceManifest(int Version, DateTimeOffset UpdatedAt, IReadOnlyList<ReferenceManifestStep> Steps)
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static ReferenceManifest Empty { get; } = new(CurrentVersion, DateTimeOffset.MinValue, []);

    public ReferenceManifestStep? Find(string step) =>
        Steps.FirstOrDefault(entry => string.Equals(entry.Step, step, StringComparison.Ordinal));

    public static async Task<ReferenceManifest> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return Empty;
        }

        try
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ReferenceManifest>(text, _json) ?? Empty;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            // A manifest we cannot read is treated as absent: the outputs themselves decide what is done.
            return Empty;
        }
    }

    public static Task WriteAsync(
        string path,
        DateTimeOffset updatedAt,
        IEnumerable<ReferenceStepResult> steps,
        CancellationToken cancellationToken)
    {
        var manifest = new ReferenceManifest(
            CurrentVersion,
            updatedAt,
            [
                .. steps.Select(step => new ReferenceManifestStep(
                    step.Step,
                    step.File,
                    step.SourceUrl?.ToString(),
                    step.RetrievedAt,
                    step.Rows)),
            ]);

        return File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, _json), cancellationToken);
    }
}

public sealed record ReferenceManifestStep(
    string Step,
    string File,
    string? SourceUrl,
    DateTimeOffset? RetrievedAt,
    int Rows);
