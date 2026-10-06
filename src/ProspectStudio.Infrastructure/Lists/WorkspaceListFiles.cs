using ProspectStudio.Core.Dealers;
using ProspectStudio.Infrastructure.Workspace;

namespace ProspectStudio.Infrastructure.Lists;

/// <summary>
/// The workspace files <c>import_list</c> defaults to when the caller gives no <c>path</c>: the
/// <c>Dealers\</c> and <c>Suppression\</c> folders of technical-design §5.1.
/// </summary>
/// <remarks>
/// Dealers and territories are matched by file name, because both live in the same folder and "any CSV
/// in Dealers\" would be ambiguous for both. A suppression list is matched by extension alone, the way
/// §5.1 writes it (<c>Suppression\ *.csv|xlsx</c>): these files arrive from a CRM with whatever name it
/// gave them.
/// </remarks>
public sealed class WorkspaceListFiles(string homeDirectory) : IListFileLocator
{
    public string FolderFor(string kind) => Path.Combine(
        homeDirectory,
        kind == ImportListKinds.Suppression
            ? WorkspaceBootstrapper.SuppressionFolder
            : WorkspaceBootstrapper.DealersFolder);

    public IReadOnlyList<string> Find(string kind)
    {
        var folder = FolderFor(kind);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return
        [
            .. Directory
                .EnumerateFiles(folder)
                .Where(path => ListFileReader.Extensions.Contains(
                    Path.GetExtension(path),
                    StringComparer.OrdinalIgnoreCase))
                .Where(path => kind == ImportListKinds.Suppression || Names(path, kind))
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
    }

    private static bool Names(string path, string kind) =>
        Path.GetFileNameWithoutExtension(path).Equals(kind, StringComparison.OrdinalIgnoreCase);
}
