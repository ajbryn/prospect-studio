using System.Text.Json;

namespace ProspectStudio.Tests.Shared;

/// <summary>
/// One entry of <c>poc/fixtures/sample-research-invalid.json</c>: a named case, the failure it claims
/// to produce, and the document that should produce it.
/// </summary>
/// <param name="ExpectedError">
/// The spec pack's own wording, e.g. <c>signals/0/date: pattern</c>. C1 established that only the
/// <strong>pointer</strong> is contractual and the rest of the message is advisory, so
/// <see cref="Pointer"/> is what tests assert on.
/// </param>
internal sealed record InvalidResearchCase(string Case, string ExpectedError, JsonElement Research)
{
    /// <summary>
    /// The JSON pointer <see cref="ExpectedError"/> names, e.g. <c>/signals/0/date</c>. Everything up
    /// to the first <c>": "</c> is the location; what follows is the reason.
    /// </summary>
    public string Pointer
    {
        get
        {
            var separator = ExpectedError.IndexOf(": ", StringComparison.Ordinal);
            var location = separator < 0 ? ExpectedError : ExpectedError[..separator];
            return "/" + location.Trim().TrimStart('/');
        }
    }

    /// <summary>The reason half of <see cref="ExpectedError"/>, for a failure message a human can read.</summary>
    public string Reason
    {
        get
        {
            var separator = ExpectedError.IndexOf(": ", StringComparison.Ordinal);
            return separator < 0 ? ExpectedError : ExpectedError[(separator + 2)..];
        }
    }

    public override string ToString() => Case;
}

/// <summary>
/// The research documents the C6 tests use: the spec pack's valid and invalid fixtures, and the
/// worked-example documents under <c>src/tests/Fixtures/research</c> (see its README for the
/// arithmetic each one produces).
/// </summary>
internal static class SampleResearch
{
    /// <summary>
    /// The spec pack's accepted document. It is also worked example 1 (Bayou Fulfillment), which is why
    /// the scoring tests read it rather than keeping a copy: an edit to the spec fixture has to be felt.
    /// </summary>
    public static string ValidPath => RepoFixtures.Fixture("sample-research-valid.json");

    public static string InvalidPath => RepoFixtures.Fixture("sample-research-invalid.json");

    /// <summary>Worked example 2: Gulf Coast Sign &amp; Lighting (<c>fx_0002</c>).</summary>
    public static string GulfCoastSignPath => ResearchFixture("gulf-coast-sign.research.json");

    /// <summary>Worked example 3: Westpark Metal Fab (<c>fx_0007</c>), the 71 B case.</summary>
    public static string WestparkMetalFabPath => ResearchFixture("westpark-metal-fab.research.json");

    /// <summary>A valid <c>status: "no_signal"</c> document with an empty <c>signals</c> array.</summary>
    public static string NorthlineGlassNoSignalPath => ResearchFixture("northline-glass.no-signal.research.json");

    public static JsonElement Valid() => Read(ValidPath);

    public static JsonElement GulfCoastSign() => Read(GulfCoastSignPath);

    public static JsonElement WestparkMetalFab() => Read(WestparkMetalFabPath);

    public static JsonElement NorthlineGlassNoSignal() => Read(NorthlineGlassNoSignalPath);

    /// <summary>Every case in <c>sample-research-invalid.json</c>, in file order.</summary>
    public static IReadOnlyList<InvalidResearchCase> InvalidCases { get; } = ReadInvalidCases();

    /// <summary>One case by its <c>case</c> name.</summary>
    public static InvalidResearchCase InvalidCase(string name) =>
        InvalidCases.SingleOrDefault(entry => entry.Case == name)
        ?? throw new KeyNotFoundException(
            $"No case '{name}' in {InvalidPath}. It has: {string.Join(", ", InvalidCases.Select(entry => entry.Case))}");

    /// <summary>A document read from disk and detached from its <see cref="JsonDocument"/>.</summary>
    public static JsonElement Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The same document with one member replaced, for a test that needs a variant without a fixture of
    /// its own (e.g. a <c>no_signal</c> document with no <c>signals</c> member at all).
    /// </summary>
    public static JsonElement With(JsonElement research, params (string Name, object? Value)[] members)
    {
        ArgumentNullException.ThrowIfNull(members);

        var overrides = members.ToDictionary(member => member.Name, member => member.Value, StringComparer.Ordinal);
        var removals = overrides.Where(entry => entry.Value is null).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);

        var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            foreach (var property in research.EnumerateObject())
            {
                if (removals.Contains(property.Name))
                {
                    continue;
                }

                if (overrides.TryGetValue(property.Name, out var replacement))
                {
                    writer.WritePropertyName(property.Name);
                    JsonSerializer.Serialize(writer, replacement);
                    overrides.Remove(property.Name);
                    continue;
                }

                property.WriteTo(writer);
            }

            foreach (var (name, value) in overrides.Where(entry => entry.Value is not null))
            {
                writer.WritePropertyName(name);
                JsonSerializer.Serialize(writer, value);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The same document with every signal re-dated to one whole month before
    /// <paramref name="now"/>, keeping each one's <c>type</c>.
    /// </summary>
    /// <remarks>
    /// For the tests that go through the real server, which runs on <c>TimeProvider.System</c> and
    /// cannot be handed <see cref="FixedTimeProvider"/>. §7.6's <c>signals</c> feature only credits
    /// buying signals dated within twelve months, so a committed fixture date would make those tests
    /// expire: worked example 2's single hiring signal is dated <c>2026-08</c>, and in September 2027 it
    /// leaves the window, the feature drops 0.6 → 0 and the example silently stops being tier A. The
    /// signal <em>count and types</em> are what the scoring depends on, and re-dating preserves both.
    /// Tests that pin §7.6's window itself use <see cref="FixedTimeProvider"/> and the fixture dates.
    /// </remarks>
    public static JsonElement WithSignalsDatedOneMonthBefore(JsonElement research, DateTimeOffset now)
    {
        if (!research.TryGetProperty("signals", out var signals) || signals.ValueKind != JsonValueKind.Array)
        {
            return research;
        }

        var month = now.AddMonths(-1).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

        var rewritten = signals.EnumerateArray()
            .Select(signal => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = signal.GetProperty("type").GetString(),
                ["text"] = signal.GetProperty("text").GetString(),
                ["url"] = signal.GetProperty("url").GetString(),
                ["date"] = month,
            })
            .ToList();

        return With(research, ("signals", rewritten));
    }

    private static IReadOnlyList<InvalidResearchCase> ReadInvalidCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(InvalidPath));

        return
        [
            .. document.RootElement.EnumerateArray().Select(entry => new InvalidResearchCase(
                entry.GetProperty("case").GetString() ?? string.Empty,
                entry.GetProperty("expectedError").GetString() ?? string.Empty,
                entry.GetProperty("research").Clone())),
        ];
    }

    private static string ResearchFixture(string name) => RepoFixtures.ResearchFixture(name);
}
