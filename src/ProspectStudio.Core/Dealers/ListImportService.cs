namespace ProspectStudio.Core.Dealers;

/// <summary>
/// Which <see cref="ImportListKinds"/> values are built. <c>import_list</c> advertises <c>warranty</c>
/// from the start, so a caller asking for it has made no mistake; it is a feature that has not landed.
/// </summary>
public static class ImportListKindRules
{
    /// <summary>Throws <see cref="ImportListUnsupportedException"/> for a kind a later chunk builds.</summary>
    public static void RejectUnbuilt(string kind)
    {
        if (kind == ImportListKinds.Warranty)
        {
            throw new ImportListUnsupportedException(
                "Warranty registrations cannot be imported yet.",
                "Available after chunk C13; import dealers, territories or suppression for now.");
        }
    }
}

/// <summary>
/// The workspace files <c>import_list</c> falls back to when the caller gives no <c>path</c>
/// (mcp-tools.md §import_list, technical-design §5.1: <c>Dealers\</c> and <c>Suppression\</c>). Core
/// defines it; Infrastructure looks at the disk.
/// </summary>
public interface IListFileLocator
{
    /// <summary>The folder a kind is read from, for an error message that says where the server looked.</summary>
    string FolderFor(string kind);

    /// <summary>
    /// Every file in that folder that could be this kind. None is <c>NOT_FOUND</c> and more than one is
    /// a question for the caller rather than a guess.
    /// </summary>
    IReadOnlyList<string> Find(string kind);
}

/// <summary>
/// <c>import_list</c> (mcp-tools.md §import_list): resolves the file to read - the caller's
/// <c>path</c>, or the one file in the workspace folder for that kind - and hands it to the importer.
/// </summary>
public sealed class ListImportService(IListImporter importer, IListFileLocator locator)
{
    public async Task<ImportListResult> ImportAsync(
        string kind,
        string? path,
        string? reason,
        bool replace,
        CancellationToken cancellationToken)
    {
        // The kind is settled before any file is looked for, so an unknown kind is reported as the kind
        // it is and an unbuilt one as UNSUPPORTED - not as a workspace folder that happens to hold no
        // matching file.
        var wanted = (kind ?? string.Empty).Trim().ToLowerInvariant();
        if (!ImportListKinds.IsKnown(wanted))
        {
            throw new ImportListRequestException(
                $"'{kind}' is not a list kind.",
                $"Use one of {string.Join(", ", ImportListKinds.All)}.");
        }

        ImportListKindRules.RejectUnbuilt(wanted);

        return await importer
            .ImportAsync(
                wanted,
                path is { Length: > 0 } ? path.Trim() : Default(wanted),
                reason,
                replace,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private string Default(string kind)
    {
        var folder = locator.FolderFor(kind);
        var found = locator.Find(kind);

        return found.Count switch
        {
            1 => found[0],
            0 => throw new ImportListFileNotFoundException(
                $"No {kind} list was found in '{folder}'.",
                $"Save the file as a .csv or .xlsx in '{folder}', or pass path with its full location."),
            _ => throw new ImportListRequestException(
                $"'{folder}' holds {found.Count} files that could be the {kind} list.",
                "Pass path to say which one: "
                + string.Join(", ", found.Select(Path.GetFileName))),
        };
    }
}
