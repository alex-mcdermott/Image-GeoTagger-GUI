using Geotagger.Core.Geo;

namespace Geotagger.Tests;

public class CoordinateParserTests
{
    [Theory]
    [InlineData("51.5007, -0.1246", 51.5007, -0.1246)]
    [InlineData("51.5007,-0.1246", 51.5007, -0.1246)]
    [InlineData("51.5007 -0.1246", 51.5007, -0.1246)]
    [InlineData("  -33.8568 ; 151.2153 ", -33.8568, 151.2153)]
    [InlineData("51.5007N 0.1246W", 51.5007, -0.1246)]
    [InlineData("N51.5007 W0.1246", 51.5007, -0.1246)]
    [InlineData("51.5007° N, 0.1246° W", 51.5007, -0.1246)]
    [InlineData("51°30'02.5\"N 0°07'28.6\"W", 51.500694, -0.124611)]
    [InlineData("51°30′02.5″N 0°07′28.6″W", 51.500694, -0.124611)]
    [InlineData("33°51'24.5\"S 151°12'55.1\"E", -33.856806, 151.215306)]
    [InlineData("51 30 02.5 N, 0 07 28.6 W", 51.500694, -0.124611)]
    [InlineData("51°29.898'N 0°15.918'W", 51.4983, -0.2653)]
    [InlineData("0.1246W 51.5007N", 51.5007, -0.1246)]
    [InlineData("https://www.google.com/maps/@51.5007292,-0.1246254,17z", 51.5007292, -0.1246254)]
    [InlineData("https://www.google.com/maps/place/Big+Ben/@51.5007292,-0.1246254,17z/data=!3m1", 51.5007292, -0.1246254)]
    [InlineData("https://maps.google.com/?q=51.5007,-0.1246", 51.5007, -0.1246)]
    [InlineData("https://www.openstreetmap.org/#map=17/51.50073/-0.12463", 51.50073, -0.12463)]
    [InlineData("geo:51.5007,-0.1246", 51.5007, -0.1246)]
    public void Parses_common_formats(string input, double lat, double lon)
    {
        Assert.True(CoordinateParser.TryParse(input, out var p), $"failed to parse '{input}'");
        Assert.Equal(lat, p.Latitude, 5);
        Assert.Equal(lon, p.Longitude, 5);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("51.5")]
    [InlineData("91, 0")]
    [InlineData("10, 181")]
    [InlineData("51.5N 0.1N")]
    [InlineData("1 2 3 4 5 6 7")]
    [InlineData("London")]
    public void Rejects_non_coordinates(string input)
    {
        Assert.False(CoordinateParser.TryParse(input, out _));
    }

    [Fact]
    public void Formats_round_trip()
    {
        var p = new GeoPoint(51.4983404, -0.2653681);
        Assert.Equal("51.49834, -0.265368", CoordinateFormatter.ToDecimal(p));
        Assert.Equal("51°29'54.0\"N 0°15'55.3\"W", CoordinateFormatter.ToDms(p));
        Assert.True(CoordinateParser.TryParse(CoordinateFormatter.ToDms(p), out var back));
        Assert.Equal(p.Latitude, back.Latitude, 4);
        Assert.Equal(p.Longitude, back.Longitude, 4);
    }
}

public class WebMercatorTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(51.5, -0.12)]
    [InlineData(-33.86, 151.21)]
    [InlineData(84, 179.9)]
    public void Project_and_unproject_round_trip(double lat, double lon)
    {
        var (x, y) = WebMercator.Project(lat, lon);
        var back = WebMercator.Unproject(x, y);
        Assert.Equal(lat, back.Latitude, 6);
        Assert.Equal(lon, back.Longitude, 6);
    }

    [Fact]
    public void Origin_is_world_centre()
    {
        var (x, y) = WebMercator.Project(0, 0);
        Assert.Equal(0.5, x, 9);
        Assert.Equal(0.5, y, 9);
    }

    [Fact]
    public void Fit_contains_all_points_and_respects_max_zoom()
    {
        var pts = new[] { new GeoPoint(51.49, -0.27), new GeoPoint(51.50, -0.15) };
        var (center, zoom) = WebMercator.Fit(pts, 1000, 600, 40, 2, 18);
        Assert.InRange(center.Latitude, 51.49, 51.50);
        Assert.InRange(center.Longitude, -0.27, -0.15);
        Assert.InRange(zoom, 12, 14);

        var (_, single) = WebMercator.Fit([new GeoPoint(1, 1)], 1000, 600, 40, 2, 17);
        Assert.Equal(17, single);
    }

    [Fact]
    public void Distance_is_sensible()
    {
        // London to Paris is ~344 km.
        double d = GeoPoint.DistanceMeters(new GeoPoint(51.5074, -0.1278), new GeoPoint(48.8566, 2.3522));
        Assert.InRange(d, 340_000, 348_000);
    }
}
