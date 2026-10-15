using Geotagger.Core.Geo;
using Geotagger.Core.Gpx;

namespace Geotagger.Core.Matching;

public enum TrackMatchKind
{
    /// <summary>The photo was taken between two nearby fixes; position is interpolated.</summary>
    Interpolated,

    /// <summary>The photo was taken near (but not between close) fixes; the nearest fix is used.</summary>
    NearestPoint,
}

public readonly record struct TrackMatch(GeoPoint Location, TrackMatchKind Kind, TimeSpan TimeToNearestFix, DateTimeOffset PhotoTimeUtc);

/// <summary>
/// All timed track points from one or more GPX files, merged and sorted by time, ready for
/// matching photo timestamps to positions.
/// </summary>
public sealed class TrackLog
{
    private readonly GpxTrackPoint[] _points;
    private readonly long[] _ticks;

    public TrackLog(IEnumerable<GpxTrackPoint> points)
    {
        _points = points.Where(p => p.Time.HasValue).OrderBy(p => p.Time!.Value.UtcTicks).ToArray();
        _ticks = _points.Select(p => p.Time!.Value.UtcTicks).ToArray();
    }

    public static TrackLog FromFiles(IEnumerable<GpxFile> files) => new(files.SelectMany(f => f.AllTrackPoints));

    public int Count => _points.Length;

    public DateTimeOffset? Start => _points.Length > 0 ? _points[0].Time : null;

    public DateTimeOffset? End => _points.Length > 0 ? _points[^1].Time : null;

    /// <summary>
    /// Finds the position at <paramref name="photoTimeUtc"/>.
    /// If the photo falls between two fixes that are at most 2×<paramref name="tolerance"/> apart the
    /// position is linearly interpolated; otherwise the nearest fix is used if it is within
    /// <paramref name="tolerance"/>. Returns null when no fix is close enough.
    /// </summary>
    public TrackMatch? Match(DateTimeOffset photoTimeUtc, TimeSpan tolerance)
    {
        if (_points.Length == 0) return null;
        long t = photoTimeUtc.UtcTicks;

        // Index of the first point strictly after t.
        int hi = Array.BinarySearch(_ticks, t);
        if (hi >= 0)
        {
            // Exact hit (take the last of any duplicates for determinism).
            while (hi + 1 < _ticks.Length && _ticks[hi + 1] == t) hi++;
            return new TrackMatch(_points[hi].ToGeoPoint(), TrackMatchKind.Interpolated, TimeSpan.Zero, photoTimeUtc);
        }
        hi = ~hi;
        int lo = hi - 1;

        if (lo >= 0 && hi < _points.Length)
        {
            var a = _points[lo];
            var b = _points[hi];
            long gap = _ticks[hi] - _ticks[lo];
            var toA = TimeSpan.FromTicks(t - _ticks[lo]);
            var toB = TimeSpan.FromTicks(_ticks[hi] - t);
            var nearest = toA <= toB ? toA : toB;

            if (gap <= 2 * tolerance.Ticks)
            {
                double f = (double)(t - _ticks[lo]) / gap;
                return new TrackMatch(Interpolate(a, b, f), TrackMatchKind.Interpolated, nearest, photoTimeUtc);
            }
            if (nearest <= tolerance)
            {
                var p = toA <= toB ? a : b;
                return new TrackMatch(p.ToGeoPoint(), TrackMatchKind.NearestPoint, nearest, photoTimeUtc);
            }
            return null;
        }

        // Before the first fix or after the last one.
        var edge = lo < 0 ? _points[0] : _points[^1];
        var distance = TimeSpan.FromTicks(Math.Abs(t - edge.Time!.Value.UtcTicks));
        return distance <= tolerance
            ? new TrackMatch(edge.ToGeoPoint(), TrackMatchKind.NearestPoint, distance, photoTimeUtc)
            : null;
    }

    private static GeoPoint Interpolate(GpxTrackPoint a, GpxTrackPoint b, double f)
    {
        double lat = a.Latitude + (b.Latitude - a.Latitude) * f;
        double dLon = b.Longitude - a.Longitude;
        if (dLon > 180) dLon -= 360;
        else if (dLon < -180) dLon += 360;
        double lon = a.Longitude + dLon * f;
        if (lon > 180) lon -= 360;
        else if (lon < -180) lon += 360;

        double? ele = a.Elevation is { } ea && b.Elevation is { } eb ? ea + (eb - ea) * f : a.Elevation ?? b.Elevation;
        return new GeoPoint(lat, lon, ele);
    }
}
