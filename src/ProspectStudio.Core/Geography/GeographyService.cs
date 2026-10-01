using System.Globalization;

namespace ProspectStudio.Core.Geography;

/// <summary>
/// The rules behind <c>resolve_geography</c> (technical-design §6.2, mcp-tools.md §resolve_geography):
/// turn a place name or one of the explicit input forms into a flat <c>GeoScope</c>, with
/// <c>alternatives</c> when the name could mean several places.
/// </summary>
public sealed class GeographyService(IGeographyReference reference, IAddressGeocoder geocoder)
{
    /// <summary>A prefix has to be this long before it may stand for a longer place name.</summary>
    private const int MinimumPrefixLength = 4;

    /// <summary>Words a user adds to a metro name that are not part of any CBSA title.</summary>
    private static readonly HashSet<string> _metroWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "metro", "metros", "metropolitan", "micropolitan", "statistical", "msa", "cbsa", "area", "region",
    };

    /// <summary>Suffixes the Census uses for a county equivalent, stripped before matching a name.</summary>
    private static readonly string[] _countySuffixes =
    [
        "city and borough", "census area", "municipality", "metropolitan government", "county", "parish",
        "borough", "municipio",
    ];

    public async Task<ResolvedGeography> ResolveAsync(GeoResolveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!reference.IsReady)
        {
            throw new GeographyNotReadyException(
                "Reference data is not prepared, so the server does not know US geography yet.");
        }

        var query = Trimmed(request.Query);
        var type = NormalizeType(request.Type);
        var values = Clean(request.Values);

        // A type with no values but a query means "read the query as this kind of place".
        if (values.Count == 0 && query is not null)
        {
            values = [query];
        }

        if (type is null)
        {
            return query is null
                ? throw new GeographyRequestException(
                    "Pass a query, or a type with values, or a radius with a center and radiusMiles.")
                : await ResolveQueryAsync(query, cancellationToken).ConfigureAwait(false);
        }

        return type switch
        {
            GeoScopeTypes.Radius => await RadiusScopeAsync(request, cancellationToken).ConfigureAwait(false),
            GeoScopeTypes.Dealer => throw new GeographyUnsupportedException(
                "Dealer territories are not available yet."),
            _ when values.Count == 0 => throw new GeographyRequestException(
                $"Type '{type}' needs at least one value."),
            GeoScopeTypes.State => await StateScopeAsync(ParseStates(values), cancellationToken).ConfigureAwait(false),
            GeoScopeTypes.Counties => await CountyValuesScopeAsync(values, cancellationToken).ConfigureAwait(false),
            GeoScopeTypes.Cbsa => await CbsaValuesScopeAsync(values, cancellationToken).ConfigureAwait(false),
            GeoScopeTypes.Zips => await ZipsScopeAsync(ParseZips(values), cancellationToken).ConfigureAwait(false),
            _ => throw new GeographyRequestException(
                $"'{request.Type}' is not a geography type. Use state, counties, cbsa, zips or radius."),
        };
    }

    private async Task<ResolvedGeography> ResolveQueryAsync(string query, CancellationToken cancellationToken)
    {
        if (UsStates.Find(query) is { } state)
        {
            return await StateScopeAsync([state], cancellationToken).ConfigureAwait(false);
        }

        if (TryReadZips(query, out var zips))
        {
            return await ZipsScopeAsync(zips, cancellationToken).ConfigureAwait(false);
        }

        if (NamesACounty(query))
        {
            return await CountyValuesScopeAsync([query], cancellationToken).ConfigureAwait(false);
        }

        var metros = await MatchCbsasAsync(query, cancellationToken).ConfigureAwait(false);
        if (metros.Count > 0)
        {
            return await CbsaScopeAsync(metros, cancellationToken).ConfigureAwait(false);
        }

        var counties = await MatchCountiesAsync(query, cancellationToken).ConfigureAwait(false);
        if (counties.Count > 0)
        {
            return await CountyMatchesScopeAsync(counties, cancellationToken).ConfigureAwait(false);
        }

        throw new GeographyNotFoundException($"No state, county, metro or ZIP matches '{query}'.");
    }

    private async Task<ResolvedGeography> StateScopeAsync(
        IReadOnlyList<UsState> states,
        CancellationToken cancellationToken)
    {
        var counties = await reference.GetCountiesAsync(cancellationToken).ConfigureAwait(false);
        var wanted = states.Select(state => state.Fips).ToHashSet(StringComparer.Ordinal);

        var fips = counties
            .Where(county => wanted.Contains(county.StateFips))
            .Select(county => county.Fips)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return new ResolvedGeography(
            GeoScopeTypes.State,
            string.Join(", ", states.Select(state => state.Name)),
            Cbsa: null,
            [.. states.Select(state => state.Abbreviation).Order(StringComparer.Ordinal)],
            fips,
            Zips: [],
            Radius: null,
            await BboxAsync(fips, cancellationToken).ConfigureAwait(false));
    }

    private async Task<ResolvedGeography> CountyValuesScopeAsync(
        IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        var chosen = new List<CountyRecord>();
        var alternatives = new List<CountyRecord>();

        foreach (var value in values)
        {
            var matches = await MatchCountiesAsync(value, cancellationToken).ConfigureAwait(false);
            if (matches.Count == 0)
            {
                throw new GeographyNotFoundException($"No county matches '{value}'.");
            }

            chosen.Add(matches[0]);

            // One name that means several counties is the ambiguous case; a list of names is not.
            if (values.Count == 1)
            {
                alternatives.AddRange(matches.Skip(1));
            }
        }

        return await CountyScopeAsync(chosen, alternatives, cancellationToken).ConfigureAwait(false);
    }

    private Task<ResolvedGeography> CountyMatchesScopeAsync(
        IReadOnlyList<CountyRecord> matches,
        CancellationToken cancellationToken) =>
        CountyScopeAsync([matches[0]], [.. matches.Skip(1)], cancellationToken);

    private async Task<ResolvedGeography> CountyScopeAsync(
        IReadOnlyList<CountyRecord> counties,
        IReadOnlyList<CountyRecord> alternatives,
        CancellationToken cancellationToken)
    {
        var fips = counties
            .Select(county => county.Fips)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return new ResolvedGeography(
            GeoScopeTypes.Counties,
            CountyLabel(counties),
            Cbsa: null,
            StatesOf(fips),
            fips,
            Zips: [],
            Radius: null,
            await BboxAsync(fips, cancellationToken).ConfigureAwait(false),
            Alternatives(alternatives.Select(county => new GeoAlternative(
                GeoScopeTypes.Counties,
                CountyLabel([county]),
                Cbsa: null,
                [county.Fips]))));
    }

    private async Task<ResolvedGeography> CbsaValuesScopeAsync(
        IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        // One name that means several metros is the ambiguous case, and keeps its alternatives. A list of
        // names is not ambiguous: every one of them was asked for, so they are unioned the way counties
        // are. Demoting all but the first to 'alternatives' would silently drop metros the user named.
        if (values.Count == 1)
        {
            return await CbsaScopeAsync(
                await MatchOrThrowAsync(values[0], cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }

        var chosen = new List<CbsaCandidate>();
        foreach (var value in values)
        {
            var matches = await MatchOrThrowAsync(value, cancellationToken).ConfigureAwait(false);
            chosen.Add(matches[0]);
        }

        return await CbsaUnionScopeAsync(chosen, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<CbsaCandidate>> MatchOrThrowAsync(
        string value,
        CancellationToken cancellationToken)
    {
        var matches = await MatchCbsasAsync(value, cancellationToken).ConfigureAwait(false);
        return matches.Count > 0
            ? matches
            : throw new GeographyNotFoundException($"No metro or micro area matches '{value}'.");
    }

    private async Task<ResolvedGeography> CbsaScopeAsync(
        IReadOnlyList<CbsaCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var chosen = candidates[0];

        return new ResolvedGeography(
            GeoScopeTypes.Cbsa,
            chosen.Title,
            chosen.Code,
            chosen.States,
            chosen.CountyFips,
            Zips: [],
            Radius: null,
            await BboxAsync(chosen.CountyFips, cancellationToken).ConfigureAwait(false),
            Alternatives(candidates.Skip(1).Select(candidate => new GeoAlternative(
                GeoScopeTypes.Cbsa,
                candidate.Title,
                candidate.Code,
                candidate.CountyFips))));
    }

    /// <summary>
    /// Several metros as one scope. <c>cbsa</c> is null because no single code describes the union, which
    /// is the same answer a county scope gives.
    /// </summary>
    private async Task<ResolvedGeography> CbsaUnionScopeAsync(
        IReadOnlyList<CbsaCandidate> areas,
        CancellationToken cancellationToken)
    {
        var fips = areas
            .SelectMany(area => area.CountyFips)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var states = areas
            .SelectMany(area => area.States)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return new ResolvedGeography(
            GeoScopeTypes.Cbsa,
            $"{areas.Count} metro areas",
            Cbsa: null,
            states,
            fips,
            Zips: [],
            Radius: null,
            await BboxAsync(fips, cancellationToken).ConfigureAwait(false));
    }

    private async Task<ResolvedGeography> ZipsScopeAsync(
        IReadOnlyList<string> zips,
        CancellationToken cancellationToken)
    {
        var rows = await reference.GetZctaCountiesAsync(cancellationToken).ConfigureAwait(false);
        var byZip = rows.ToLookup(row => row.Zcta5, StringComparer.Ordinal);

        var fips = new SortedSet<string>(StringComparer.Ordinal);
        var unknown = new List<string>();

        foreach (var zip in zips)
        {
            var counties = byZip[zip].Select(row => row.CountyFips).ToList();
            if (counties.Count == 0)
            {
                unknown.Add(zip);
                continue;
            }

            foreach (var county in counties)
            {
                fips.Add(county);
            }
        }

        if (fips.Count == 0)
        {
            throw new GeographyNotFoundException(
                $"No ZIP code tabulation area matches {string.Join(", ", unknown)}.");
        }

        var countyFips = fips.ToList();

        return new ResolvedGeography(
            GeoScopeTypes.Zips,
            zips.Count == 1 ? $"ZIP {zips[0]}" : $"{zips.Count} ZIP codes",
            Cbsa: null,
            StatesOf(countyFips),
            countyFips,
            zips,
            Radius: null,
            await BboxAsync(countyFips, cancellationToken).ConfigureAwait(false),
            Alternatives: null,
            // A typo in a 50-ZIP list should neither be swallowed nor sink the other 49.
            Warnings: unknown.Count == 0
                ? null
                : [$"No ZIP code tabulation area matches {string.Join(", ", unknown)}."]);
    }

    private async Task<ResolvedGeography> RadiusScopeAsync(
        GeoResolveRequest request,
        CancellationToken cancellationToken)
    {
        var miles = request.RadiusMiles
            ?? throw new GeographyRequestException("A radius scope needs radiusMiles.");

        if (miles <= 0 || double.IsNaN(miles))
        {
            throw new GeographyRequestException("radiusMiles has to be greater than zero.");
        }

        var (lat, lon, label) = await CenterAsync(request.Center, cancellationToken).ConfigureAwait(false);

        var counties = await reference
            .FindCountiesIntersectingAsync(GeoCircle.Boundary(lat, lon, miles), cancellationToken)
            .ConfigureAwait(false);

        return new ResolvedGeography(
            GeoScopeTypes.Radius,
            string.Create(CultureInfo.InvariantCulture, $"{miles:0.#} mi around {label}"),
            Cbsa: null,
            StatesOf(counties),
            [.. counties.Order(StringComparer.Ordinal)],
            Zips: [],
            new GeoRadius(lat, lon, miles),
            GeoCircle.Bounds(lat, lon, miles).ToArray());
    }

    private async Task<(double Lat, double Lon, string Label)> CenterAsync(
        GeoCenter? center,
        CancellationToken cancellationToken)
    {
        if (center is null)
        {
            throw new GeographyRequestException("A radius scope needs a center with either lat and lon or an address.");
        }

        if (center.Lat is { } lat && center.Lon is { } lon)
        {
            if (lat is < -90 or > 90 || lon is < -180 or > 180)
            {
                throw new GeographyRequestException("center.lat must be -90 to 90 and center.lon -180 to 180.");
            }

            return (lat, lon, string.Create(CultureInfo.InvariantCulture, $"{lat:0.####}, {lon:0.####}"));
        }

        if (string.IsNullOrWhiteSpace(center.Address))
        {
            throw new GeographyRequestException("A radius scope needs a center with either lat and lon or an address.");
        }

        // The address stays out of the message: an error message reaches the log through the tool filter,
        // and a street address is the one piece of personal data this chunk handles. The caller sent it,
        // so it does not need to be told back.
        var geocoded = await geocoder.GeocodeAsync(center.Address.Trim(), cancellationToken).ConfigureAwait(false)
            ?? throw new GeographyNotFoundException("The Census geocoder did not match that address.");

        return (geocoded.Lat, geocoded.Lon, geocoded.MatchedAddress);
    }

    private async Task<IReadOnlyList<CbsaCandidate>> MatchCbsasAsync(string query, CancellationToken cancellationToken)
    {
        var cleaned = WithoutMetroWords(query);
        if (cleaned.Length == 0)
        {
            return [];
        }

        var rows = await reference.GetCbsaCountiesAsync(cancellationToken).ConfigureAwait(false);
        var areas = rows
            .GroupBy(row => row.CbsaCode, StringComparer.Ordinal)
            .Select(group => new CbsaCandidate(
                group.Key,
                group.First().CbsaTitle,
                [
                    .. group
                        .Select(row => UsStates.Find(row.StateName)?.Abbreviation ?? row.StateName)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal),
                ],
                [.. group.Select(row => row.CountyFips).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]))
            .OrderBy(area => area.Code, StringComparer.Ordinal)
            .ToList();

        // An exact place name wins outright; only when nothing matches exactly is a prefix a guess worth
        // offering, and then every guess is an alternative.
        var exact = areas.Where(area => TitleMatches(area.Title, cleaned, exact: true)).ToList();

        return exact.Count > 0
            ? exact
            : [.. areas.Where(area => TitleMatches(area.Title, cleaned, exact: false))];
    }

    private async Task<IReadOnlyList<CountyRecord>> MatchCountiesAsync(
        string value,
        CancellationToken cancellationToken)
    {
        var counties = await reference.GetCountiesAsync(cancellationToken).ConfigureAwait(false);
        var trimmed = value.Trim();

        if (trimmed.Length == 5 && trimmed.All(char.IsAsciiDigit))
        {
            return [.. counties.Where(county => county.Fips == trimmed)];
        }

        var (name, state) = SplitCountyAndState(trimmed);
        if (name.Length == 0)
        {
            return [];
        }

        var candidates = counties.Where(county =>
            state is null || county.StateFips == state.Fips);

        var exact = candidates
            .Where(county => string.Equals(StripCountySuffix(county.Name), name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(county => county.Fips, StringComparer.Ordinal)
            .ToList();

        if (exact.Count > 0)
        {
            return exact;
        }

        return name.Length < MinimumPrefixLength
            ? []
            : [
                .. candidates
                    .Where(county => StripCountySuffix(county.Name).StartsWith(name, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(county => county.Fips, StringComparer.Ordinal),
            ];
    }

    private async Task<IReadOnlyList<double>> BboxAsync(
        IReadOnlyCollection<string> countyFips,
        CancellationToken cancellationToken)
    {
        if (countyFips.Count == 0)
        {
            return [];
        }

        var bounds = await reference.GetBoundsAsync(countyFips, cancellationToken).ConfigureAwait(false);
        return bounds?.ToArray() ?? [];
    }

    private static IReadOnlyList<GeoAlternative>? Alternatives(IEnumerable<GeoAlternative> alternatives)
    {
        var list = alternatives.ToList();
        return list.Count == 0 ? null : list;
    }

    private static IReadOnlyList<string> StatesOf(IEnumerable<string> countyFips) =>
    [
        .. countyFips
            .Where(fips => fips.Length >= 2)
            .Select(fips => UsStates.AbbreviationForFips(fips[..2]))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    private static string CountyLabel(IReadOnlyList<CountyRecord> counties)
    {
        if (counties.Count > 3)
        {
            var states = StatesOf(counties.Select(county => county.Fips));
            return $"{counties.Count} counties in {string.Join(", ", states)}";
        }

        return string.Join(
            ", ",
            counties.Select(county =>
                $"{StripCountySuffix(county.Name)} County, {UsStates.AbbreviationForFips(county.StateFips)}"));
    }

    private static bool NamesACounty(string query) =>
        _countySuffixes.Any(suffix => query.Contains(suffix, StringComparison.OrdinalIgnoreCase));

    private static (string Name, UsState? State) SplitCountyAndState(string value)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length > 1 && UsStates.Find(parts[^1]) is { } state)
        {
            return (StripCountySuffix(string.Join(", ", parts[..^1])), state);
        }

        return (StripCountySuffix(value), null);
    }

    private static string StripCountySuffix(string name)
    {
        var trimmed = name.Trim();

        foreach (var suffix in _countySuffixes)
        {
            if (trimmed.Length > suffix.Length
                && trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && char.IsWhiteSpace(trimmed[^(suffix.Length + 1)]))
            {
                return trimmed[..^suffix.Length].TrimEnd();
            }
        }

        return trimmed;
    }

    private static bool TitleMatches(string title, string query, bool exact)
    {
        if (string.Equals(title, query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var comma = title.IndexOf(',', StringComparison.Ordinal);
        var places = comma < 0 ? title : title[..comma];
        var stateSuffix = comma < 0 ? string.Empty : title[(comma + 1)..].Trim();

        foreach (var place in places.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (exact)
            {
                if (string.Equals(place, query, StringComparison.OrdinalIgnoreCase)
                    || string.Equals($"{place}, {stateSuffix}", query, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (query.Length >= MinimumPrefixLength
                && place.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string WithoutMetroWords(string query) => string.Join(
        ' ',
        query.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !_metroWords.Contains(word.Trim(',', '.'))));

    private static bool TryReadZips(string query, out IReadOnlyList<string> zips)
    {
        var parts = query.Split(
            [',', ';', ' '],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length > 0 && parts.All(part => part.Length == 5 && part.All(char.IsAsciiDigit)))
        {
            zips = [.. parts.Distinct(StringComparer.Ordinal)];
            return true;
        }

        zips = [];
        return false;
    }

    private static IReadOnlyList<string> ParseZips(IReadOnlyList<string> values)
    {
        var zips = new List<string>();
        foreach (var value in values)
        {
            if (!TryReadZips(value, out var parsed))
            {
                throw new GeographyRequestException($"'{value}' is not a 5-digit ZIP code.");
            }

            zips.AddRange(parsed);
        }

        return [.. zips.Distinct(StringComparer.Ordinal)];
    }

    private static IReadOnlyList<UsState> ParseStates(IReadOnlyList<string> values)
    {
        var states = new List<UsState>();
        foreach (var value in values)
        {
            states.Add(UsStates.Find(value)
                ?? throw new GeographyNotFoundException($"'{value}' is not a US state name or two-letter code."));
        }

        return [.. states.DistinctBy(state => state.Fips)];
    }

    /// <summary>
    /// The documented enum, plus the singular forms a caller reaches for: the schema says
    /// <c>counties</c>, but "county" is the word people type.
    /// </summary>
    private static string? NormalizeType(string? type)
    {
        var trimmed = Trimmed(type)?.ToLowerInvariant();

        return trimmed switch
        {
            null => null,
            "county" => GeoScopeTypes.Counties,
            "states" => GeoScopeTypes.State,
            "zip" or "zipcode" or "zipcodes" or "zip_codes" => GeoScopeTypes.Zips,
            "metro" or "msa" => GeoScopeTypes.Cbsa,
            _ => trimmed,
        };
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> Clean(IReadOnlyList<string>? values) =>
    [
        .. (values ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()),
    ];

    private sealed record CbsaCandidate(
        string Code,
        string Title,
        IReadOnlyList<string> States,
        IReadOnlyList<string> CountyFips);
}
