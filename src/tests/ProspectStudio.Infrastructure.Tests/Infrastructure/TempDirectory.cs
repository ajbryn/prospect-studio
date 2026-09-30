namespace ProspectStudio.Infrastructure.Tests.Infrastructure;

/// <summary>
/// A throw-away folder under the system temp directory. Every test gets its own, so nothing leaks
/// between tests and nothing is left behind.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "prospect-studio-infrastructure-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A database file still held by a pooled connection is the usual cause; a leftover temp
            // folder is harmless and the OS clears it.
        }
    }
}
