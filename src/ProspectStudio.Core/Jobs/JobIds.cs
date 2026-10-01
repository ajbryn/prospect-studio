using ProspectStudio.Core.Ids;

namespace ProspectStudio.Core.Jobs;

/// <summary>
/// Job ids: <c>job_</c> plus 6 characters from the unambiguous alphabet in
/// <see cref="ShortCodes.Alphabet"/> (mcp-tools.md §get_job shows <c>job_ab12cd</c>).
/// </summary>
public static class JobIds
{
    public const string Prefix = "job_";

    public const int CodeLength = 6;

    public static string New() => Prefix + ShortCodes.Generate(CodeLength);
}
