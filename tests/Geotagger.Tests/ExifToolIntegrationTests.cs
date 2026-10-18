using Geotagger.Core.ExifTool;
using Geotagger.Core.Geo;
using Geotagger.Core.Photos;

namespace Geotagger.Tests;

/// <summary>
/// These run the real exiftool (skipped if it isn't installed). Set GEOTAGGER_SAMPLE_RAW to a raw
/// file to also test writing to a real camera raw (a temporary copy is used; the original is never touched).
/// </summary>
public class ExifToolIntegrationTests
{
    [Fact]
    public async Task Stay_open_process_survives_errors_and_runs_many_commands()
    {
        await using var et = await TestExifTool.RequireAsync();
        Assert.Matches(@"^\d+\.\d+", et.Version);

        var bad = await et.ExecuteAsync(["-json", Path.Combine(Path.GetTempPath(), "definitely-missing-" + Guid.NewGuid() + ".jpg")]);
        Assert.True(bad.HasErrors);

        for (int i = 0; i < 25; i++)
        {
            var r = await et.ExecuteAsync(["-ver"]);
            Assert.Equal(et.Version, r.Output.Trim());
        }
    }

    [Fact]
    public async Task Writes_reads_and_removes_gps_in_a_jpeg()
    {
        await using var et = await TestExifTool.RequireAsync();
        using var dir = new TempDir();
        var photo = new PhotoSource(TestFiles.CreateJpeg(dir.File("sydney.jpg")));
        var where = new GeoPoint(-33.856784, 151.215297, 25.5);

        var result = await GeoWriter.WriteAsync(et, photo, new GeoTagChange(where, new DateTimeOffset(2024, 6, 1, 9, 30, 15, TimeSpan.Zero)), new WriteOptions());
        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Single(result.FilesWritten);

        var meta = await MetadataReader.ReadMediaAsync(et, [photo.PrimaryPath]);
        var read = meta[photo.PrimaryPath].Location!.Value;
        Assert.Equal(where.Latitude, read.Latitude, 6);
        Assert.Equal(where.Longitude, read.Longitude, 6);
        Assert.Equal(25.5, read.Altitude!.Value, 3);

        // Check the raw tags independently of our reader.
        Assert.StartsWith("S", await TestExifTool.RunAsync("-s3", "-GPSLatitudeRef", photo.PrimaryPath));
        Assert.StartsWith("E", await TestExifTool.RunAsync("-s3", "-GPSLongitudeRef", photo.PrimaryPath));
        Assert.Equal("2024:06:01 09:30:15Z", await TestExifTool.RunAsync("-s3", "-GPSDateTime", photo.PrimaryPath));
        Assert.Empty(Directory.GetFiles(dir.Path, "*_original"));

        var removed = await GeoWriter.WriteAsync(et, photo, GeoTagChange.Remove, new WriteOptions());
        Assert.True(removed.Success, string.Join("; ", removed.Errors));
        meta = await MetadataReader.ReadMediaAsync(et, [photo.PrimaryPath]);
        Assert.Null(meta[photo.PrimaryPath].Location);
    }

    [Fact]
    public async Task Keeps_backups_and_file_dates_when_asked()
    {
        await using var et = await TestExifTool.RequireAsync();
        using var dir = new TempDir();
        var path = TestFiles.CreateJpeg(dir.File("a.jpg"));
        var stamp = new DateTime(2020, 5, 4, 3, 2, 1, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        var r = await GeoWriter.WriteAsync(et, new PhotoSource(path), new GeoTagChange(new GeoPoint(10, 20)),
            new WriteOptions { KeepBackups = true, PreserveFileDates = true });
        Assert.True(r.Success);
        Assert.True(File.Exists(path + "_original"));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task Handles_unicode_file_names()
    {
        await using var et = await TestExifTool.RequireAsync();
        using var dir = new TempDir();
        var path = TestFiles.CreateJpeg(dir.File("Café – 東京 📷.jpg"));
        var r = await GeoWriter.WriteAsync(et, new PhotoSource(path), new GeoTagChange(new GeoPoint(35.6762, 139.6503)), new WriteOptions());
        Assert.True(r.Success, string.Join("; ", r.Errors));
        var meta = await MetadataReader.ReadMediaAsync(et, [Path.GetFullPath(path)]);
        Assert.Equal(35.6762, meta.Values.Single().Location!.Value.Latitude, 6);
    }

    [Fact]
    public async Task Sidecar_mode_leaves_raw_untouched_and_tags_companion_jpeg()
    {
        await using var et = await TestExifTool.RequireAsync();
        using var dir = new TempDir();
        byte[] fakeRaw = [1, 2, 3, 4, 5, 6, 7, 8];
        File.WriteAllBytes(dir.File("IMG_9.ORF"), fakeRaw);
        var jpg = TestFiles.CreateJpeg(dir.File("IMG_9.JPG"));
        var photo = new PhotoSource(dir.File("IMG_9.ORF"), [jpg]);
        var where = new GeoPoint(51.49834043, -0.26536813, 8.3);

        var r = await GeoWriter.WriteAsync(et, photo, new GeoTagChange(where), new WriteOptions { Target = WriteTarget.Sidecar });
        Assert.True(r.Success, string.Join("; ", r.Errors));
        Assert.Equal(fakeRaw, File.ReadAllBytes(dir.File("IMG_9.ORF")));

        string sidecar = dir.File("IMG_9.xmp");
        Assert.True(File.Exists(sidecar));
        Assert.Contains(sidecar, photo.SidecarPaths);

        var fromSidecar = (await MetadataReader.ReadSidecarsAsync(et, [sidecar]))[Path.GetFullPath(sidecar)].Location!.Value;
        Assert.Equal(where.Latitude, fromSidecar.Latitude, 5);
        Assert.Equal(where.Longitude, fromSidecar.Longitude, 5);
        Assert.Equal("ORF", await TestExifTool.RunAsync("-s3", "-XMP-photoshop:SidecarForExtension", sidecar));

        var fromJpeg = (await MetadataReader.ReadMediaAsync(et, [jpg]))[Path.GetFullPath(jpg)].Location!.Value;
        Assert.Equal(where.Latitude, fromJpeg.Latitude, 6);

        // Updating an existing sidecar keeps other metadata (e.g. a Lightroom colour label).
        await TestExifTool.RunAsync("-overwrite_original", "-XMP-xmp:Label=Green", sidecar);
        var moved = new GeoPoint(48.8584, 2.2945);
        r = await GeoWriter.WriteAsync(et, photo, new GeoTagChange(moved), new WriteOptions { Target = WriteTarget.Sidecar });
        Assert.True(r.Success, string.Join("; ", r.Errors));
        Assert.Equal("Green", await TestExifTool.RunAsync("-s3", "-XMP-xmp:Label", sidecar));
        fromSidecar = (await MetadataReader.ReadSidecarsAsync(et, [sidecar]))[Path.GetFullPath(sidecar)].Location!.Value;
        Assert.Equal(48.8584, fromSidecar.Latitude, 5);
    }

    [Fact]
    public async Task Writes_into_a_real_camera_raw()
    {
        string? sample = Environment.GetEnvironmentVariable("GEOTAGGER_SAMPLE_RAW");
        if (string.IsNullOrEmpty(sample) || !File.Exists(sample)) Assert.Skip("Set GEOTAGGER_SAMPLE_RAW to a raw file to run this test.");

        await using var et = await TestExifTool.RequireAsync();
        using var dir = new TempDir();
        string copy = dir.File(Path.GetFileName(sample!));
        File.Copy(sample!, copy);
        var where = new GeoPoint(51.4983404, -0.2653681, 8.3);

        var r = await GeoWriter.WriteAsync(et, new PhotoSource(copy), new GeoTagChange(where), new WriteOptions());
        Assert.True(r.Success, string.Join("; ", r.Errors));

        var read = (await MetadataReader.ReadMediaAsync(et, [copy]))[Path.GetFullPath(copy)];
        Assert.Equal(where.Latitude, read.Location!.Value.Latitude, 6);
        Assert.Equal(where.Longitude, read.Location!.Value.Longitude, 6);
        Assert.NotNull(read.DateTaken);
        string validate = await TestExifTool.RunAsync("-s3", "-validate", copy);
        Assert.True(validate == "OK" || validate.Contains("minor"), validate);
    }

    [Fact]
    public async Task Downloads_exiftool_when_enabled()
    {
        if (Environment.GetEnvironmentVariable("GEOTAGGER_TEST_DOWNLOAD") != "1")
            Assert.Skip("Set GEOTAGGER_TEST_DOWNLOAD=1 to test downloading exiftool from exiftool.org.");

        using var dir = new TempDir();
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Geotagger-tests/1.0");
        string exe = await ExifToolInstaller.InstallAsync(http, Path.Combine(dir.Path, "exiftool"));
        string? version = await ExifToolLocator.GetVersionAsync(ExifToolLocator.ToLocation(exe));
        Assert.NotNull(version);
        Assert.Equal((await ExifToolInstaller.GetLatestVersionAsync(http)), version);
    }
}
