using ProspectStudio.Core.Configuration;
using ProspectStudio.Core.Market;
using ProspectStudio.Infrastructure.Census;
using ProspectStudio.Infrastructure.Tests.Infrastructure;
using ProspectStudio.Tests;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Infrastructure.Tests.Network;

/// <summary>
/// Two opt-in live checks that the CBP API still behaves the way every recorded fixture assumes.
/// Skipped unless <c>PS_RUN_NETWORK_TESTS=1</c>, and the data query is skipped again without
/// <c>CENSUS_API_KEY</c>, because a missing key is not a failure of this code.
/// </summary>
/// <remarks>
/// Deliberately few: the fixtures are what the suite runs on, and these exist only to notice the day
/// the API changes under them.
/// </remarks>
[Trait("Category", "Network")]
public class CensusCbpNetworkTests
{
    [NetworkFact]
    public async Task The_latest_published_cbp_year_is_still_discoverable()
    {
        // Metadata answers unkeyed, so this runs without a key - which is also why the year probe uses it.
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        using var client = new CensusCbpClient(Options(directory.Path, key: null), TimeProvider.System);

        var year = await client.GetYearAsync(timeout.Token);

        year.ShouldBeGreaterThanOrEqualTo(
            CbpFixtures.LatestCbpYear,
            $"C3 verified {CbpFixtures.LatestCbpYear} as the newest CBP vintage. A lower number means the "
            + "probe broke; a higher one means a new release, so re-record the fixtures.");
    }

    [NetworkFact(PsOptionsFactory.CensusKeyVariable)]
    public async Task Harris_county_warehousing_still_comes_back_with_establishments()
    {
        using var timeout = Deadline();
        using var directory = new TempDirectory();
        using var client = new CensusCbpClient(
            Options(directory.Path, Environment.GetEnvironmentVariable(PsOptionsFactory.CensusKeyVariable)),
            TimeProvider.System);

        var rows = await client.GetEstablishmentsAsync(["4931"], [CbpFixtures.HarrisCountyFips], timeout.Token);

        rows.ShouldNotBeEmpty();
        rows.ShouldAllBe(row => row.CountyFips == CbpFixtures.HarrisCountyFips);

        var total = rows
            .Where(row => row.SizeBandCode == CbpSizeBands.AllEstablishmentsCode)
            .Sum(row => row.Establishments);
        total.ShouldBeGreaterThan(0, "Harris County has warehouses.");

        var bands = rows
            .Where(row => row.SizeBandCode != CbpSizeBands.AllEstablishmentsCode)
            .Select(row => CbpSizeBands.Parse(row.SizeBandCode, row.SizeBandLabel))
            .ToList();

        bands.ShouldNotBeEmpty("the size bands are what withMinEmployees is built from.");
        bands.ShouldContain(
            band => band.MinEmployees >= 20,
            "at least one band has to parse to a bound of 20 or more, or the EMPSZES_LABEL wording changed.");
    }

    private static PsOptions Options(string data, string? key) =>
        PsOptionsFactory.Create(new Dictionary<string, string?>
        {
            [PsOptionsFactory.HomeVariable] = Path.Combine(data, "home"),
            [PsOptionsFactory.DataVariable] = data,
            [PsOptionsFactory.CensusKeyVariable] = key,
        });

    private static CancellationTokenSource Deadline() => new(TimeSpan.FromMinutes(2));
}
