using System.Text;

namespace ProspectStudio.Core.Candidates;

/// <summary>
/// Geohash encoding, for the second dedupe key of technical-design §7.2 (<c>name_norm</c> plus
/// geohash-7). A geohash-7 cell is roughly 153 m by 153 m, which is the "within 150 m" the C4 test
/// list refers to - but it is a <em>grid</em>, so two points a few metres apart can still sit in
/// different cells. §7.2's rule 3 (Jaro-Winkler within 200 m) is what covers that case.
/// </summary>
public static class Geohash
{
    /// <summary>The precision §7.2 rule 2 uses.</summary>
    public const int DefaultPrecision = 7;

    /// <summary>The geohash alphabet (base 32, no <c>a</c>, <c>i</c>, <c>l</c> or <c>o</c>).</summary>
    public const string Alphabet = "0123456789bcdefghjkmnpqrstuvwxyz";

    /// <summary>The longest geohash this encoder produces; beyond it a double runs out of precision.</summary>
    public const int MaxPrecision = 12;

    /// <summary>The WGS84 mean radius, in metres.</summary>
    private const double EarthRadiusMeters = 6_371_008.8;

    public static string Encode(double lat, double lon, int precision = DefaultPrecision)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(precision, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(precision, MaxPrecision);

        var minLat = -90.0;
        var maxLat = 90.0;
        var minLon = -180.0;
        var maxLon = 180.0;

        var geohash = new StringBuilder(precision);
        var even = true;
        var bit = 0;
        var value = 0;

        while (geohash.Length < precision)
        {
            if (even)
            {
                var middle = (minLon + maxLon) / 2;
                if (lon >= middle)
                {
                    value = (value << 1) | 1;
                    minLon = middle;
                }
                else
                {
                    value <<= 1;
                    maxLon = middle;
                }
            }
            else
            {
                var middle = (minLat + maxLat) / 2;
                if (lat >= middle)
                {
                    value = (value << 1) | 1;
                    minLat = middle;
                }
                else
                {
                    value <<= 1;
                    maxLat = middle;
                }
            }

            even = !even;

            if (++bit == 5)
            {
                geohash.Append(Alphabet[value]);
                bit = 0;
                value = 0;
            }
        }

        return geohash.ToString();
    }

    /// <summary>
    /// Great-circle distance in metres, for §7.2 rule 3's 200 m window and §7.6's proximity feature.
    /// </summary>
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = double.DegreesToRadians(lat1);
        var phi2 = double.DegreesToRadians(lat2);
        var deltaPhi = phi2 - phi1;
        var deltaLambda = double.DegreesToRadians(lon2 - lon1);

        var haversine = (Math.Sin(deltaPhi / 2) * Math.Sin(deltaPhi / 2))
            + (Math.Cos(phi1) * Math.Cos(phi2) * Math.Sin(deltaLambda / 2) * Math.Sin(deltaLambda / 2));

        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1.0, Math.Sqrt(haversine)));
    }
}
