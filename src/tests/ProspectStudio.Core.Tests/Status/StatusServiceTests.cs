using ProspectStudio.Core.Configuration;
using ProspectStudio.Core.Status;
using Shouldly;

namespace ProspectStudio.Core.Tests.Status;

public class StatusServiceTests
{
    private static PsOptions Options(ApiKeys? keys = null, string? trackingBaseUrl = null) => new()
    {
        Home = @"C:\ws",
        Data = @"C:\data",
        TrackingBaseUrl = trackingBaseUrl ?? PsOptionsFactory.DefaultTrackingBaseUrl,
        UserAgent = PsOptionsFactory.DefaultUserAgent,
        OvertureRelease = PsOptionsFactory.DefaultOvertureRelease,
        Keys = keys ?? new ApiKeys(),
    };

    [Fact]
    public void Reports_version_and_both_paths()
    {
        var status = new StatusService(Options()).GetStatus();

        status.Version.ShouldBe(ProductVersion.Current);
        status.Version.Split('.').Length.ShouldBe(3);
        status.Home.ShouldBe(@"C:\ws");
        status.Data.ShouldBe(@"C:\data");
    }

    [Fact]
    public void Readiness_is_present_but_empty_until_data_is_prepared()
    {
        var ready = new StatusService(Options()).GetStatus().Ready;

        ready.ReferenceData.ShouldBeFalse();
        ready.BrandKit.ShouldBeFalse();
        ready.Overture.Release.ShouldBeNull();
        ready.Overture.States.ShouldBeEmpty();
        ready.Dealers.ShouldBe(0);
        ready.Territories.ShouldBe(0);
        ready.Suppression.ShouldBe(0);
    }

    [Fact]
    public void Keys_are_reported_as_booleans_only()
    {
        var keys = new ApiKeys { Census = "census-secret", GoogleMaps = "maps-secret" };

        var status = new StatusService(Options(keys)).GetStatus();

        status.Keys.Census.ShouldBeTrue();
        status.Keys.GoogleMaps.ShouldBeTrue();
        status.Keys.OpenAi.ShouldBeFalse();
        status.Keys.Gemini.ShouldBeFalse();
        status.Keys.HubSpot.ShouldBeFalse();
    }

    [Fact]
    public void Warns_about_a_missing_census_key_and_the_placeholder_tracking_url()
    {
        var status = new StatusService(Options()).GetStatus();

        status.Warnings.ShouldContain(w => w.Contains("CENSUS_API_KEY"));
        status.Warnings.ShouldContain(w => w.Contains("PS_TRACKING_BASE_URL"));
    }

    [Fact]
    public void Warns_about_nothing_once_the_key_and_url_are_configured()
    {
        var status = new StatusService(
            Options(new ApiKeys { Census = "census-secret" }, "https://lift.example/go?code={code}")).GetStatus();

        status.Warnings.ShouldBeEmpty();
    }
}
