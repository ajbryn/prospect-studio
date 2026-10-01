using System.Buffers;
using System.Text;

namespace ProspectStudio.Infrastructure.Reference;

/// <summary>
/// The small amount of delimited-text handling the reference pipeline needs: RFC 4180 quoting on the way
/// out, and splitting a delimited line on the way in.
/// </summary>
internal static class DelimitedText
{
    /// <summary>The characters that force a field to be quoted (RFC 4180).</summary>
    private static readonly SearchValues<char> _mustQuote = SearchValues.Create(",\"\n\r");

    public static string Row(params ReadOnlySpan<string> fields)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < fields.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(Field(fields[index]));
        }

        return builder.ToString();
    }

    public static string Field(string value)
    {
        if (!value.AsSpan().ContainsAny(_mustQuote))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    /// <summary>
    /// Splits a line on <paramref name="delimiter"/>, ignoring the delimiter inside double quotes, and
    /// drops a UTF-8 BOM left on the first field - a BOM there silently breaks every lookup of the
    /// first column.
    /// </summary>
    public static string[] Split(string line, char delimiter)
    {
        ArgumentNullException.ThrowIfNull(line);

        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];

            if (character == '"')
            {
                // RFC 4180 escapes a quote inside a quoted field by doubling it. Toggling twice instead
                // would drop both characters and lose the quote the field actually holds.
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                    continue;
                }

                quoted = !quoted;
            }
            else if (character == delimiter && !quoted)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(character);
            }
        }

        fields.Add(field.ToString());

        if (fields.Count > 0)
        {
            fields[0] = fields[0].TrimStart('﻿');
        }

        return [.. fields];
    }
}
