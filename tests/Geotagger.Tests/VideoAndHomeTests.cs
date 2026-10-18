using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Geotagger.Core;
using Geotagger.Core.Geo;
using Geotagger.Core.Photos;
using Geotagger.Services;
using Geotagger.ViewModels;
using Geotagger.Views;

namespace Geotagger.Tests;

public class VideoTests
{
    private static readonly DateTimeOffset Taken = new(2024, 6, 1, 10, 30, 15, TimeSpan.FromHours(1));

    [Fact]
    public void Video_args_write_every_common_gps_location()
    {
        var args = GeoWriter.BuildEmbeddedArgs("clip.mov", new GeoTagChange(new GeoPoint(51.5007, -0.1246, 12.5)), new WriteOptions());
        Assert.Contains("-api", args);
        Assert.Contains("QuickTimeUTC=1", args);
        Assert.Contains("-Keys:GPSCoordinates=51.5007, -0.1246, 12.5", args);
        Assert.Contains("-UserData:GPSCoordinates=51.5007, -0.1246, 12.5", args);
        Assert.Contains("-XMP-exif:GPSLatitude=51.5007", args);
        Assert.Contains("-XMP-exif:GPSLongitude=-0.1246", args);
        Assert.Contains("-GPS:GPSLongitudeRef=W", args);
        Assert.DoesNotContain(args, a => a.StartsWith("-QuickTime:CreateDate", StringComparison.Ordinal));

        var removal = GeoWriter.BuildEmbeddedArgs("clip.mp4", GeoTagChange.Remove, new WriteOptions());
        Assert.Contains("-Keys:GPSCoordinates=", removal);
        Assert.Contains("-UserData:GPSCoordinates=", removal);
    }

    [Fact]
    public void Video_film_mode_args_set_quicktime_dates()
    {
        var args = GeoWriter.BuildEmbeddedArgs("clip.mov", new GeoTagChange(new GeoPoint(1, 2), Taken.ToUniversalTime(), Taken), new WriteOptions());
        Assert.Contains("-QuickTime:CreateDate=2024:06:01 10:30:15+01:00", args);
        Assert.Contains("-Keys:CreationDate=2024:06:01 10:30:15+01:00", args);
        Assert.Contains("-Track*Date=2024:06:01 10:30:15+01:00", args);
        Assert.Contains("-FileModifyDate=2024:06:01 10:30:15+01:00", args);
    }

    [Fact]
    public void Parses_video_metadata_with_xmp_taking_priority()
    {
        string path = Path.Combine(Path.GetTempPath(), "c.mov").Replace('\\', '/');
        var merged = MetadataReader.ParseVideoJson($$"""
            [{"SourceFile":"{{path}}","Keys:GPSCoordinates":"40 -74 10","XMP-exif:GPSLatitude":51.5,"XMP-exif:GPSLongitude":-0.12,
              "QuickTime:CreateDate":"2024:06:01 10:30:15+01:00","QuickTime:Duration":12.5}]
            """).Single();
        var baseMeta = new PhotoMetadata { Path = merged.Path, Location = new GeoPoint(18, 98) };
        var m = merged.Merge(baseMeta);
        Assert.Equal(51.5, m.Location!.Value.Latitude);
        Assert.Equal(new DateTime(2024, 6, 1, 10, 30, 15), m.DateTaken);
        Assert.Equal(TimeSpan.FromHours(1), m.TimeZoneOffset);
        Assert.Equal(TimeSpan.FromSeconds(12.5), m.Duration);

        var keysOnly = MetadataReader.ParseVideoJson($$"""[{"SourceFile":"{{path}}","Keys:GPSCoordinates":"40.7 -74.0 10"}]""").Single();
        Assert.Equal(new GeoPoint(40.7, -74.0, 10), keysOnly.Merge(baseMeta).Location);
    }

    private static string? Ffmpeg() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Select(d => Path.Combine(d.Trim(), OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"))
        .FirstOrDefault(File.Exists);

    /// <summary>A real video to write to: GEOTAGGER_SAMPLE_VIDEO (copied), or a 1-second clip made with ffmpeg.</summary>
    private static string RequireVideo(TempDir dir)
    {
        string? sample = Environment.GetEnvironmentVariable("GEOTAGGER_SAMPLE_VIDEO");
        if (!string.IsNullOrEmpty(sample) && File.Exists(sample))
        {
            string copy = dir.File(Path.GetFileName(sample));
            File.Copy(sample, copy);
            return copy;
        }
        var ffmpeg = Ffmpeg();
        if (ffmpeg is null) Assert.Skip("Needs ffmpeg (or GEOTAGGER_SAMPLE_VIDEO) to create a test video.");
        string path = dir.File("clip.mov");
        var psi = new ProcessStartInfo(ffmpeg!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=10:duration=1",
                     "-pix_fmt", "yuv420p", path })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(File.Exists(path), "ffmpeg failed to create a test video");
        return path;
    }

    [Fact]
    public async Task Writes_and_reads_gps_and_dates_in_a_video()
    {
        await using var et = await TestExifTool.RequireAsync();
        using var dir = new TempDir();
        string video = RequireVideo(dir);
        var where = new GeoPoint(-33.856784, 151.215297, 25.5);

        var r = await GeoWriter.WriteAsync(et, new PhotoSource(video), new GeoTagChange(where, Taken.ToUniversalTime(), Taken), new WriteOptions());
        Assert.True(r.Success, string.Join("; ", r.Errors));

        Assert.StartsWith("-33.856784 151.215297 25.5", await TestExifTool.RunAsync("-n", "-s3", "-Keys:GPSCoordinates", video));
        Assert.StartsWith("-33.856784 151.215297", await TestExifTool.RunAsync("-n", "-s3", "-UserData:GPSCoordinates", video));
        Assert.Equal("-33.856784", await TestExifTool.RunAsync("-n", "-s3", "-XMP-exif:GPSLatitude", video));
        Assert.Equal("2024:06:01 09:30:15", await TestExifTool.RunAsync("-s3", "-QuickTime:CreateDate", video)); // UTC
        Assert.Equal(Taken.UtcDateTime, File.GetLastWriteTimeUtc(video));

        var meta = (await MetadataReader.ReadMediaAsync(et, [video]))[Path.GetFullPath(video)];
        Assert.Equal(where.Latitude, meta.Location!.Value.Latitude, 6);
        Assert.Equal(where.Longitude, meta.Location!.Value.Longitude, 6);
        Assert.Equal(Taken.DateTime, meta.DateTaken);
        Assert.Equal(TimeSpan.FromHours(1), meta.TimeZoneOffset);

        var removed = await GeoWriter.WriteAsync(et, new PhotoSource(video), GeoTagChange.Remove, new WriteOptions());
        Assert.True(removed.Success, string.Join("; ", removed.Errors));
        meta = (await MetadataReader.ReadMediaAsync(et, [video]))[Path.GetFullPath(video)];
        if (!video.EndsWith(".MOV", StringComparison.Ordinal) || Environment.GetEnvironmentVariable("GEOTAGGER_SAMPLE_VIDEO") is null)
            Assert.Null(meta.Location);
    }

    [Fact]
    public void Folder_scan_includes_videos()
    {
        using var dir = new TempDir();
        foreach (var f in new[] { "a.MOV", "b.mp4", "c.jpg", "d.avi" }) File.WriteAllText(dir.File(f), "x");
        var names = FolderScanner.Scan(dir.Path).Photos.Select(p => p.FileName).ToList();
        Assert.Equal(["a.MOV", "b.mp4", "c.jpg"], names);
    }
}

public class HomeLocationTests
{
    [Fact]
    public void Settings_parse_and_clear_home()
    {
        var settings = new AppSettings();
        var vm = new SettingsViewModel(settings, "", new FakeUi(), () => "", () => { });
        vm.HomeText = "https://www.google.com/maps/@51.4778616,-0.0014862,17z";
        Assert.True(vm.CanSave);
        vm.ApplyTo(settings);
        Assert.Equal(51.4778616, settings.HomeLatitude!.Value, 6);
        Assert.Equal(-0.0014862, settings.HomeLongitude!.Value, 6);

        vm.HomeText = "not a place";
        Assert.False(vm.CanSave);

        vm.ClearHomeCommand.Execute(null);
        vm.ApplyTo(settings);
        Assert.Null(settings.HomeLatitude);
    }

    [AvaloniaFact]
    public async Task Places_selected_photos_at_home()
    {
        await using var probe = await TestExifTool.RequireAsync();
        using var root = new TempDir();
        AppPaths.OverrideRoot(Path.Combine(root.Path, "appdata"));
        try
        {
            string folder = Path.Combine(root.Path, "home");
            Directory.CreateDirectory(folder);
            TestFiles.CreateJpeg(Path.Combine(folder, "kitchen.jpg"));
            TestFiles.CreateJpeg(Path.Combine(folder, "garden.jpg"));

            using var http = new HttpClient(new FakeTileHandler());
            var settings = new AppSettings { HomeLatitude = 51.4778616, HomeLongitude = -0.0014862 };
            var vm = new MainViewModel(new FakeUi(), settings, http);
            var window = new Window { Width = 1200, Height = 800, Content = new MainView { DataContext = vm } };
            window.Show();
            await vm.InitializeAsync(folder, []);

            Assert.True(vm.HasHomeLocation);
            Assert.False(vm.PlaceSelectionAtHomeCommand.CanExecute(null)); // nothing selected
            vm.SetSelectionFromView(vm.Photos.ToList());
            vm.PlaceSelectionAtHomeCommand.Execute(null);
            Assert.All(vm.Photos, p => Assert.Equal(51.4778616, p.EffectiveLocation!.Value.Latitude, 6));

            await vm.SaveCommand.ExecuteAsync(null);
            Assert.Equal(0, vm.PendingCount);
            Assert.Equal(-0.0014862, double.Parse(await TestExifTool.RunAsync("-n", "-s3", "-Composite:GPSLongitude",
                Path.Combine(folder, "garden.jpg")), CultureInfo.InvariantCulture), 6);

            vm.SetHomeLocation(null);
            Assert.False(vm.HasHomeLocation);
            Assert.Null(SettingsStore.Load().HomeLatitude); // persisted (to the test's private settings folder)

            window.Close();
            await vm.DisposeAsync();
        }
        finally
        {
            AppPaths.OverrideRoot(null);
        }
    }
}
