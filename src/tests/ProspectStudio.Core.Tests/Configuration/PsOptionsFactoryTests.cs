using ProspectStudio.Core.Configuration;
using Shouldly;

namespace ProspectStudio.Core.Tests.Configuration;

public class PsOptionsFactoryTests
{
    private const string UserProfile = @"C:\Users\tester";
    private const string LocalAppData = @"C:\Users\tester\AppData\Local";

    private static Dictionary<string, string?> BaseEnvironment() => new(StringComparer.OrdinalIgnoreCase)
    {
        [PsOptionsFactory.UserProfileVariable] = UserProfile,
        [PsOptionsFactory.LocalAppDataVariable] = LocalAppData,
    };

    [Fact]
    public void Defaults_come_from_the_documented_values()
    {
        var options = PsOptionsFactory.Create(BaseEnvironment());

        options.Home.ShouldBe(Path.Combine(UserProfile, "Documents", "Prospect Studio"));
        options.Data.ShouldBe(Path.Combine(LocalAppData, "ProspectStudio"));
        options.TrackingBaseUrl.ShouldBe("https://example.com/lp?code={code}");
        options.UserAgent.ShouldBe("ProspectStudioBot/0.1 (+mailto:marketing@example.com)");
        options.OvertureRelease.ShouldBe("2026-09-23.1");
        options.OfferPrefix.ShouldBeNull();
        options.CbpYear.ShouldBeNull();
    }

    [Fact]
    public void Derived_paths_hang_off_the_data_folder()
    {
        var options = PsOptionsFactory.Create(BaseEnvironment());
        var data = Path.Combine(LocalAppData, "ProspectStudio");

        options.LogsDirectory.ShouldBe(Path.Combine(data, "logs"));
        options.CacheDirectory.ShouldBe(Path.Combine(data, "cache"));
        options.ReferenceDataDirectory.ShouldBe(Path.Combine(data, "refdata"));
        options.OvertureDirectory.ShouldBe(Path.Combine(data, "overture"));
        options.DatabasePath.ShouldBe(Path.Combine(data, "prospect.db"));
    }

    [Fact]
    public void No_key_is_configured_by_default()
    {
        var keys = PsOptionsFactory.Create(BaseEnvironment()).Keys;

        keys.HasCensus.ShouldBeFalse();
        keys.HasOpenAi.ShouldBeFalse();
        keys.HasGemini.ShouldBeFalse();
        keys.HasGoogleMaps.ShouldBeFalse();
        keys.HasHubSpot.ShouldBeFalse();
    }

    [Fact]
    public void Environment_values_override_every_default()
    {
        var environment = BaseEnvironment();
        environment[PsOptionsFactory.HomeVariable] = @"D:\Workspaces\Prospect";
        environment[PsOptionsFactory.DataVariable] = @"D:\Data\Prospect";
        environment[PsOptionsFactory.TrackingBaseUrlVariable] = "https://lift.example/go?code={code}";
        environment[PsOptionsFactory.OfferPrefixVariable] = "BOOM";
        environment[PsOptionsFactory.UserAgentVariable] = "ProspectStudioBot/9.9 (+mailto:andy@example.com)";
        environment[PsOptionsFactory.OvertureReleaseVariable] = "2026-11-01.0";
        environment[PsOptionsFactory.CbpYearVariable] = "2023";
        environment[PsOptionsFactory.CensusKeyVariable] = "census-secret";
        environment[PsOptionsFactory.OpenAiKeyVariable] = "openai-secret";
        environment[PsOptionsFactory.GeminiKeyVariable] = "gemini-secret";
        environment[PsOptionsFactory.GoogleMapsKeyVariable] = "maps-secret";
        environment[PsOptionsFactory.HubSpotKeyVariable] = "hubspot-secret";

        var options = PsOptionsFactory.Create(environment);

        options.Home.ShouldBe(@"D:\Workspaces\Prospect");
        options.Data.ShouldBe(@"D:\Data\Prospect");
        options.TrackingBaseUrl.ShouldBe("https://lift.example/go?code={code}");
        options.OfferPrefix.ShouldBe("BOOM");
        options.UserAgent.ShouldBe("ProspectStudioBot/9.9 (+mailto:andy@example.com)");
        options.OvertureRelease.ShouldBe("2026-11-01.0");
        options.CbpYear.ShouldBe(2023);
        options.Keys.HasCensus.ShouldBeTrue();
        options.Keys.HasOpenAi.ShouldBeTrue();
        options.Keys.HasGemini.ShouldBeTrue();
        options.Keys.HasGoogleMaps.ShouldBeTrue();
        options.Keys.HasHubSpot.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_values_fall_back_to_the_default(string blank)
    {
        var environment = BaseEnvironment();
        environment[PsOptionsFactory.HomeVariable] = blank;
        environment[PsOptionsFactory.TrackingBaseUrlVariable] = blank;
        environment[PsOptionsFactory.CensusKeyVariable] = blank;

        var options = PsOptionsFactory.Create(environment);

        options.Home.ShouldBe(Path.Combine(UserProfile, "Documents", "Prospect Studio"));
        options.TrackingBaseUrl.ShouldBe("https://example.com/lp?code={code}");
        options.Keys.HasCensus.ShouldBeFalse();
    }

    [Fact]
    public void Values_are_trimmed()
    {
        var environment = BaseEnvironment();
        environment[PsOptionsFactory.HomeVariable] = "  D:\\Workspaces\\Prospect  ";

        PsOptionsFactory.Create(environment).Home.ShouldBe(@"D:\Workspaces\Prospect");
    }

    [Theory]
    [InlineData("not-a-year")]
    [InlineData("42")]
    [InlineData("")]
    public void An_unusable_cbp_year_is_ignored(string raw)
    {
        var environment = BaseEnvironment();
        environment[PsOptionsFactory.CbpYearVariable] = raw;

        PsOptionsFactory.Create(environment).CbpYear.ShouldBeNull();
    }

    [Fact]
    public void Api_keys_are_never_printed()
    {
        var environment = BaseEnvironment();
        environment[PsOptionsFactory.CensusKeyVariable] = "census-secret";

        var text = PsOptionsFactory.Create(environment).ToString();

        text.ShouldNotContain("census-secret");
        text.ShouldContain("census = True");
    }
}
