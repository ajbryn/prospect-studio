using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProspectStudio.Mcp.Errors;

/// <summary>
/// Writes every timestamp in a tool response as <c>yyyy-MM-ddTHH:mm:ssZ</c>, the form every example in
/// mcp-tools.md uses. The default round-trip format carries the offset and seven fractional digits,
/// which is noise in a paged list and disagrees with the contract. Reading stays tolerant: anything
/// <see cref="DateTimeOffset.Parse(string, IFormatProvider)"/> accepts is fine.
/// </summary>
public sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
            : reader.GetDateTimeOffset();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// The nullable companion, so an absent <c>lastExportAt</c> stays <c>null</c> rather than becoming a
/// timestamp of its default value.
/// </summary>
public sealed class NullableUtcTimestampConverter : JsonConverter<DateTimeOffset?>
{
    private static readonly UtcTimestampConverter Inner = new();

    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : Inner.Read(ref reader, typeToConvert, options);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            Inner.Write(writer, value.Value, options);
        }
    }
}
