using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Geotagger.Core;
using Geotagger.Core.Geo;
using Geotagger.Core.Gpx;
using Geotagger.Core.Photos;
using Geotagger.Map;
using Geotagger.Services;
using Geotagger.ViewModels;
using Geotagger.Views;

namespace Geotagger.Tests;

/// <summary>Film mode: photos take their location AND capture time from a point on a GPX track.</summary>
public class FilmModeTests
{
    private static readonly DateTimeOffset LocalTaken = new(2024, 6, 1, 10, 30, 15, TimeSpan.FromHours(1));

    [Fact]
    public void Embedded_args_set_date_taken_and_file_dates()
    {
        var change = new GeoTagChange(new GeoPoint(51.5, -0.12), LocalTaken.ToUniversalTime(), LocalTaken);
        var args = GeoWriter.BuildEmbeddedArgs("scan.jpg", change, new WriteOptions { PreserveFileDates = true });

        Assert.Contains("-DateTimeOriginal=2024:06:01 10:30:15", args);
        Assert.Contains("-OffsetTimeOriginal=+01:00", args);
        Assert.Contains("-FileModifyDate=2024:06:01 10:30:15+01:00", args);
        Assert.Contains("-GPSTimeStamp=09:30:15", args);
        Assert.DoesNotContain("-P", args); // the file dates are set explicitly instead of preserved
        if (OperatingSystem.IsWindows()) Assert.Contains("-FileCreateDate=2024:06:01 10:30:15+01:00", args);
    }

    [Fact]
    public void Sidecar_args_set_xmp_dates_and_negative_offsets_format()
    {
        var taken = new DateTimeOffset(2024, 1, 5, 8, 0, 0, TimeSpan.FromHours(-5));
        var args = GeoWriter.BuildSidecarArgs("IMG_1.xmp", true, "IMG_1.CR2", new GeoTagChange(new GeoPoint(40.7, -74), null, taken), new WriteOptions());
        Assert.Contains("-XMP-exif:DateTimeOriginal=2024:01:05 08:00:00-05:00", args);
        Assert.Contains("-XMP-photoshop:DateCreated=2024:01:05 08:00:00-05:00", args);
    }

    [Fact]
    public void Parses_dates_from_sidecars()
    {
        string path = Path.Combine(Path.GetTempPath(), "a.xmp").Replace('\\', '/');
        var list = MetadataReader.ParseSidecarJson($$"""[{"SourceFile":"{{path}}","DateTimeOriginal":"2024:06:01 10:30:15+01:00"}]""").ToList();
        Assert.Equal(new DateTime(2024, 6, 1, 10, 30, 15), list[0].Data.DateTaken);
        Assert.Equal(TimeSpan.FromHours(1), list[0].Data.TimeZoneOffset);
        Assert.Null(list[0].Data.Location);
    }

    [Fact]
    public async Task Writes_dates_into_a_jpeg_scan()
    {
        await using var et = await TestExifTool.RequireAsync();
        using var dir = new TempDir();
        var scan = TestFiles.CreateJpeg(dir.File("roll1_frame07.jpg"));
        var change = new GeoTagChange(new GeoPoint(51.5007, -0.1246, 12), LocalTaken.ToUniversalTime(), LocalTaken);

        var r = await GeoWriter.WriteAsync(et, new PhotoSource(scan), change, new WriteOptions());
        Assert.True(r.Success, string.Join("; ", r.Errors));

        Assert.Equal("2024:06:01 10:30:15", await TestExifTool.RunAsync("-s3", "-DateTimeOriginal", scan));
        Assert.Equal("+01:00", await TestExifTool.RunAsync("-s3", "-OffsetTimeOriginal", scan));
        Assert.Equal("2024:06:01 09:30:15Z", await TestExifTool.RunAsync("-s3", "-GPSDateTime", scan));
        Assert.Equal(LocalTaken.UtcDateTime, File.GetLastWriteTimeUtc(scan));
        if (OperatingSystem.IsWindows()) Assert.Equal(LocalTaken.UtcDateTime, File.GetCreationTimeUtc(scan));

        var meta = (await MetadataReader.ReadMediaAsync(et, [scan]))[Path.GetFullPath(scan)];
        Assert.Equal(LocalTaken.DateTime, meta.DateTaken);
        Assert.Equal(TimeSpan.FromHours(1), meta.TimeZoneOffset);
    }

    [Fact]
    public async Task Sidecar_mode_dates_the_sidecar_and_raw_file_without_changing_the_raw()
    {
        await using var et = await TestExifTool.RequireAsync();
        using var dir = new TempDir();
        byte[] raw = [9, 8, 7, 6, 5, 4, 3, 2];
        File.WriteAllBytes(dir.File("SCAN_01.DNG.ORF"), raw);
        var photo = new PhotoSource(dir.File("SCAN_01.DNG.ORF"));
        var change = new GeoTagChange(new GeoPoint(51.5, -0.12), LocalTaken.ToUniversalTime(), LocalTaken);

        var r = await GeoWriter.WriteAsync(et, photo, change, new WriteOptions { Target = WriteTarget.Sidecar });
        Assert.True(r.Success, string.Join("; ", r.Errors));
        Assert.Equal(raw, File.ReadAllBytes(photo.PrimaryPath));
        Assert.Equal(LocalTaken.UtcDateTime, File.GetLastWriteTimeUtc(photo.PrimaryPath));

        var sidecar = (await MetadataReader.ReadSidecarsAsync(et, [photo.DefaultSidecarPath]))[Path.GetFullPath(photo.DefaultSidecarPath)];
        Assert.Equal(LocalTaken.DateTime, sidecar.DateTaken);
        Assert.Equal(TimeSpan.FromHours(1), sidecar.TimeZoneOffset);
        Assert.Equal(51.5, sidecar.Location!.Value.Latitude, 6);
    }

    [AvaloniaFact]
    public void Map_snaps_to_the_track_and_interpolates_time()
    {
        var t0 = new DateTimeOffset(2024, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var points = new List<GpxTrackPoint>
        {
            new(51.5000, -0.1300, 10, t0),
            new(51.5000, -0.1100, 20, t0.AddMinutes(10)),
        };
        var map = new MapView { SnapToTracks = true };
        var window = new Window { Width = 800, Height = 600, Content = map };
        window.Show();
        map.SetView(new GeoPoint(51.5, -0.12), 15);
        map.Tracks = [new MapTrack(points, Avalonia.Media.Colors.Magenta, "walk")];
        Dispatcher.UIThread.RunJobs();

        // A few pixels off the middle of the segment snaps onto it, halfway in time.
        var mid = map.GeoToScreen(new GeoPoint(51.5, -0.12));
        var snap = map.SnapAt(new Point(mid.X, mid.Y + 8));
        Assert.NotNull(snap);
        Assert.Equal(51.5, snap.Location.Latitude, 5);
        Assert.Equal(-0.12, snap.Location.Longitude, 4);
        Assert.Equal(15, snap.Location.Altitude!.Value, 1);
        Assert.InRange((snap.Time - t0.AddMinutes(5)).Duration().TotalSeconds, 0, 10);

        Assert.Null(map.SnapAt(new Point(mid.X, mid.Y + 200)));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Film_mode_end_to_end()
    {
        await using var probe = await TestExifTool.RequireAsync();
        using var root = new TempDir();
        AppPaths.OverrideRoot(Path.Combine(root.Path, "appdata"));
        try
        {
            string folder = Path.Combine(root.Path, "roll");
            Directory.CreateDirectory(folder);
            var frames = Enumerable.Range(1, 3).Select(i => TestFiles.CreateJpeg(Path.Combine(folder, $"frame{i:00}.jpg"))).ToList();

            string zone = TimeZoneInfo.GetSystemTimeZones().FirstOrDefault(t => t.Id is "Europe/London" or "GMT Standard Time")?.Id ?? "UTC";
            using var http = new HttpClient(new FakeTileHandler());
            var vm = new MainViewModel(new FakeUi(), new AppSettings { CameraTimeZoneId = zone, FilmMode = true }, http);
            var window = new Window { Width = 1400, Height = 900, Content = new MainView { DataContext = vm } };
            window.Show();

            await vm.InitializeAsync(folder, [TestFiles.DataPath("walk.gpx")]);
            Assert.True(vm.IsFilmMode);
            Assert.Equal(3, vm.Photos.Count);

            // Two frames dropped together at 09:00:30 UTC (10:00:30 BST), the third later on.
            var snap = new TrackSnap(new GeoPoint(51.5005, -0.1205, 11), new DateTimeOffset(2024, 6, 1, 9, 0, 30, 400, TimeSpan.Zero), "Walk");
            vm.FilmPlace([vm.Photos[1], vm.Photos[0]], snap);
            vm.FilmPlace([vm.Photos[2]], snap with { Time = new DateTimeOffset(2024, 6, 1, 9, 2, 0, TimeSpan.Zero) });

            var first = vm.Photos.Single(p => p.FileName == "frame01.jpg");
            Assert.Equal(new DateTime(2024, 6, 1, 10, 0, 30), first.DateTaken); // rounded to the second, local time
            Assert.Equal(new DateTime(2024, 6, 1, 10, 0, 31), vm.Photos.Single(p => p.FileName == "frame02.jpg").DateTaken);
            Assert.Equal(3, vm.PendingCount);

            await vm.SaveCommand.ExecuteAsync(null);
            Assert.Equal(0, vm.PendingCount);
            Assert.False(vm.IsInfoOpen && vm.InfoSeverity == FluentAvalonia.UI.Controls.FAInfoBarSeverity.Error, vm.InfoMessage);

            Assert.Equal("2024:06:01 10:00:30", await TestExifTool.RunAsync("-s3", "-DateTimeOriginal", frames[0]));
            Assert.Equal("2024:06:01 10:00:31", await TestExifTool.RunAsync("-s3", "-DateTimeOriginal", frames[1]));
            Assert.Equal("2024:06:01 10:02:00", await TestExifTool.RunAsync("-s3", "-DateTimeOriginal", frames[2]));
            Assert.Equal(new DateTime(2024, 6, 1, 9, 2, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(frames[2]));
            Assert.Equal(51.5005, double.Parse(await TestExifTool.RunAsync("-n", "-s3", "-Composite:GPSLatitude", frames[0]), CultureInfo.InvariantCulture), 5);

            window.Close();
            await vm.DisposeAsync();
        }
        finally
        {
            AppPaths.OverrideRoot(null);
        }
    }
}
