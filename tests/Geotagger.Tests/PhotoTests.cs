using Geotagger.Core.Geo;
using Geotagger.Core.Photos;
using Geotagger.Services;
using SkiaSharp;

namespace Geotagger.Tests;

public class FolderScannerTests
{
    private static TempDir MakeFolder()
    {
        var dir = new TempDir();
        foreach (var f in new[]
                 {
                     "IMG_1.CR2", "IMG_1.JPG", "IMG_1.xmp", "IMG_2.NEF", "IMG_2.NEF.xmp", "IMG_3.jpg", "IMG_4.HEIC",
                     "notes.txt", "track.gpx", "._IMG_5.jpg",
                 })
            File.WriteAllText(dir.File(f), "x");
        Directory.CreateDirectory(dir.File("sub"));
        File.WriteAllText(Path.Combine(dir.Path, "sub", "IMG_6.dng"), "x");
        return dir;
    }

    [Fact]
    public void Pairs_raw_with_jpeg_and_finds_sidecars()
    {
        using var dir = MakeFolder();
        var result = FolderScanner.Scan(dir.Path);

        Assert.Equal(["IMG_1.CR2", "IMG_2.NEF", "IMG_3.jpg", "IMG_4.HEIC"], result.Photos.Select(p => p.FileName));
        var img1 = result.Photos[0];
        Assert.Equal("IMG_1.JPG", Path.GetFileName(Assert.Single(img1.CompanionPaths)));
        Assert.Equal("IMG_1.xmp", Path.GetFileName(Assert.Single(img1.SidecarPaths)));
        Assert.Equal("IMG_2.NEF.xmp", Path.GetFileName(Assert.Single(result.Photos[1].SidecarPaths)));
        Assert.Empty(result.Photos[2].SidecarPaths);
        Assert.Equal("track.gpx", Path.GetFileName(Assert.Single(result.GpxFiles)));
    }

    [Fact]
    public void Can_list_pairs_separately_and_recurse()
    {
        using var dir = MakeFolder();
        Assert.Equal(5, FolderScanner.Scan(dir.Path, pairRawWithJpeg: false).Photos.Count);
        Assert.Contains(FolderScanner.Scan(dir.Path, recursive: true).Photos, p => p.FileName == "IMG_6.dng");
    }
}

public class MetadataReaderTests
{
    private static string JsonPath(string name) => Path.Combine(Path.GetTempPath(), name).Replace('\\', '/');

    [Fact]
    public void Parses_exiftool_json()
    {
        string json = $$"""
            [{"SourceFile":"{{JsonPath("a.ORF")}}","DateTimeOriginal":"2026:10:14 10:05:17","SubSecTimeOriginal":"45",
              "OffsetTimeOriginal":"+01:00","GPSLatitude":51.5,"GPSLongitude":-0.12,"GPSAltitude":-3.5,"Orientation":6,
              "Make":"OLYMPUS IMAGING CORP.","Model":"E-M1","LensModel":"OLYMPUS M.12-40mm F2.8","ImageSize":"4640 3472"},
             {"SourceFile":"{{JsonPath("b.jpg")}}","CreateDate":"2026:10:14 11:00:00","GPSLatitude":0,"GPSLongitude":0,"Make":"Canon","Model":"Canon EOS R5"},
             {"SourceFile":"{{JsonPath("c.jpg")}}","DateTimeOriginal":"0000:00:00 00:00:00","OffsetTime":"-05:30"}]
            """;
        var list = MetadataReader.ParseMediaJson(json);
        Assert.Equal(3, list.Count);

        var a = list[0];
        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "a.ORF")), a.Path);
        Assert.Equal(new DateTime(2026, 10, 14, 10, 5, 17, 450), a.DateTaken);
        Assert.Equal(TimeSpan.FromHours(1), a.TimeZoneOffset);
        Assert.Equal(new GeoPoint(51.5, -0.12, -3.5), a.Location);
        Assert.Equal(6, a.Orientation);
        Assert.Equal("Olympus E-M1", a.Camera);
        Assert.Equal(4640, a.Width);

        Assert.Equal(new DateTime(2026, 10, 14, 11, 0, 0), list[1].DateTaken);
        Assert.Null(list[1].Location); // 0,0 is treated as "no GPS"
        Assert.Equal("Canon EOS R5", list[1].Camera);

        Assert.Null(list[2].DateTaken);
        Assert.Equal(TimeSpan.FromMinutes(-330), list[2].TimeZoneOffset);
    }

    [Fact]
    public void Parses_sidecar_json_with_altitude_reference()
    {
        string json = $$"""[{"SourceFile":"{{JsonPath("a.xmp")}}","GPSLatitude":51.49,"GPSLongitude":-0.26,"GPSAltitude":5,"GPSAltitudeRef":1},{"SourceFile":"{{JsonPath("b.xmp")}}"}]""";
        var list = MetadataReader.ParseSidecarJson(json).ToList();
        Assert.Equal(new GeoPoint(51.49, -0.26, -5), list[0].Data.Location);
        Assert.Null(list[1].Data.Location);
    }
}

public class GeoWriterArgumentTests
{
    [Fact]
    public void Embedded_args_write_signed_coordinates_as_value_plus_reference()
    {
        var args = GeoWriter.BuildEmbeddedArgs("photo.orf", new GeoTagChange(new GeoPoint(-33.5, 151.25, -2)), new WriteOptions());
        Assert.Contains("-overwrite_original_in_place", args);
        Assert.Contains("-P", args);
        Assert.Contains("-GPSLatitude=33.5", args);
        Assert.Contains("-GPSLatitudeRef=S", args);
        Assert.Contains("-GPSLongitude=151.25", args);
        Assert.Contains("-GPSLongitudeRef=E", args);
        Assert.Contains("-GPSAltitude=2", args);
        Assert.Contains("-GPSAltitudeRef=1", args);
        Assert.Contains("-GPSDateStamp=", args); // manual placement clears a stale fix time
        Assert.Equal("photo.orf", args[^1]);
    }

    [Fact]
    public void Options_and_removal()
    {
        var keep = GeoWriter.BuildEmbeddedArgs("a.jpg", GeoTagChange.Remove, new WriteOptions { KeepBackups = true, PreserveFileDates = false });
        Assert.DoesNotContain("-overwrite_original_in_place", keep);
        Assert.DoesNotContain("-P", keep);
        Assert.Contains("-GPS:all=", keep);

        var timed = GeoWriter.BuildEmbeddedArgs("a.jpg",
            new GeoTagChange(new GeoPoint(1, 2), new DateTimeOffset(2024, 6, 1, 9, 30, 15, TimeSpan.Zero)), new WriteOptions());
        Assert.Contains("-GPSDateStamp=2024:06:01", timed);
        Assert.Contains("-GPSTimeStamp=09:30:15", timed);
        Assert.Contains("-GPSAltitude=", timed);
    }

    [Fact]
    public void New_sidecar_is_created_from_scratch()
    {
        var args = GeoWriter.BuildSidecarArgs("IMG_1.xmp", exists: false, "IMG_1.CR2", new GeoTagChange(new GeoPoint(51.5, -0.1)), new WriteOptions());
        Assert.Contains("-XMP-exif:GPSLatitude=51.5", args);
        Assert.Contains("-XMP-exif:GPSLongitude=-0.1", args);
        Assert.Contains("-XMP-photoshop:SidecarForExtension=CR2", args);
        Assert.Equal(["-o", "IMG_1.xmp"], args[^2..]);
    }

    [Fact]
    public void Plans_targets_for_each_write_mode()
    {
        using var dir = new TempDir();
        foreach (var f in new[] { "A.ORF", "A.JPG", "A.xmp", "B.ORF", "C.jpg" }) File.WriteAllText(dir.File(f), "x");
        var withSidecar = new PhotoSource(dir.File("A.ORF"), [dir.File("A.JPG")], [dir.File("A.xmp")]);
        var noSidecar = new PhotoSource(dir.File("B.ORF"));
        var jpeg = new PhotoSource(dir.File("C.jpg"));

        string Names(PhotoSource p, WriteTarget t) => string.Join(",", GeoWriter.PlanTargets(p, new WriteOptions { Target = t })
            .Select(x => Path.GetFileName(x.Path) + (x.IsSidecar ? (x.Exists ? "(xmp)" : "(new xmp)") : "")));

        Assert.Equal("A.ORF,A.JPG,A.xmp(xmp)", Names(withSidecar, WriteTarget.Embedded));
        Assert.Equal("A.JPG,A.xmp(xmp)", Names(withSidecar, WriteTarget.Sidecar));
        Assert.Equal("B.ORF", Names(noSidecar, WriteTarget.Embedded));
        Assert.Equal("B.xmp(new xmp)", Names(noSidecar, WriteTarget.Sidecar));
        Assert.Equal("B.ORF,B.xmp(new xmp)", Names(noSidecar, WriteTarget.Both));
        Assert.Equal("C.jpg", Names(jpeg, WriteTarget.Sidecar));
    }
}

public class ThumbnailOrientationTests
{
    // 4x2 image, red pixel at top-left.
    private static SKBitmap Sample()
    {
        var bmp = new SKBitmap(new SKImageInfo(4, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        bmp.Erase(SKColors.Blue);
        bmp.SetPixel(0, 0, SKColors.Red);
        return bmp;
    }

    [Theory]
    [InlineData(1, 4, 2, 0, 0)]
    [InlineData(2, 4, 2, 3, 0)]
    [InlineData(3, 4, 2, 3, 1)]
    [InlineData(4, 4, 2, 0, 1)]
    [InlineData(5, 2, 4, 0, 0)]
    [InlineData(6, 2, 4, 1, 0)]
    [InlineData(7, 2, 4, 1, 3)]
    [InlineData(8, 2, 4, 0, 3)]
    public void Applies_exif_orientation(int orientation, int width, int height, int redX, int redY)
    {
        using var src = Sample();
        using var result = ThumbnailService.ApplyOrientation(src, orientation);
        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
        Assert.Equal(SKColors.Red, result.GetPixel(redX, redY));
    }
}
