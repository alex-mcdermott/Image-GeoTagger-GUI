namespace Geotagger.Core.Geo;

/// <summary>
/// Spherical (Web) Mercator helpers. "Normalised" coordinates run from 0 to 1 across the
/// whole world, x left→right from the antimeridian and y top→bottom from ~85°N.
/// </summary>
public static class WebMercator
{
    public const double MaxLatitude = 85.05112878;
    public const int TileSize = 256;

    public static (double X, double Y) Project(double latitude, double longitude)
    {
        double lat = Math.Clamp(latitude, -MaxLatitude, MaxLatitude);
        double x = (longitude + 180.0) / 360.0;
        double sin = Math.Sin(lat * Math.PI / 180.0);
        double y = 0.5 - Math.Log((1 + sin) / (1 - sin)) / (4 * Math.PI);
        return (x, y);
    }

    public static GeoPoint Unproject(double x, double y)
    {
        double lon = x * 360.0 - 180.0;
        lon = ((lon + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
        double n = Math.PI - 2.0 * Math.PI * Math.Clamp(y, 0, 1);
        double lat = 180.0 / Math.PI * Math.Atan(Math.Sinh(n));
        return new GeoPoint(lat, lon);
    }

    /// <summary>Size of the whole world in pixels at the given (fractional) zoom.</summary>
    public static double WorldSize(double zoom) => TileSize * Math.Pow(2, zoom);

    /// <summary>Ground resolution in metres per pixel at a latitude and zoom level.</summary>
    public static double MetersPerPixel(double latitude, double zoom) =>
        156_543.033_92 * Math.Cos(latitude * Math.PI / 180.0) / Math.Pow(2, zoom);

    /// <summary>
    /// The largest zoom at which the bounding box of <paramref name="points"/> fits into a
    /// viewport of the given size (in pixels), leaving <paramref name="padding"/> on each side.
    /// </summary>
    public static (GeoPoint Center, double Zoom) Fit(IReadOnlyCollection<GeoPoint> points, double width, double height,
        double padding, double minZoom, double maxZoom)
    {
        if (points.Count == 0) throw new ArgumentException("No points to fit.", nameof(points));

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in points)
        {
            var (x, y) = Project(p.Latitude, p.Longitude);
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }

        double availW = Math.Max(1, width - 2 * padding);
        double availH = Math.Max(1, height - 2 * padding);
        double spanX = Math.Max(maxX - minX, 1e-12);
        double spanY = Math.Max(maxY - minY, 1e-12);
        double zoomX = Math.Log2(availW / (spanX * TileSize));
        double zoomY = Math.Log2(availH / (spanY * TileSize));
        double zoom = Math.Clamp(Math.Min(zoomX, zoomY), minZoom, maxZoom);
        var center = Unproject((minX + maxX) / 2, (minY + maxY) / 2);
        return (center, zoom);
    }
}
