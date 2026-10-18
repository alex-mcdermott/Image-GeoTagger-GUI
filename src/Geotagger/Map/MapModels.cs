using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Geotagger.Core.Geo;
using Geotagger.Core.Gpx;

namespace Geotagger.Map;

/// <summary>What the map needs to know about a photo for hover previews.</summary>
public interface IMapPhoto
{
    string FileName { get; }
    DateTime? DateTaken { get; }
    Bitmap? Thumbnail { get; }
}

/// <summary>One location on the map holding one or more photos.</summary>
public sealed class MapMarker
{
    public MapMarker(GeoPoint location, IReadOnlyList<IMapPhoto> photos, bool isSelected, bool isPending)
    {
        Location = location;
        Photos = photos;
        IsSelected = isSelected;
        IsPending = isPending;
        (X, Y) = WebMercator.Project(location.Latitude, location.Longitude);
    }

    public GeoPoint Location { get; }
    public IReadOnlyList<IMapPhoto> Photos { get; }
    public bool IsSelected { get; }
    public bool IsPending { get; }
    internal double X { get; }
    internal double Y { get; }
}

/// <summary>A polyline drawn on the map (a GPX track segment).</summary>
public sealed class MapTrack
{
    public MapTrack(IEnumerable<GeoPoint> points, Color color, string name)
        : this(points.Select(p => new GpxTrackPoint(p.Latitude, p.Longitude, p.Altitude, null)).ToList(), color, name) { }

    public MapTrack(IReadOnlyList<GpxTrackPoint> points, Color color, string name)
    {
        Points = points;
        var xs = new List<double>();
        var ys = new List<double>();
        foreach (var p in points)
        {
            var (x, y) = WebMercator.Project(p.Latitude, p.Longitude);
            xs.Add(x);
            ys.Add(y);
        }
        Xs = xs.ToArray();
        Ys = ys.ToArray();
        Color = color;
        Name = name;
    }

    public double[] Xs { get; }
    public double[] Ys { get; }
    public IReadOnlyList<GpxTrackPoint> Points { get; }
    public Color Color { get; }
    public string Name { get; }
}

/// <summary>A position on a track log under the pointer, interpolated between fixes (film mode).</summary>
public sealed record TrackSnap(GeoPoint Location, DateTimeOffset Time, string TrackName);

public sealed class MapMarkersEventArgs(IReadOnlyList<MapMarker> markers, KeyModifiers modifiers) : EventArgs
{
    public IReadOnlyList<MapMarker> Markers { get; } = markers;
    public KeyModifiers Modifiers { get; } = modifiers;
}

public sealed class MapMarkersMovedEventArgs(IReadOnlyList<MapMarker> markers, GeoPoint to, TrackSnap? snap = null) : EventArgs
{
    public IReadOnlyList<MapMarker> Markers { get; } = markers;
    public GeoPoint To { get; } = to;

    /// <summary>The track position the pin was dropped on, when snapping to tracks.</summary>
    public TrackSnap? Snap { get; } = snap;
}

public sealed class MapDropEventArgs(IDataTransfer data, GeoPoint location, TrackSnap? snap = null) : EventArgs
{
    public IDataTransfer Data { get; } = data;
    public GeoPoint Location { get; } = location;

    /// <summary>The track position under the drop, when snapping to tracks.</summary>
    public TrackSnap? Snap { get; } = snap;
}
