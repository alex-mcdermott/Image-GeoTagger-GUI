using Geotagger.Core.Geo;

namespace Geotagger.Core.Gpx;

public sealed record GpxTrackPoint(double Latitude, double Longitude, double? Elevation, DateTimeOffset? Time)
{
    public GeoPoint ToGeoPoint() => new(Latitude, Longitude, Elevation);
}

public sealed class GpxSegment
{
    public List<GpxTrackPoint> Points { get; } = [];
}

public sealed class GpxTrack
{
    public string? Name { get; set; }
    public List<GpxSegment> Segments { get; } = [];
}

public sealed class GpxFile
{
    public string? FilePath { get; init; }
    public string? Name { get; set; }
    public string? Creator { get; set; }

    /// <summary>Tracks (trk) and routes (rte). Routes rarely carry timestamps but are still drawn.</summary>
    public List<GpxTrack> Tracks { get; } = [];

    public List<GpxTrackPoint> Waypoints { get; } = [];

    public IEnumerable<GpxTrackPoint> AllTrackPoints =>
        Tracks.SelectMany(t => t.Segments).SelectMany(s => s.Points);

    public int PointCount => Tracks.Sum(t => t.Segments.Sum(s => s.Points.Count));

    public int TimedPointCount => AllTrackPoints.Count(p => p.Time.HasValue);

    public DateTimeOffset? StartTime => AllTrackPoints.Where(p => p.Time.HasValue).Select(p => p.Time).Min();

    public DateTimeOffset? EndTime => AllTrackPoints.Where(p => p.Time.HasValue).Select(p => p.Time).Max();

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Name) ? Name! :
        FilePath is not null ? Path.GetFileNameWithoutExtension(FilePath) : "Track";
}
