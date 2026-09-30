using System.Reflection;

namespace ProspectStudio.Core;

public static class ProductVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var version = typeof(ProductVersion).Assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
