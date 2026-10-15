using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Geotagger.Core.Geo;

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
    {
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
    public Color Color { get; }
    public string Name { get; }
}

public sealed class MapMarkersEventArgs(IReadOnlyList<MapMarker> markers, KeyModifiers modifiers) : EventArgs
{
    public IReadOnlyList<MapMarker> Markers { get; } = markers;
    public KeyModifiers Modifiers { get; } = modifiers;
}

public sealed class MapMarkersMovedEventArgs(IReadOnlyList<MapMarker> markers, GeoPoint to) : EventArgs
{
    public IReadOnlyList<MapMarker> Markers { get; } = markers;
    public GeoPoint To { get; } = to;
}

public sealed class MapDropEventArgs(IDataTransfer data, GeoPoint location) : EventArgs
{
    public IDataTransfer Data { get; } = data;
    public GeoPoint Location { get; } = location;
}
