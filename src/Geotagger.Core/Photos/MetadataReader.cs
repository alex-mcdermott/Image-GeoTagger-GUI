using System.Globalization;
using System.Text.Json;
using Geotagger.Core.ExifTool;
using Geotagger.Core.Geo;

namespace Geotagger.Core.Photos;

public sealed record PhotoMetadata
{
    public required string Path { get; init; }
    public DateTime? DateTaken { get; init; }
    public TimeSpan? TimeZoneOffset { get; init; }
    public GeoPoint? Location { get; init; }
    public int Orientation { get; init; } = 1;
    public string? Make { get; init; }
    public string? Model { get; init; }
    public string? Lens { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }

    public string? Camera
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Model)) return Make;
            if (string.IsNullOrWhiteSpace(Make)) return Model;
            // Avoid "Canon Canon EOS R5" / "OLYMPUS IMAGING CORP. E-M1".
            string firstWord = Make!.Split(' ')[0];
            return Model!.StartsWith(firstWord, StringComparison.OrdinalIgnoreCase) ? Model : $"{FriendlyMake(Make)} {Model}";
        }
    }

    private static string FriendlyMake(string make)
    {
        string m = make.Trim();
        foreach (var suffix in new[] { " IMAGING CORP.", " CORPORATION", " Corporation", " CORP.", " Co., Ltd.", " COMPANY" })
            if (m.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) m = m[..^suffix.Length];
        return m.Length > 1 && m.All(c => !char.IsLetter(c) || char.IsUpper(c))
            ? char.ToUpperInvariant(m[0]) + m[1..].ToLowerInvariant()
            : m;
    }
}

/// <summary>What an XMP sidecar says about a photo (null when it says nothing).</summary>
public sealed record SidecarMetadata(GeoPoint? Location, DateTime? DateTaken, TimeSpan? TimeZoneOffset);

/// <summary>Reads the metadata Geotagger needs from media files and XMP sidecars via exiftool.</summary>
public static class MetadataReader
{
    private const int BatchSize = 400;

    public static IReadOnlyList<string> BuildMediaArgs(IEnumerable<string> files) =>
    [
        "-json", "-n", "-fast2", "-charset", "filename=utf8",
        "-DateTimeOriginal", "-CreateDate", "-SubSecTimeOriginal", "-OffsetTimeOriginal", "-OffsetTime",
        "-Composite:GPSLatitude", "-Composite:GPSLongitude", "-Composite:GPSAltitude",
        "-Orientation", "-Make", "-Model", "-LensModel", "-Composite:ImageSize",
        .. files,
    ];

    public static IReadOnlyList<string> BuildSidecarArgs(IEnumerable<string> files) =>
    [
        "-json", "-n", "-charset", "filename=utf8",
        "-XMP:GPSLatitude", "-XMP:GPSLongitude", "-XMP:GPSAltitude", "-XMP:GPSAltitudeRef", "-XMP:DateTimeOriginal",
        .. files,
    ];

    /// <summary>Reads media files; result is keyed by full path.</summary>
    public static async Task<Dictionary<string, PhotoMetadata>> ReadMediaAsync(ExifToolService exifTool,
        IReadOnlyList<string> files, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var result = new Dictionary<string, PhotoMetadata>(PathComparer);
        var batches = files.Chunk(BatchSize).ToList();
        int done = 0;

        // Batches run in parallel across the exiftool pool.
        var tasks = batches.Select(async batch =>
        {
            var r = await exifTool.ExecuteAsync(BuildMediaArgs(batch), ct).ConfigureAwait(false);
            var parsed = ParseMediaJson(r.Output);
            lock (result)
            {
                foreach (var m in parsed) result[m.Path] = m;
                done += batch.Length;
            }
            progress?.Report(done);
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return result;
    }

    /// <summary>Reads GPS positions and capture dates from XMP sidecars; result is keyed by sidecar path.</summary>
    public static async Task<Dictionary<string, SidecarMetadata>> ReadSidecarsAsync(ExifToolService exifTool,
        IReadOnlyList<string> sidecars, CancellationToken ct = default)
    {
        var result = new Dictionary<string, SidecarMetadata>(PathComparer);
        foreach (var batch in sidecars.Chunk(BatchSize))
        {
            var r = await exifTool.ExecuteAsync(BuildSidecarArgs(batch), ct).ConfigureAwait(false);
            foreach (var (path, data) in ParseSidecarJson(r.Output)) result[path] = data;
        }
        return result;
    }

    public static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static List<PhotoMetadata> ParseMediaJson(string json)
    {
        var list = new List<PhotoMetadata>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        using var doc = JsonDocument.Parse(json);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string? source = GetString(e, "SourceFile");
            if (source is null) continue;

            var date = ParseExifDate(GetString(e, "DateTimeOriginal")) ?? ParseExifDate(GetString(e, "CreateDate"));
            if (date is { } d && GetString(e, "SubSecTimeOriginal") is { Length: > 0 } sub &&
                int.TryParse(sub.Trim().PadRight(3, '0')[..3], out int ms))
                date = d.AddMilliseconds(ms);

            GeoPoint? location = null;
            if (GetDouble(e, "GPSLatitude") is { } lat && GetDouble(e, "GPSLongitude") is { } lon)
            {
                var p = new GeoPoint(lat, lon, GetDouble(e, "GPSAltitude"));
                // 0,0 is almost always a camera writing empty GPS fields rather than a real position.
                if (p.IsValid && !(lat == 0 && lon == 0)) location = p;
            }

            int? width = null, height = null;
            if (GetString(e, "ImageSize") is { } size)
            {
                var parts = size.Split(' ', 'x');
                if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                    (width, height) = (w, h);
            }

            list.Add(new PhotoMetadata
            {
                Path = NormalisePath(source),
                DateTaken = date,
                TimeZoneOffset = ParseOffset(GetString(e, "OffsetTimeOriginal")) ?? ParseOffset(GetString(e, "OffsetTime")),
                Location = location,
                Orientation = (int)(GetDouble(e, "Orientation") ?? 1),
                Make = GetString(e, "Make")?.Trim(),
                Model = GetString(e, "Model")?.Trim(),
                Lens = GetString(e, "LensModel")?.Trim(),
                Width = width,
                Height = height,
            });
        }
        return list;
    }

    internal static IEnumerable<(string Path, SidecarMetadata Data)> ParseSidecarJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;
        using var doc = JsonDocument.Parse(json);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string? source = GetString(e, "SourceFile");
            if (source is null) continue;
            GeoPoint? location = null;
            if (GetDouble(e, "GPSLatitude") is { } lat && GetDouble(e, "GPSLongitude") is { } lon)
            {
                double? alt = GetDouble(e, "GPSAltitude");
                if (alt is { } a && GetDouble(e, "GPSAltitudeRef") == 1) alt = -Math.Abs(a);
                var p = new GeoPoint(lat, lon, alt);
                if (p.IsValid) location = p;
            }
            // XMP dates look like "2026:10:14 10:05:17+01:00" (optionally with sub-seconds).
            string? date = GetString(e, "DateTimeOriginal");
            yield return (NormalisePath(source), new SidecarMetadata(location, ParseExifDate(date), ParseTrailingOffset(date)));
        }
    }

    public static string NormalisePath(string exiftoolPath) => Path.GetFullPath(exiftoolPath);

    internal static DateTime? ParseExifDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        // "2026:10:14 10:05:17" possibly followed by sub-seconds or a zone.
        if (s.Length >= 19 &&
            DateTime.TryParseExact(s[..19], "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        return null;
    }

    internal static TimeSpan? ParseTrailingOffset(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.EndsWith('Z')) return TimeSpan.Zero;
        return s.Length >= 25 ? ParseOffset(s[^6..]) : null;
    }

    internal static TimeSpan? ParseOffset(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s == "Z") return TimeSpan.Zero;
        if (s.Length >= 6 && (s[0] == '+' || s[0] == '-') &&
            int.TryParse(s.AsSpan(1, 2), out int h) && int.TryParse(s.AsSpan(4, 2), out int m) && h <= 14 && m < 60)
        {
            var span = new TimeSpan(h, m, 0);
            return s[0] == '-' ? -span : span;
        }
        return null;
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static double? GetDouble(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)) return d;
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return null;
    }
}
