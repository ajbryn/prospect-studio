using ProspectStudio.Core.Candidates;
using ProspectStudio.Tests.Shared;
using Shouldly;

namespace ProspectStudio.Core.Tests.Candidates;

/// <summary>
/// Geohash and distance, the machinery behind technical-design §7.2's second and third dedupe rules.
/// </summary>
public class GeohashTests
{
    [Fact]
    public void Encode_MatchesTheCanonicalReferenceValue() =>
        // The standard worked example for geohash: 57.64911 N, 10.40744 E -> u4pruydqqvj.
        Geohash.Encode(57.64911, 10.40744, 11).ShouldBe("u4pruydqqvj");

    [Fact]
    public void Encode_TruncatesRatherThanRecomputing() =>
        // A shorter geohash is a prefix of a longer one for the same point; nothing about rule 2 works
        // if that is not true.
        Geohash.Encode(57.64911, 10.40744, 7).ShouldBe("u4pruyd");

    [Fact]
    public void Encode_DowntownHouston() =>
        Geohash.Encode(29.7604, -95.3698).ShouldBe("9vk1mc9");

    [Fact]
    public void The_geohash_pair_in_the_fixture_shares_a_cell()
    {
        // fx_0025 / fx_0113: the pair §7.2 rule 2 has to catch on its own - same name_norm, same
        // geohash-7 cell, 88 m apart, and neither has a website so the domain key cannot fire.
        var left = SamplePlaces.Row("fx_0025");
        var right = SamplePlaces.Row("fx_0113");

        Geohash.Encode(left.Lat, left.Lon).ShouldBe("9vk5s6h");
        Geohash.Encode(right.Lat, right.Lon).ShouldBe("9vk5s6h");
    }

    [Fact]
    public void The_close_pair_in_the_fixture_straddles_a_cell_boundary()
    {
        // fx_0007 / fx_0014 are 15 m apart and still land in different geohash-7 cells, because a
        // geohash is a grid rather than a radius. This is why §7.2 needs rule 3 at all, and why a test
        // for "same name within 150 m" must not be written as a geohash test alone.
        var left = SamplePlaces.Row("fx_0007");
        var right = SamplePlaces.Row("fx_0014");

        Geohash.Encode(left.Lat, left.Lon).ShouldBe("9vk11mq");
        Geohash.Encode(right.Lat, right.Lon).ShouldBe("9vk11mw");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(13)]
    public void Encode_RejectsAnUnusablePrecision(int precision) =>
        Should.Throw<ArgumentOutOfRangeException>(() => Geohash.Encode(29.7604, -95.3698, precision));

    [Fact]
    public void DistanceMeters_IsZeroForTheSamePoint() =>
        Geohash.DistanceMeters(29.7604, -95.3698, 29.7604, -95.3698).ShouldBe(0, 0.001);

    [Theory]
    // The fixture pairs, to a metre. The dedupe rules turn on these distances, so they are pinned.
    [InlineData("fx_0007", "fx_0014", 14.7)]
    [InlineData("fx_0025", "fx_0113", 88.2)]
    [InlineData("fx_0032", "fx_0045", 114.5)]
    [InlineData("fx_0017", "fx_0018", 1414.3)]
    [InlineData("fx_0001", "fx_0013", 2186.3)]
    [InlineData("fx_0104", "fx_0105", 21263.7)]
    [InlineData("fx_0065", "fx_0066", 60172.3)]
    public void DistanceMeters_MatchesTheFixtureGeometry(string left, string right, double expected)
    {
        var a = SamplePlaces.Row(left);
        var b = SamplePlaces.Row(right);

        Geohash.DistanceMeters(a.Lat, a.Lon, b.Lat, b.Lon).ShouldBe(expected, 1.0);
    }

    [Fact]
    public void DistanceMeters_IsSymmetric()
    {
        var a = SamplePlaces.Row("fx_0001");
        var b = SamplePlaces.Row("fx_0013");

        Geohash.DistanceMeters(a.Lat, a.Lon, b.Lat, b.Lon)
            .ShouldBe(Geohash.DistanceMeters(b.Lat, b.Lon, a.Lat, a.Lon), 0.001);
    }
}
