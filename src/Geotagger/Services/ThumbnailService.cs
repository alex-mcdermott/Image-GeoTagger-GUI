using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using Geotagger.Core.ExifTool;
using Geotagger.Core.Photos;
using SkiaSharp;

namespace Geotagger.Services;

public sealed record ThumbnailRequest(PhotoSource Photo, int Orientation);

/// <summary>
/// Produces small, correctly rotated JPEG previews for photos. Raw files are never decoded: the
/// camera's embedded preview is pulled out with exiftool and scaled with Skia. Results are cached
/// on disk keyed by path, size and modification time.
/// </summary>
public sealed class ThumbnailService
{
    public const int ThumbnailSize = 320;
    public const int PreviewSize = 1280;
    private const int BatchSize = 8;

    private static readonly string[] PreviewTags = ["PreviewImage", "JpgFromRaw", "ThumbnailImage", "OtherImage"];

    private readonly Func<ExifToolService?> _exifTool;
    private readonly string _cacheDir;

    public ThumbnailService(Func<ExifToolService?> exifTool, string cacheDir)
    {
        _exifTool = exifTool;
        _cacheDir = cacheDir;
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>Generates thumbnails for many photos, calling <paramref name="onReady"/> as each completes.</summary>
    public async Task GenerateAsync(IReadOnlyList<ThumbnailRequest> requests, int size, Action<PhotoSource, Bitmap?> onReady,
        CancellationToken ct)
    {
        var missing = new List<ThumbnailRequest>();
        foreach (var r in requests)
        {
            ct.ThrowIfCancellationRequested();
            string cached = CachePath(r, size);
            var bmp = File.Exists(cached) ? await Task.Run(() => TryLoad(cached), ct).ConfigureAwait(false) : null;
            if (bmp is not null) onReady(r.Photo, bmp);
            else missing.Add(r);
        }

        var exifTool = _exifTool();
        int parallel = Math.Max(1, (exifTool?.PoolSize ?? 2) - 1);
        using var gate = new SemaphoreSlim(parallel);
        var tasks = new List<Task>();
        foreach (var batch in Batches(missing))
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try { await ProcessBatchAsync(exifTool, batch, size, onReady, ct).ConfigureAwait(false); }
                finally { gate.Release(); }
            }, ct));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Gets one larger preview (cached).</summary>
    public async Task<Bitmap?> GetPreviewAsync(ThumbnailRequest request, CancellationToken ct)
    {
        Bitmap? result = null;
        await GenerateAsync([request], PreviewSize, (_, b) => result = b, ct).ConfigureAwait(false);
        return result;
    }

    public long CacheSizeBytes() =>
        Directory.Exists(_cacheDir) ? new DirectoryInfo(_cacheDir).EnumerateFiles().Sum(f => f.Length) : 0;

    public void ClearCache()
    {
        foreach (var f in Directory.EnumerateFiles(_cacheDir))
            try { File.Delete(f); } catch { /* in use */ }
    }

    // Avoid two files with the same name+extension in one exiftool batch (they'd share an output name).
    private static IEnumerable<List<ThumbnailRequest>> Batches(List<ThumbnailRequest> items)
    {
        var current = new List<ThumbnailRequest>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            string name = Path.GetFileName(item.Photo.PrimaryPath);
            if (current.Count >= BatchSize || !names.Add(name))
            {
                if (current.Count > 0) yield return current;
                current = [];
                names.Clear();
                names.Add(name);
            }
            current.Add(item);
        }
        if (current.Count > 0) yield return current;
    }

    private async Task ProcessBatchAsync(ExifToolService? exifTool, List<ThumbnailRequest> batch, int size,
        Action<PhotoSource, Bitmap?> onReady, CancellationToken ct)
    {
        string work = Path.Combine(Path.GetTempPath(), "geotagger-thumbs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            if (exifTool is not null)
            {
                var args = new List<string> { "-charset", "filename=utf8", "-b", "-W!", work.Replace('\\', '/') + "/%f.%e.%t.%s" };
                args.AddRange(PreviewTags.Select(t => "-" + t));
                args.AddRange(batch.Select(b => b.Photo.PrimaryPath));
                try { await exifTool.ExecuteAsync(args, ct).ConfigureAwait(false); }
                catch (ExifToolException) { /* fall back to decoding what we can */ }
            }

            foreach (var item in batch)
            {
                ct.ThrowIfCancellationRequested();
                Bitmap? bitmap = null;
                try
                {
                    string? source = PickSource(work, item.Photo.PrimaryPath, size);
                    if (source is not null)
                    {
                        string cached = CachePath(item, size);
                        if (RenderToCache(source, cached, size, item.Orientation))
                            bitmap = TryLoad(cached);
                    }
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    bitmap = null;
                }
                onReady(item.Photo, bitmap);
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Chooses the smallest extracted image that is still big enough; otherwise the largest.</summary>
    private static string? PickSource(string work, string mediaPath, int size)
    {
        string prefix = Path.GetFileName(mediaPath) + ".";
        var candidates = new List<(string Path, int LongEdge)>();
        foreach (var tag in PreviewTags)
        {
            foreach (var ext in new[] { "jpg", "tif", "png" })
            {
                string p = Path.Combine(work, prefix + tag + "." + ext);
                if (!File.Exists(p)) continue;
                using var codec = SKCodec.Create(p);
                if (codec is null) continue;
                candidates.Add((p, Math.Max(codec.Info.Width, codec.Info.Height)));
            }
        }

        // Rendered formats Skia can decode directly (JPEG, PNG, WebP...).
        if (!SupportedFormats.IsRaw(mediaPath))
        {
            using var codec = SKCodec.Create(mediaPath);
            if (codec is not null) candidates.Add((mediaPath, Math.Max(codec.Info.Width, codec.Info.Height)));
        }

        // Videos rarely embed a decent preview; grab a frame with ffmpeg when it's installed.
        if (SupportedFormats.IsVideo(mediaPath) && candidates.All(c => c.LongEdge < size / 2) &&
            GrabVideoFrame(mediaPath, Path.Combine(work, prefix + "frame.jpg"), size) is { } frame)
        {
            using var codec = SKCodec.Create(frame);
            if (codec is not null) candidates.Add((frame, Math.Max(codec.Info.Width, codec.Info.Height)));
        }

        if (candidates.Count == 0) return null;
        var bigEnough = candidates.Where(c => c.LongEdge >= size).OrderBy(c => c.LongEdge).FirstOrDefault();
        return bigEnough.Path ?? candidates.OrderByDescending(c => c.LongEdge).First().Path;
    }

    private static readonly Lazy<string?> Ffmpeg = new(FindFfmpeg);

    private static string? FindFfmpeg()
    {
        string exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(OperatingSystem.IsWindows() ? [] : new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin" });
        foreach (var d in dirs)
        {
            try
            {
                string p = Path.Combine(d.Trim().Trim('"'), exe);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>Extracts one frame (about a second in, already rotated for display) as a JPEG.</summary>
    private static string? GrabVideoFrame(string video, string output, int size)
    {
        if (Ffmpeg.Value is not { } ffmpeg) return null;
        foreach (var seek in new[] { "1", "0" })
        {
            try
            {
                var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-ss", seek, "-i", video, "-frames:v", "1",
                             "-vf", $"scale='min({size},iw)':-2", "-q:v", "3", output })
                    psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                if (p is null) return null;
                p.StandardOutput.ReadToEndAsync();
                p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(20000)) { try { p.Kill(true); } catch { } return null; }
                if (p.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0) return output;
            }
            catch (Exception) { return null; }
        }
        return null;
    }

    private static bool RenderToCache(string source, string destination, int size, int orientation)
    {
        using var codec = SKCodec.Create(source);
        if (codec is null) return false;

        var info = codec.Info;
        float scale = Math.Min(1f, size / (float)Math.Max(info.Width, info.Height));
        // JPEG can decode at 1/2, 1/4, 1/8 natively, which is much faster than full decode + resize.
        var decodeSize = codec.GetScaledDimensions(scale);
        if (decodeSize.Width < 1 || decodeSize.Height < 1) decodeSize = new SKSizeI(info.Width, info.Height);

        using var decoded = new SKBitmap(new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            // Some codecs can't scale; decode at full size instead.
            using var full = SKBitmap.Decode(codec);
            if (full is null) return false;
            return Finish(full);
        }
        return Finish(decoded);

        bool Finish(SKBitmap bmp)
        {
            int longEdge = Math.Max(bmp.Width, bmp.Height);
            SKBitmap scaled = bmp;
            if (longEdge > size)
            {
                float f = size / (float)longEdge;
                var target = new SKImageInfo(Math.Max(1, (int)Math.Round(bmp.Width * f)), Math.Max(1, (int)Math.Round(bmp.Height * f)),
                    SKColorType.Rgba8888, SKAlphaType.Premul);
                scaled = bmp.Resize(target, new SKSamplingOptions(SKCubicResampler.Mitchell)) ?? bmp;
            }

            using var oriented = ApplyOrientation(scaled, orientation);
            using var image = SKImage.FromBitmap(oriented);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 88);
            if (!ReferenceEquals(scaled, bmp)) scaled.Dispose();
            if (data is null) return false;

            string tmp = destination + ".tmp";
            using (var fs = File.Create(tmp)) data.SaveTo(fs);
            File.Move(tmp, destination, overwrite: true);
            return true;
        }
    }

    /// <summary>Returns a new bitmap rotated/flipped for EXIF orientation 1–8.</summary>
    internal static SKBitmap ApplyOrientation(SKBitmap src, int orientation)
    {
        if (orientation is < 2 or > 8) return src.Copy();
        bool swap = orientation >= 5;
        int w = swap ? src.Height : src.Width;
        int h = swap ? src.Width : src.Height;
        var dst = new SKBitmap(new SKImageInfo(w, h, src.ColorType, src.AlphaType));
        using var canvas = new SKCanvas(dst);
        switch (orientation)
        {
            case 2: canvas.Translate(w, 0); canvas.Scale(-1, 1); break;
            case 3: canvas.Translate(w, h); canvas.RotateDegrees(180); break;
            case 4: canvas.Translate(0, h); canvas.Scale(1, -1); break;
            case 5: canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
            case 6: canvas.Translate(w, 0); canvas.RotateDegrees(90); break;
            case 7: canvas.Translate(w, h); canvas.RotateDegrees(90); canvas.Scale(-1, 1); break;
            case 8: canvas.Translate(0, h); canvas.RotateDegrees(270); break;
        }
        canvas.DrawBitmap(src, 0, 0);
        canvas.Flush();
        return dst;
    }

    private string CachePath(ThumbnailRequest r, int size)
    {
        long length = 0;
        long ticks = 0;
        try
        {
            var fi = new FileInfo(r.Photo.PrimaryPath);
            length = fi.Length;
            ticks = fi.LastWriteTimeUtc.Ticks;
        }
        catch { /* file vanished */ }

        string key = $"{r.Photo.PrimaryPath.ToLowerInvariant()}|{length}|{ticks}|{size}|{r.Orientation}|v1";
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_cacheDir, hash + ".jpg");
    }

    private static Bitmap? TryLoad(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return new Bitmap(fs);
        }
        catch
        {
            return null;
        }
    }
}
