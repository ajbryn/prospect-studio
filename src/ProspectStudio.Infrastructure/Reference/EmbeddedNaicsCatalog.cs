using System.Globalization;
using ProspectStudio.Core.Naics;

namespace ProspectStudio.Infrastructure.Reference;

/// <summary>
/// The NAICS 2022 table, read from <c>Reference/naics2022.csv</c>, which travels inside this assembly.
/// That is what makes <c>lookup_naics</c> work on a first run, before <c>prepare_data</c> has downloaded
/// anything (implementation-plan C2).
/// </summary>
/// <remarks>
/// Generated once from <c>https://www.census.gov/naics/2022NAICS/2-6%20digit_2022_Codes.xlsx</c>:
/// 2,125 rows, titles trimmed, and the three sector codes that are ranges (<c>31-33</c>, <c>44-45</c>,
/// <c>48-49</c>) kept as strings.
/// </remarks>
public sealed class EmbeddedNaicsCatalog : INaicsCatalog
{
    public const string ResourceName = "ProspectStudio.Infrastructure.Reference.naics2022.csv";

    private static readonly Lazy<IReadOnlyList<NaicsEntry>> _entries = new(Read, LazyThreadSafetyMode.ExecutionAndPublication);

    public Task<IReadOnlyList<NaicsEntry>> GetEntriesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_entries.Value);
    }

    private static IReadOnlyList<NaicsEntry> Read()
    {
        using var stream = typeof(EmbeddedNaicsCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"'{ResourceName}' is not embedded in {typeof(EmbeddedNaicsCatalog).Assembly.GetName().Name}. "
                + "The Infrastructure project embeds Reference/naics2022.csv.");

        using var reader = new StreamReader(stream);
        var entries = new List<NaicsEntry>();

        _ = reader.ReadLine();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var fields = DelimitedText.Split(line, ',');
            if (fields.Length < 3 || !int.TryParse(fields[2], CultureInfo.InvariantCulture, out var level))
            {
                continue;
            }

            entries.Add(new NaicsEntry(fields[0].Trim(), fields[1].Trim(), level));
        }

        return entries;
    }
}
