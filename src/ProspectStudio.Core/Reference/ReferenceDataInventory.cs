namespace ProspectStudio.Core.Reference;

/// <summary>
/// Whether <c>refdata\</c> holds a complete set of reference files, which is what
/// <c>get_status.ready.referenceData</c> reports and what makes <c>resolve_geography</c> answer instead
/// of returning <c>NOT_READY</c> (POC-1, POC-2). Core asks the question; Infrastructure looks at the
/// disk.
/// </summary>
public interface IReferenceDataInventory
{
    bool IsComplete { get; }

    /// <summary>The expected files that are not there yet, for a message that says what to do.</summary>
    IReadOnlyList<string> MissingFiles { get; }
}
