using ProspectStudio.Core.Geography;
using ProspectStudio.Core.Market;
using ProspectStudio.Core.Naics;
using ProspectStudio.Tests.Shared;

namespace ProspectStudio.Core.Tests.Market;

/// <summary>
/// Hands the service rows parsed out of the recorded CBP bodies, and records what it was asked for -
/// which is how the NAICS-overlap and county-scope tests check the request rather than only the answer.
/// </summary>
internal sealed class FakeCbpDataSource(int year = CbpFixtures.LatestCbpYear) : ICbpDataSource
{
    private readonly Dictionary<string, string> _bodies = new(StringComparer.Ordinal);

    public List<IReadOnlyList<string>> NaicsAsked { get; } = [];

    public List<IReadOnlyList<string>> CountiesAsked { get; } = [];

    public int Calls => NaicsAsked.Count;

    public int YearCalls { get; private set; }

    /// <summary>Registers a recorded response body as the rows published for one NAICS code.</summary>
    public FakeCbpDataSource With(string naics, string body)
    {
        _bodies[naics] = body;
        return this;
    }

    public static FakeCbpDataSource Houston() => new FakeCbpDataSource()
        .With("4931", CbpFixtures.Houston4931)
        .With("238210", CbpFixtures.Houston238210);

    public static FakeCbpDataSource Harris() => new FakeCbpDataSource()
        .With("4931", CbpFixtures.Harris4931)
        .With("49311", CbpFixtures.Harris49311)
        .With("238210", CbpFixtures.Harris238210);

    public Task<int> GetYearAsync(CancellationToken cancellationToken)
    {
        YearCalls++;
        return Task.FromResult(year);
    }

    public Task<IReadOnlyList<CbpEstablishmentRow>> GetEstablishmentsAsync(
        IReadOnlyList<string> naicsCodes,
        IReadOnlyList<string> countyFips,
        CancellationToken cancellationToken)
    {
        NaicsAsked.Add([.. naicsCodes]);
        CountiesAsked.Add([.. countyFips]);

        var wanted = countyFips.ToHashSet(StringComparer.Ordinal);
        List<CbpEstablishmentRow> rows = [];

        foreach (var code in naicsCodes)
        {
            if (!_bodies.TryGetValue(code, out var body))
            {
                throw new InvalidOperationException(
                    $"No recorded CBP body for NAICS '{code}'. The test registered: "
                    + $"{string.Join(", ", _bodies.Keys)}.");
            }

            // Only what the caller's counties cover, exactly as a real per-state query would return.
            rows.AddRange(CbpTable.Parse(body).Where(row => wanted.Contains(row.CountyFips)));
        }

        return Task.FromResult<IReadOnlyList<CbpEstablishmentRow>>(rows);
    }
}

/// <summary>
/// A response whose bands add up to <em>more</em> than its <c>001</c> total: 150 banded against a
/// published 100. That is impossible for disjoint bands, so it can only mean the band selection is wrong
/// — and clamping the gap to zero turns it into a clean-looking answer with nothing suppressed.
/// </summary>
/// <remarks>
/// Hand-built on purpose. Once the containment rule is right no recorded body produces this, which is
/// why the defence cannot be driven from a fixture; the labels are the real ones so the rows are the
/// shape a future vintage's extra nested band would arrive in.
/// </remarks>
internal sealed class ContradictoryCbpSource : ICbpDataSource
{
    public Task<int> GetYearAsync(CancellationToken cancellationToken) =>
        Task.FromResult(CbpFixtures.LatestCbpYear);

    public Task<IReadOnlyList<CbpEstablishmentRow>> GetEstablishmentsAsync(
        IReadOnlyList<string> naicsCodes,
        IReadOnlyList<string> countyFips,
        CancellationToken cancellationToken)
    {
        var naics = naicsCodes.Count > 0 ? naicsCodes[0] : "4931";
        var county = countyFips.Count > 0 ? countyFips[0] : CbpFixtures.HarrisCountyFips;

        return Task.FromResult<IReadOnlyList<CbpEstablishmentRow>>(
        [
            new(naics, county, CbpSizeBands.AllEstablishmentsCode, "All establishments", 100),
            new(naics, county, "241", "Establishments with 20 to 49 employees", 90),
            new(naics, county, "242", "Establishments with 50 to 99 employees", 60),
        ]);
    }
}

/// <summary>
/// County names, so a suppression note can say "Liberty and San Jacinto" rather than "48291, 48407".
/// Only the county list is implemented: market sizing works from county FIPS and has no business asking
/// for geometry, so the geometry members refuse rather than returning something plausible.
/// </summary>
/// <remarks>
/// The names are the Census spellings, as <c>src/tests/Fixtures/geo/cbsa_excerpt.csv</c> has them, and
/// the ten counties are CBSA 26420 - including San Jacinto 48407, which the spec used to omit.
/// </remarks>
internal sealed class FakeGeographyReference : IGeographyReference
{
    private static readonly List<CountyRecord> _counties =
    [
        new("48015", "Austin County", "48"),
        new("48039", "Brazoria County", "48"),
        new("48071", "Chambers County", "48"),
        new("48157", "Fort Bend County", "48"),
        new("48167", "Galveston County", "48"),
        new("48201", "Harris County", "48"),
        new("48291", "Liberty County", "48"),
        new("48339", "Montgomery County", "48"),
        new("48407", "San Jacinto County", "48"),
        new("48473", "Waller County", "48"),
        new("48245", "Jefferson County", "48"),
        new("22071", "Orleans Parish", "22"),
    ];

    public bool IsReady => true;

    public Task<IReadOnlyList<CountyRecord>> GetCountiesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CountyRecord>>(_counties);

    public Task<IReadOnlyList<CbsaCountyRecord>> GetCbsaCountiesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CbsaCountyRecord>>(
        [
            .. _counties
                .Where(county => county.Fips != "48245" && county.Fips != "22071")
                .Select(county => new CbsaCountyRecord(
                    "26420",
                    "Houston-Pasadena-The Woodlands, TX",
                    "Metropolitan Statistical Area",
                    county.Fips,
                    county.Name,
                    "Texas",
                    "Outlying")),
        ]);

    public Task<IReadOnlyList<ZctaCountyRecord>> GetZctaCountiesAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("Market sizing works from county FIPS, so it never needs ZCTAs.");

    public Task<GeoBounds?> GetBoundsAsync(
        IReadOnlyCollection<string> countyFips,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Market sizing never needs geometry.");

    public Task<IReadOnlyList<string>> FindCountiesIntersectingAsync(
        IReadOnlyList<GeoPoint> boundary,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Market sizing never needs geometry.");
}

/// <summary>The NAICS titles mcp-tools.md §estimate_market shows, straight from <c>naics2022.csv</c>.</summary>
internal sealed class FakeNaicsCatalog : INaicsCatalog
{
    private static readonly List<NaicsEntry> _entries =
    [
        new("493", "Warehousing and Storage", 3),
        new("4931", "Warehousing and Storage", 4),
        new("49311", "General Warehousing and Storage", 5),
        new("493110", "General Warehousing and Storage", 6),
        new("23821", "Electrical Contractors and Other Wiring Installation Contractors", 5),
        new("238210", "Electrical Contractors and Other Wiring Installation Contractors", 6),
    ];

    public Task<IReadOnlyList<NaicsEntry>> GetEntriesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NaicsEntry>>(_entries);
}
