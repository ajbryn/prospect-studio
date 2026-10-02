using System.Globalization;
using ProspectStudio.Core.Geography;
using ProspectStudio.Core.Naics;

namespace ProspectStudio.Core.Market;

/// <summary>
/// The rules behind <c>estimate_market</c> (mcp-tools.md §estimate_market, implementation-plan C3):
/// sum establishments by NAICS code and in total, sum the size bands at or above a threshold, and say
/// in <c>notes</c> wherever the answer is a lower bound.
/// </summary>
/// <remarks>
/// <para>
/// Suppression in CBP is <strong>absent rows</strong>, not blanked values. Band rows vanish while the
/// <c>001</c> row stays, so the gap is <c>001</c> minus the band sum; whole counties vanish from the
/// response, which means <em>unknown</em> and never zero. A value check finds nothing and reports
/// suppressed data as complete, which is the worst possible failure for a go/no-go number.
/// </para>
/// <para>
/// <see cref="IGeographyReference"/> is here only to turn a missing county's FIPS into its name, so a
/// note can say "Liberty and San Jacinto" instead of "48291, 48407".
/// </para>
/// </remarks>
public sealed class MarketSizingService(ICbpDataSource cbp, IGeographyReference geography, INaicsCatalog naics)
{
    private readonly ICbpDataSource _cbp = cbp;
    private readonly IGeographyReference _geography = geography;
    private readonly INaicsCatalog _naics = naics;

    public async Task<MarketEstimate> EstimateAsync(MarketEstimateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requested = RequestedCodes(request.Naics);
        var counties = RequestedCounties(request.Geography);
        var codes = NaicsCodeSet.DropDescendants(requested);

        // The rows come first so that a missing key is reported before anything is requested; the vintage
        // is resolved inside the data call, so asking for the year afterwards is a cached read.
        var rows = await _cbp.GetEstablishmentsAsync(codes, counties, cancellationToken).ConfigureAwait(false);
        var year = await _cbp.GetYearAsync(cancellationToken).ConfigureAwait(false);
        var titles = await TitlesAsync(cancellationToken).ConfigureAwait(false);

        var byNaics = codes.Select(code => Measure(code, rows, counties, request.MinEmployees)).ToList();

        return new MarketEstimate(
            year,
            request.Geography.Label,
            [.. byNaics.Select(measured => new NaicsMarketEstimate(
                measured.Naics,
                titles.TryGetValue(measured.Naics, out var title) ? title : measured.Naics,
                measured.Establishments,
                measured.WithMinEmployees))],
            new MarketTotals(
                byNaics.Sum(measured => measured.Establishments),
                // Null when no threshold was asked for, and also when no code could answer it: a total of
                // 0 beside per-code nulls would be a confident zero the bands never supported.
                request.MinEmployees is null || byNaics.All(measured => measured.WithMinEmployees is null)
                    ? null
                    : byNaics.Sum(measured => measured.WithMinEmployees ?? 0)),
            await NotesAsync(request, requested, codes, byNaics, counties, cancellationToken).ConfigureAwait(false));
    }

    private static IReadOnlyList<string> RequestedCodes(IReadOnlyList<string> naics)
    {
        if (naics is null || naics.Count == 0)
        {
            throw new MarketRequestException(
                "Pass at least one NAICS code. Use lookup_naics to turn a description such as "
                + "'electrical contractor' into a code.");
        }

        List<string> codes = [];
        foreach (var raw in naics)
        {
            var code = raw?.Trim() ?? string.Empty;
            if (code.Length is < 2 or > 6 || !code.All(char.IsAsciiDigit))
            {
                throw new MarketRequestException(
                    $"'{raw}' is not a NAICS code: a code is 2 to 6 digits, such as '4931' or '238210'.");
            }

            codes.Add(code);
        }

        return codes;
    }

    /// <summary>
    /// The counties to size, which is always <see cref="ResolvedGeography.CountyFips"/>: a multi-metro
    /// union deliberately has a null <c>cbsa</c> while its counties are all present.
    /// </summary>
    private static IReadOnlyList<string> RequestedCounties(ResolvedGeography scope)
    {
        if (scope is null)
        {
            throw new MarketRequestException("Pass a geography: a place name, or a scope with counties.");
        }

        var counties = scope.CountyFips?
            .Where(fips => !string.IsNullOrWhiteSpace(fips))
            .Select(fips => fips.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];

        return counties.Count > 0
            ? counties
            : throw new MarketRequestException(
                $"'{scope.Label}' resolves to no counties, so there is no market to size. CBP is published "
                + "per county; resolve a state, metro area, county list or ZIP list instead.");
    }

    private static MeasuredNaics Measure(
        string code,
        IReadOnlyList<CbpEstablishmentRow> rows,
        IReadOnlyList<string> counties,
        int? minEmployees)
    {
        var forCode = rows.Where(row => string.Equals(row.Naics, code, StringComparison.Ordinal)).ToList();
        var bandRows = forCode.Where(row => row.SizeBandCode != CbpSizeBands.AllEstablishmentsCode).ToList();

        // Never add 001 to the bands: it is their sum exactly, so counting both doubles the market.
        var establishments = forCode
            .Where(row => row.SizeBandCode == CbpSizeBands.AllEstablishmentsCode)
            .Sum(row => row.Establishments);

        var answered = forCode.Select(row => row.CountyFips).ToHashSet(StringComparer.Ordinal);
        var absent = counties.Where(fips => !answered.Contains(fips)).ToList();

        // Only the bands that partition the total: a detail band nested inside a broader one (263 inside
        // 260) would otherwise be counted twice, which both inflates the sum and shrinks the gap.
        var bands = bandRows
            .GroupBy(row => row.SizeBandCode, StringComparer.Ordinal)
            .Select(group => CbpSizeBands.Parse(group.Key, group.First().SizeBandLabel))
            .ToList();
        var partition = CbpSizeBands.Partition(bands);
        var counted = partition.Select(band => band.Code).ToHashSet(StringComparer.Ordinal);
        var banded = bandRows.Where(row => counted.Contains(row.SizeBandCode)).Sum(row => row.Establishments);

        // No clamp: a band sum above the total is impossible for disjoint bands, so it means the band
        // taxonomy changed under us and every band-derived number is wrong, not merely imprecise.
        if (banded > establishments)
        {
            throw new MarketExternalException(
                $"CBP reports {banded} establishments across the size bands of NAICS {code} but only "
                + $"{establishments} in total, which is impossible: the published band set "
                + $"({string.Join(", ", counted.Order(StringComparer.Ordinal))}) is no longer a partition, "
                + "so no size breakdown from this response can be trusted.");
        }

        SizeBandSelection? selection = null;
        int? withMinEmployees = null;
        string? unreachable = null;
        if (minEmployees is int threshold)
        {
            selection = CbpSizeBands.Select(bands, threshold);

            if (selection.Codes.Count > 0)
            {
                var selected = selection.Codes.ToHashSet(StringComparer.Ordinal);
                withMinEmployees = bandRows
                    .Where(row => selected.Contains(row.SizeBandCode))
                    .Sum(row => row.Establishments);
            }
            else if (partition.Count > 0)
            {
                // The threshold is past the top band, which is open-ended, so there is no next edge to
                // round up to: null, and a note naming the band those establishments are hiding in.
                unreachable = partition[^1].Label;
            }

            // No bands published at all leaves withMinEmployees null too - one spelling of "could not be
            // computed" - and the suppression note says the bands are missing.
        }

        return new MeasuredNaics(
            code,
            establishments,
            withMinEmployees,
            establishments - banded,
            absent,
            selection,
            unreachable);
    }

    private async Task<IReadOnlyList<string>> NotesAsync(
        MarketEstimateRequest request,
        IReadOnlyList<string> requested,
        IReadOnlyList<string> codes,
        IReadOnlyList<MeasuredNaics> measured,
        IReadOnlyList<string> counties,
        CancellationToken cancellationToken)
    {
        var minEmployees = request.MinEmployees;
        List<string> notes = [];

        if (GranularityNote(request.Geography.Type, counties.Count) is { } granularity)
        {
            notes.Add(granularity);
        }

        foreach (var dropped in requested.Where(code => !codes.Contains(code, StringComparer.Ordinal)))
        {
            notes.Add(
                $"NAICS {dropped} was dropped because {NaicsCodeSet.AncestorOf(dropped, codes)} already "
                + "includes it: a CBP code counts its descendants, so adding both would double-count.");
        }

        if (minEmployees is int threshold && measured.Any(code => code.Selection?.RoundedUp == true))
        {
            var edges = measured
                .Where(code => code.Selection?.RoundedUp == true)
                .Select(code => code.Selection!.EffectiveMinEmployees)
                .Distinct()
                .Order()
                .ToList();

            notes.Add(
                $"minEmployees {threshold} falls inside a size band, so the next band edge up was used "
                + $"instead: {string.Join(", ", edges.Select(edge => edge.ToString(CultureInfo.InvariantCulture)))}.");
        }

        if (minEmployees is int wanted && measured.Any(code => code.UnreachableTopBand is not null))
        {
            var tops = measured
                .Select(code => code.UnreachableTopBand)
                .Where(label => label is not null)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            notes.Add(
                $"minEmployees {wanted} is above every size band CBP publishes here - the largest is "
                + $"'{string.Join("', '", tops)}' - so withMinEmployees is null rather than 0: establishments "
                + "that large are inside that band and cannot be counted separately.");
        }

        var names = measured.Any(code => code.AbsentCounties.Count > 0)
            ? await CountyNamesAsync(cancellationToken).ConfigureAwait(false)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var code in measured)
        {
            if (SuppressionNote(code, minEmployees, names) is { } note)
            {
                notes.Add(note);
            }
        }

        return notes;
    }

    /// <summary>
    /// What a ZIP or radius scope really counts, or null when the scope is already county-shaped. CBP has
    /// no sub-county geography, so <c>resolve_geography</c>'s "counties this ZIP touches" becomes "every
    /// establishment in those counties" - an order of magnitude more than the ZIP holds, and otherwise
    /// presented as a precise number.
    /// </summary>
    private static string? GranularityNote(string type, int counties) =>
        type switch
        {
            GeoScopeTypes.Zips =>
                $"CBP is published per county, so this counts the {counties} "
                + $"{(counties == 1 ? "county" : "counties")} these ZIPs fall in, not the ZIPs themselves.",
            GeoScopeTypes.Radius =>
                $"CBP is published per county, so this counts the {counties} "
                + $"{(counties == 1 ? "county" : "counties")} the radius touches, not the circle itself.",
            _ => null,
        };

    /// <summary>
    /// The hedge on one NAICS code's numbers, or null when nothing about it was suppressed. A note on a
    /// complete answer teaches the reader to ignore notes, which is how the real warning gets missed.
    /// </summary>
    private static string? SuppressionNote(
        MeasuredNaics measured,
        int? minEmployees,
        IReadOnlyDictionary<string, string> countyNames)
    {
        List<string> parts = [];

        // A band gap only moves withMinEmployees; an absent county makes even the total a lower bound.
        if (measured.UnbandedEstablishments > 0 && minEmployees is not null)
        {
            parts.Add(
                $"{measured.UnbandedEstablishments} of {measured.Establishments} establishments have no "
                + "size band published");
        }

        if (measured.AbsentCounties.Count > 0)
        {
            var names = measured.AbsentCounties
                .Select(fips => countyNames.TryGetValue(fips, out var name) ? name : fips)
                .Select(name => name.EndsWith(" County", StringComparison.Ordinal) ? name[..^" County".Length] : name)
                .ToList();

            parts.Add(
                names.Count == 1
                    ? $"{names[0]} County is absent entirely"
                    : $"{Join(names)} counties are absent entirely");
        }

        if (parts.Count == 0)
        {
            return null;
        }

        // An absent county is missing from the total itself, so establishments is hedged whether or not a
        // threshold was asked for - a confident zero total is the one answer this must never give.
        var hedged = (measured.AbsentCounties.Count > 0, minEmployees is not null) switch
        {
            (true, true) => "establishments and withMinEmployees are both lower bounds",
            (true, false) => "establishments is a lower bound",
            _ => "withMinEmployees is a lower bound",
        };

        return $"{measured.Naics}: {string.Join(", and ", parts)}, so {hedged}.";
    }

    private static string Join(IReadOnlyList<string> names) =>
        names.Count switch
        {
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
        };

    private async Task<Dictionary<string, string>> TitlesAsync(CancellationToken cancellationToken)
    {
        var entries = await _naics.GetEntriesAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, string> titles = new(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            titles.TryAdd(entry.Code, entry.Title);
        }

        return titles;
    }

    private async Task<Dictionary<string, string>> CountyNamesAsync(CancellationToken cancellationToken)
    {
        var counties = await _geography.GetCountiesAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, string> names = new(StringComparer.Ordinal);
        foreach (var county in counties)
        {
            names.TryAdd(county.Fips, county.Name);
        }

        return names;
    }

    /// <param name="UnbandedEstablishments">
    /// <c>001</c> minus the bands that partition it: establishments whose size class was suppressed by
    /// leaving its row out, which is the only way CBP suppresses.
    /// </param>
    /// <param name="UnreachableTopBand">
    /// The label of the largest published band when the threshold is above all of them, which is why
    /// <paramref name="WithMinEmployees"/> is null rather than 0.
    /// </param>
    private sealed record MeasuredNaics(
        string Naics,
        int Establishments,
        int? WithMinEmployees,
        int UnbandedEstablishments,
        IReadOnlyList<string> AbsentCounties,
        SizeBandSelection? Selection,
        string? UnreachableTopBand);
}
