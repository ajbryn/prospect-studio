using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProspectStudio.Tests.Shared;

/// <summary>
/// What a tagged fixture row is supposed to do when the sample search profile is run over it. The tag
/// is the leading <c>[word]</c> of <c>fixture_note</c> in
/// <c>poc/fixtures/sample-places.csv</c>.
/// </summary>
internal enum FixtureExpectation
{
    /// <summary>Selected by the profile and surviving its exclusions, so it reaches the campaign.</summary>
    Candidate,

    /// <summary>Selected by the profile and then removed by an exclusion list.</summary>
    Excluded,

    /// <summary>Not selected at all - out of scope, below the confidence floor, or in no target category.</summary>
    Unmatched,
}

/// <summary>
/// The sample search profile, read the way <c>find_candidates</c> reads it, so a fixture test can ask
/// "would this row be selected?" without going near DuckDB.
/// </summary>
/// <remarks>
/// This exists because a profile edit can silently disarm a fixture row. It happened three times
/// across C4: the dealer rows sat in an excluded category so suppression had nothing to remove,
/// <c>machine_and_tool_rental</c> was left with no rows at all, and dropping
/// <c>storage_facility</c> from the profile stopped <c>fx_0111</c> matching - which was the only row
/// exercising the missing-<c>freeform</c> extraction path. Each time the suite stayed green while
/// testing one thing less.
/// </remarks>
internal sealed class SampleProfile
{
    private readonly Regex _keywords;
    private readonly Regex? _excludedKeywords;

    private SampleProfile(
        HashSet<string> categories,
        HashSet<string> excludedCategories,
        Regex keywords,
        Regex? excludedKeywords,
        double minConfidence)
    {
        Categories = categories;
        ExcludedCategories = excludedCategories;
        _keywords = keywords;
        _excludedKeywords = excludedKeywords;
        MinConfidence = minConfidence;
    }

    public HashSet<string> Categories { get; }

    public HashSet<string> ExcludedCategories { get; }

    public double MinConfidence { get; }

    public static SampleProfile Load()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepoFixtures.SampleSearchProfile));
        var root = document.RootElement;
        var segments = root.GetProperty("segments").EnumerateArray().ToList();

        var exclusions = root.GetProperty("exclusions");

        return new SampleProfile(
            [.. segments.SelectMany(segment => Strings(segment, "overtureCategories"))],
            [.. Strings(exclusions, "overtureCategories")],
            Alternation([.. segments.SelectMany(segment => Strings(segment, "keywords"))])
                ?? throw new InvalidOperationException("the sample profile has no keywords at all."),
            Alternation([.. Strings(exclusions, "keywords")]),
            root.TryGetProperty("minConfidence", out var floor) ? floor.GetDouble() : 0.6);
    }

    /// <summary>In scope: inside the Houston CBSA and at or above the profile's confidence floor.</summary>
    public bool InScope(SamplePlace place) =>
        SamplePlaces.HoustonCbsaCounties.Contains(place.CountyFips) && place.Confidence >= MinConfidence;

    /// <summary>True when a segment's categories reach this row, as leaf or as an interior node.</summary>
    public bool MatchesCategory(SamplePlace place) =>
        Categories.Contains(place.TaxonomyPrimary) || place.TaxonomyHierarchy.Any(Categories.Contains);

    public bool MatchesKeyword(SamplePlace place) => _keywords.IsMatch(place.Name);

    public bool IsExcludedByCategory(SamplePlace place) =>
        ExcludedCategories.Contains(place.TaxonomyPrimary)
        || place.TaxonomyHierarchy.Any(ExcludedCategories.Contains);

    public bool IsExcludedByKeyword(SamplePlace place) =>
        _excludedKeywords is not null && _excludedKeywords.IsMatch(place.Name);

    /// <summary>Selected by §6.3's county, confidence and category-or-keyword filters.</summary>
    public bool Selects(SamplePlace place) =>
        InScope(place) && (MatchesCategory(place) || MatchesKeyword(place));

    public bool IsExcluded(SamplePlace place) =>
        IsExcludedByCategory(place) || IsExcludedByKeyword(place);

    /// <summary>What actually happens to this row, to compare against its documented tag.</summary>
    public FixtureExpectation Outcome(SamplePlace place) =>
        !Selects(place) ? FixtureExpectation.Unmatched
        : IsExcluded(place) ? FixtureExpectation.Excluded
        : FixtureExpectation.Candidate;

    /// <summary>Why a row was not selected, for a failure message that is actionable on its own.</summary>
    public string Explain(SamplePlace place)
    {
        var reasons = new List<string>();

        if (!SamplePlaces.HoustonCbsaCounties.Contains(place.CountyFips))
        {
            reasons.Add($"county {place.CountyFips} is outside the Houston CBSA");
        }

        if (place.Confidence < MinConfidence)
        {
            reasons.Add($"confidence {place.Confidence} is below {MinConfidence}");
        }

        if (!MatchesCategory(place))
        {
            reasons.Add($"'{place.TaxonomyPrimary}' and its hierarchy are in no segment");
        }

        if (!MatchesKeyword(place))
        {
            reasons.Add($"no segment keyword appears in '{place.Name}'");
        }

        if (IsExcludedByCategory(place))
        {
            reasons.Add("its category is in exclusions.overtureCategories");
        }

        if (IsExcludedByKeyword(place))
        {
            reasons.Add("its name hits exclusions.keywords");
        }

        return reasons.Count == 0 ? "it matches cleanly" : string.Join("; ", reasons);
    }

    private static List<string> Strings(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(item => item.GetString() ?? string.Empty)]
            : [];

    /// <summary>
    /// The same whole-string-free, case-insensitive alternation <c>find_candidates</c> hands DuckDB's
    /// <c>regexp_matches(lower(name), …)</c>.
    /// </summary>
    private static Regex? Alternation(List<string> words) =>
        words.Count == 0
            ? null
            : new Regex(
                string.Join("|", words.Select(Regex.Escape)),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

/// <summary>Reads the <c>[tag]</c> prefix out of a row's <c>fixture_note</c>.</summary>
internal static class FixtureTags
{
    private static readonly Regex Leading = new(@"^\[(?<tag>[a-z]+)\]", RegexOptions.Compiled);

    /// <summary>The row's documented expectation, or null when the row carries no tag.</summary>
    public static FixtureExpectation? Of(SamplePlace place)
    {
        var match = Leading.Match(place.FixtureNote);
        if (!match.Success)
        {
            return null;
        }

        var tag = match.Groups["tag"].Value;

        return tag switch
        {
            "candidate" => FixtureExpectation.Candidate,
            "excluded" => FixtureExpectation.Excluded,
            "unmatched" => FixtureExpectation.Unmatched,
            _ => throw new InvalidOperationException(
                $"{place.Id} carries an unknown fixture tag '[{tag}]'. The tags are [candidate], "
                + "[excluded] and [unmatched] - see src/tests/Fixtures/places/README.md. A new tag has "
                + "to be taught to FixtureTags, or the guard silently stops checking that row."),
        };
    }

    /// <summary>Every tagged row, with what it claims to do.</summary>
    public static IReadOnlyList<(SamplePlace Place, FixtureExpectation Expected)> Tagged { get; } =
    [
        .. SamplePlaces.All
            .Select(place => (Place: place, Expected: Of(place)))
            .Where(row => row.Expected is not null)
            .Select(row => (row.Place, row.Expected!.Value)),
    ];
}
