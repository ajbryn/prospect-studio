using System.Text.Json;

namespace ProspectStudio.Core.SearchProfiles;

/// <summary>RFC 6901 pointers over a <see cref="JsonElement"/>, for locating a validation problem.</summary>
internal static class JsonPointers
{
    public static string Append(string pointer, string propertyName) =>
        $"{pointer}/{propertyName.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)}";

    public static bool TryResolve(JsonElement root, string pointer, out JsonElement value)
    {
        value = root;
        if (pointer.Length == 0)
        {
            return true;
        }

        foreach (var raw in pointer.TrimStart('/').Split('/'))
        {
            var token = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

            switch (value.ValueKind)
            {
                case JsonValueKind.Object when value.TryGetProperty(token, out var property):
                    value = property;
                    break;
                case JsonValueKind.Array when int.TryParse(token, out var index)
                    && index >= 0
                    && index < value.GetArrayLength():
                    value = value[index];
                    break;
                default:
                    value = default;
                    return false;
            }
        }

        return true;
    }
}
