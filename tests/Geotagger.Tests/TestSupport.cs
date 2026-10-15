using System.Net;
using Geotagger.Core.ExifTool;
using SkiaSharp;

namespace Geotagger.Tests;

/// <summary>A temporary directory deleted after the test.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "geotagger-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

public static class TestFiles
{
    public static string DataPath(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);

    /// <summary>Writes a small solid-colour JPEG.</summary>
    public static string CreateJpeg(string path, int width = 64, int height = 48, SKColor? color = null)
    {
        using var bmp = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(color ?? SKColors.SteelBlue);
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(0, 0, width / 2f, height / 2f, paint); // top-left marker for orientation checks
        }
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        using var fs = File.Create(path);
        data.SaveTo(fs);
        return path;
    }
}

public static class TestExifTool
{
    private static readonly Lazy<Task<ExifToolLocation?>> Found = new(async () =>
        (await ExifToolLocator.FindAsync(null))?.Location);

    /// <summary>Returns a running exiftool service, or skips the test when exiftool isn't installed.</summary>
    public static async Task<ExifToolService> RequireAsync()
    {
        var location = await Found.Value;
        if (location is null) Assert.Skip("exiftool is not installed on this machine.");
        return await ExifToolService.CreateAsync(location!, poolSize: 2);
    }

    /// <summary>Runs exiftool once on the command line (independently of the code under test).</summary>
    public static async Task<string> RunAsync(params string[] args)
    {
        var location = await Found.Value ?? throw new InvalidOperationException("exiftool missing");
        var psi = new System.Diagnostics.ProcessStartInfo(location.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in location.PrefixArguments) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("-charset");
        psi.ArgumentList.Add("filename=utf8");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        string output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return output.Trim();
    }
}

/// <summary>Serves a plain grey PNG for every request so map tests never touch the network.</summary>
public sealed class FakeTileHandler : HttpMessageHandler
{
    private static readonly byte[] Tile = CreateTile();

    public int Requests;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Requests);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Tile) });
    }

    private static byte[] CreateTile()
    {
        using var bmp = new SKBitmap(256, 256);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(new SKColor(0xD8, 0xE4, 0xD0));
            using var p = new SKPaint { Color = new SKColor(0xB0, 0xC0, 0xA8), StrokeWidth = 2, Style = SKPaintStyle.Stroke };
            c.DrawRect(0, 0, 256, 256, p);
        }
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }
}
