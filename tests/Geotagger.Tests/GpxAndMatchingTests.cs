using System.Text;
using Geotagger.Core.Gpx;
using Geotagger.Core.Matching;

namespace Geotagger.Tests;

public class GpxParserTests
{
    [Fact]
    public void Parses_tracks_segments_routes_and_waypoints()
    {
        var gpx = GpxParser.Load(TestFiles.DataPath("walk.gpx"));

        Assert.Equal("Morning walk", gpx.Name);
        Assert.Equal("Geotagger tests", gpx.Creator);
        Assert.Single(gpx.Waypoints);
        Assert.Equal(2, gpx.Tracks.Count);

        var walk = gpx.Tracks[0];
        Assert.Equal("Walk", walk.Name);
        Assert.Equal(2, walk.Segments.Count);
        Assert.Equal(3, walk.Segments[0].Points.Count);
        Assert.Equal(2, walk.Segments[1].Points.Count); // the lat=91 point is dropped

        var first = walk.Segments[0].Points[0];
        Assert.Equal(51.5, first.Latitude);
        Assert.Equal(-0.12, first.Longitude);
        Assert.Equal(10.0, first.Elevation);
        Assert.Equal(new DateTimeOffset(2024, 6, 1, 9, 0, 0, TimeSpan.Zero), first.Time);
        Assert.Null(walk.Segments[1].Points[1].Elevation);
        Assert.Equal(500, walk.Segments[1].Points[1].Time!.Value.Millisecond);

        var route = gpx.Tracks[1];
        Assert.Equal("Planned", route.Name);
        Assert.All(route.Segments[0].Points, p => Assert.Null(p.Time));

        Assert.Equal(7, gpx.PointCount);
        Assert.Equal(5, gpx.TimedPointCount);
        Assert.Equal(new DateTimeOffset(2024, 6, 1, 9, 0, 0, TimeSpan.Zero), gpx.StartTime);
    }

    [Fact]
    public void Accepts_gpx_1_0_without_namespace_prefixes_or_offsets()
    {
        const string xml = """
            <?xml version="1.0"?>
            <gpx version="1.0" creator="old logger" xmlns="http://www.topografix.com/GPX/1/0">
              <name>Old file</name>
              <trk><trkseg>
                <trkpt lat="10" lon="20"><time>2024-01-01T12:00:00+02:00</time></trkpt>
                <trkpt lat="10.1" lon="20.1"><time>2024-01-01T10:01:00</time></trkpt>
              </trkseg></trk>
            </gpx>
            """;
        var gpx = GpxParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
        Assert.Equal("Old file", gpx.Name);
        var pts = gpx.AllTrackPoints.ToList();
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero), pts[0].Time); // offset honoured
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 10, 1, 0, TimeSpan.Zero), pts[1].Time); // no zone = UTC
    }

    [Fact]
    public void Empty_or_pointless_files_are_fine()
    {
        var gpx = GpxParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes("<gpx><trk><trkseg/></trk></gpx>")));
        Assert.Empty(gpx.Tracks);
        Assert.Equal(0, gpx.PointCount);
        Assert.Null(gpx.StartTime);
    }
}

public class TrackLogTests
{
    private static readonly TrackLog Log = TrackLog.FromFiles([GpxParser.Load(TestFiles.DataPath("walk.gpx"))]);

    private static DateTimeOffset T(int h, int m, int s = 0) => new(2024, 6, 1, h, m, s, TimeSpan.Zero);

    [Fact]
    public void Uses_only_timed_points() => Assert.Equal(5, Log.Count);

    [Fact]
    public void Interpolates_between_close_fixes()
    {
        var m = Log.Match(T(9, 0, 30), TimeSpan.FromMinutes(5));
        Assert.NotNull(m);
        Assert.Equal(TrackMatchKind.Interpolated, m.Value.Kind);
        Assert.Equal(51.5005, m.Value.Location.Latitude, 6);
        Assert.Equal(-0.1205, m.Value.Location.Longitude, 6);
        Assert.Equal(11.0, m.Value.Location.Altitude!.Value, 6);
        Assert.Equal(TimeSpan.FromSeconds(30), m.Value.TimeToNearestFix);
    }

    [Fact]
    public void Exact_hit_returns_that_fix()
    {
        var m = Log.Match(T(9, 1), TimeSpan.FromMinutes(5));
        Assert.Equal(51.501, m!.Value.Location.Latitude, 9);
        Assert.Equal(TimeSpan.Zero, m.Value.TimeToNearestFix);
    }

    [Fact]
    public void Snaps_to_nearest_fix_across_a_long_gap_when_within_tolerance()
    {
        var m = Log.Match(T(9, 5), TimeSpan.FromMinutes(5));
        Assert.Equal(TrackMatchKind.NearestPoint, m!.Value.Kind);
        Assert.Equal(51.502, m.Value.Location.Latitude, 9);
    }

    [Fact]
    public void Does_not_match_in_the_middle_of_a_long_gap()
    {
        Assert.Null(Log.Match(T(9, 30), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Larger_tolerance_interpolates_across_the_gap()
    {
        var m = Log.Match(T(9, 32), TimeSpan.FromMinutes(60));
        Assert.Equal(TrackMatchKind.Interpolated, m!.Value.Kind);
        Assert.Equal((51.502 + 51.6) / 2, m.Value.Location.Latitude, 6);
    }

    [Fact]
    public void Handles_photos_just_before_and_after_the_log()
    {
        Assert.Equal(51.5, Log.Match(T(8, 58), TimeSpan.FromMinutes(5))!.Value.Location.Latitude, 9);
        Assert.Null(Log.Match(T(8, 50), TimeSpan.FromMinutes(5)));
        Assert.Equal(51.601, Log.Match(T(10, 10), TimeSpan.FromMinutes(10))!.Value.Location.Latitude, 9);
        Assert.Null(Log.Match(T(10, 30), TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void Interpolation_crosses_the_antimeridian_the_short_way()
    {
        var log = new TrackLog([
            new GpxTrackPoint(0, 179.9, null, T(9, 0)),
            new GpxTrackPoint(0, -179.9, null, T(9, 1)),
        ]);
        var m = log.Match(T(9, 0, 30), TimeSpan.FromMinutes(5));
        Assert.Equal(180, Math.Abs(m!.Value.Location.Longitude), 6);
    }
}

public class CameraClockTests
{
    private static string LondonId =>
        TimeZoneInfo.TryFindSystemTimeZoneById("Europe/London", out var tz) ? tz.Id : "GMT Standard Time";

    [Fact]
    public void Converts_local_camera_time_with_daylight_saving()
    {
        var clock = new CameraClock { TimeZoneId = LondonId };
        Assert.Equal(new DateTimeOffset(2024, 6, 1, 9, 0, 0, TimeSpan.Zero), clock.ToUtc(new DateTime(2024, 6, 1, 10, 0, 0), null));
        Assert.Equal(new DateTimeOffset(2024, 1, 10, 10, 0, 0, TimeSpan.Zero), clock.ToUtc(new DateTime(2024, 1, 10, 10, 0, 0), null));
    }

    [Fact]
    public void Embedded_offset_wins_only_when_preferred()
    {
        var local = new DateTime(2024, 6, 1, 10, 0, 0);
        var offset = TimeSpan.FromHours(2);
        Assert.Equal(new DateTimeOffset(2024, 6, 1, 8, 0, 0, TimeSpan.Zero),
            new CameraClock { TimeZoneId = LondonId, PreferEmbeddedOffset = true }.ToUtc(local, offset));
        Assert.Equal(new DateTimeOffset(2024, 6, 1, 9, 0, 0, TimeSpan.Zero),
            new CameraClock { TimeZoneId = LondonId, PreferEmbeddedOffset = false }.ToUtc(local, offset));
    }

    [Fact]
    public void Applies_clock_correction()
    {
        var clock = new CameraClock { TimeZoneId = "UTC", Correction = TimeSpan.FromSeconds(90) };
        Assert.Equal(new DateTimeOffset(2024, 6, 1, 10, 1, 30, TimeSpan.Zero), clock.ToUtc(new DateTime(2024, 6, 1, 10, 0, 0), null));
        Assert.Null(clock.ToUtc(null, null));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("+00:01:30", 90)]
    [InlineData("-00:01:30", -90)]
    [InlineData("01:00", 3600)]
    [InlineData("-5:00", -18000)]
    [InlineData("90", 90)]
    [InlineData("-1h", -3600)]
    [InlineData("2m30s", 150)]
    [InlineData("1d2h", 93600)]
    public void Parses_corrections(string text, int seconds)
    {
        Assert.True(CameraClock.TryParseCorrection(text, out var v));
        Assert.Equal(TimeSpan.FromSeconds(seconds), v);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1:60")]
    [InlineData("1:2:3:4")]
    public void Rejects_bad_corrections(string text) => Assert.False(CameraClock.TryParseCorrection(text, out _));

    [Fact]
    public void Formats_corrections() => Assert.Equal("-00:01:30", CameraClock.FormatCorrection(TimeSpan.FromSeconds(-90)));
}

public class MatchSummaryTests
{
    [Fact]
    public void All_matched() =>
        Assert.Equal("All 3 photos fall within the track log.", Geotagger.ViewModels.MainViewModel.BuildMatchSummary(3, 3, 0));

    [Fact]
    public void Outside_track_suggests_checking_the_clock()
    {
        string s = Geotagger.ViewModels.MainViewModel.BuildMatchSummary(3, 2, 0);
        Assert.StartsWith("2 of 3", s);
        Assert.Contains("1 is outside it", s);
        Assert.DoesNotContain("no date", s);
    }

    [Fact]
    public void Undated_photos_are_reported_separately()
    {
        // e.g. a second camera whose clock was reset part-way through the day
        string s = Geotagger.ViewModels.MainViewModel.BuildMatchSummary(353, 200, 153);
        Assert.StartsWith("200 of 353", s);
        Assert.Contains("153 have no date taken", s);
        Assert.DoesNotContain("outside", s);
    }
}
