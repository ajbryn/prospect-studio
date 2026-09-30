namespace ProspectStudio.Mcp.Tests.Infrastructure;

/// <summary>
/// A throw-away <c>PROSPECT_STUDIO_HOME</c> / <c>PROSPECT_STUDIO_DATA</c> pair under the system temp
/// folder, so a test that starts its own server leaves no files behind.
/// </summary>
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "prospect-studio-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Home => Path.Combine(Root, "home");

    public string Data => Path.Combine(Root, "data");

    public string WorkspaceFolder(params string[] parts) => Path.Combine([Home, .. parts]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The server may still hold its log file or the database; a leftover temp folder is
            // harmless and the OS clears it.
        }
    }
}
