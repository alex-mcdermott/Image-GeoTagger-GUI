using System.Globalization;

namespace Geotagger.Core.Geo;

/// <summary>A WGS-84 position. Altitude is metres above sea level when known.</summary>
public readonly record struct GeoPoint(double Latitude, double Longitude, double? Altitude = null)
{
    private const double EarthRadiusMeters = 6_371_008.8;

    public bool IsValid =>
        !double.IsNaN(Latitude) && !double.IsNaN(Longitude) &&
        Latitude is >= -90 and <= 90 && Longitude is >= -180 and <= 180;

    public GeoPoint WithoutAltitude() => this with { Altitude = null };

    /// <summary>Great-circle distance in metres (haversine).</summary>
    public static double DistanceMeters(GeoPoint a, GeoPoint b)
    {
        double dLat = ToRad(b.Latitude - a.Latitude);
        double dLon = ToRad(b.Longitude - a.Longitude);
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(ToRad(a.Latitude)) * Math.Cos(ToRad(b.Latitude)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    /// <summary>True when both points describe the same place to within ~1 cm.</summary>
    public bool SamePlaceAs(GeoPoint other) =>
        Math.Abs(Latitude - other.Latitude) < 1e-7 && Math.Abs(Longitude - other.Longitude) < 1e-7;

    public override string ToString() => CoordinateFormatter.ToDecimal(this);

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
}

public static class CoordinateFormatter
{
    public static string ToDecimal(GeoPoint p, int decimals = 6) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Math.Round(p.Latitude, decimals).ToString("0.0#####", CultureInfo.InvariantCulture)}, {Math.Round(p.Longitude, decimals).ToString("0.0#####", CultureInfo.InvariantCulture)}");

    public static string ToDms(GeoPoint p) =>
        $"{FormatDms(Math.Abs(p.Latitude))}{(p.Latitude >= 0 ? "N" : "S")} {FormatDms(Math.Abs(p.Longitude))}{(p.Longitude >= 0 ? "E" : "W")}";

    public static string FormatAltitude(double? altitude) =>
        altitude is { } a ? string.Create(CultureInfo.InvariantCulture, $"{a:0.#} m") : "—";

    private static string FormatDms(double value)
    {
        int deg = (int)Math.Floor(value);
        double minutesFull = (value - deg) * 60;
        int min = (int)Math.Floor(minutesFull);
        double sec = (minutesFull - min) * 60;
        if (Math.Round(sec, 1) >= 60) { sec = 0; min++; }
        if (min >= 60) { min = 0; deg++; }
        return string.Create(CultureInfo.InvariantCulture, $"{deg}°{min:00}'{sec:00.0}\"");
    }
}
