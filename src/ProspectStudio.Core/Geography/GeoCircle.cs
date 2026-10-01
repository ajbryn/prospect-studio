namespace ProspectStudio.Core.Geography;

/// <summary>
/// A radius in miles as a closed ring of lon/lat points, so the counties it touches can be found with a
/// plain polygon intersection. A circle on the ground is an ellipse in degrees - a degree of longitude
/// shrinks with the cosine of the latitude - and ignoring that would stretch the circle east-west by
/// 15% at the latitude of Houston.
/// </summary>
public static class GeoCircle
{
    /// <summary>Miles in a degree of latitude (WGS84 mean).</summary>
    public const double MilesPerDegreeLatitude = 69.0547;

    /// <summary>Enough vertices that the chord error stays well under a tenth of a mile.</summary>
    public const int DefaultSegments = 72;

    public static IReadOnlyList<GeoPoint> Boundary(double lat, double lon, double miles, int segments = DefaultSegments)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(segments, 8);

        var (deltaLat, deltaLon) = Deltas(lat, miles);
        var points = new List<GeoPoint>(segments + 1);

        for (var step = 0; step <= segments; step++)
        {
            var angle = step * 2 * Math.PI / segments;
            points.Add(new GeoPoint(lat + (deltaLat * Math.Sin(angle)), lon + (deltaLon * Math.Cos(angle))));
        }

        return points;
    }

    public static GeoBounds Bounds(double lat, double lon, double miles)
    {
        var (deltaLat, deltaLon) = Deltas(lat, miles);
        return new GeoBounds(lon - deltaLon, lat - deltaLat, lon + deltaLon, lat + deltaLat);
    }

    private static (double Lat, double Lon) Deltas(double lat, double miles)
    {
        var deltaLat = miles / MilesPerDegreeLatitude;
        var shrink = Math.Cos(lat * Math.PI / 180);

        // Near a pole the cosine goes to zero and the longitude delta would blow up; a half turn of
        // longitude already covers everything at that latitude.
        var deltaLon = shrink < 0.01 ? 180 : miles / (MilesPerDegreeLatitude * shrink);

        return (deltaLat, Math.Min(deltaLon, 180));
    }
}
