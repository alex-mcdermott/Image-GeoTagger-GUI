using System.Globalization;
using Geotagger.Core.ExifTool;
using Geotagger.Core.Geo;

namespace Geotagger.Core.Photos;

public enum WriteTarget
{
    /// <summary>Write EXIF GPS tags into the files themselves (and keep any existing XMP sidecars in sync).</summary>
    Embedded,

    /// <summary>Leave raw files untouched; write XMP sidecars instead. Non-raw files are still tagged directly.</summary>
    Sidecar,

    /// <summary>Embed in the files and also create/update an XMP sidecar for raws.</summary>
    Both,
}

public sealed record WriteOptions
{
    public WriteTarget Target { get; init; } = WriteTarget.Embedded;

    /// <summary>Let exiftool keep "file_original" backups.</summary>
    public bool KeepBackups { get; init; }

    /// <summary>Keep the file's modification date unchanged.</summary>
    public bool PreserveFileDates { get; init; } = true;
}

/// <summary>
/// A location change to apply. <see cref="Location"/> null removes GPS data.
/// <see cref="GpsTimeUtc"/> is written as the GPS fix time when the location came from a track log.
/// </summary>
public sealed record GeoTagChange(GeoPoint? Location, DateTimeOffset? GpsTimeUtc = null)
{
    public static GeoTagChange Remove { get; } = new((GeoPoint?)null);
}

public sealed record WriteResult(PhotoSource Photo, bool Success, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, IReadOnlyList<string> FilesWritten);

public static class GeoWriter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Arguments that write (or remove) EXIF GPS tags in a media file.</summary>
    public static List<string> BuildEmbeddedArgs(string file, GeoTagChange change, WriteOptions options)
    {
        var args = CommonArgs(options);
        if (change.Location is { } p)
        {
            args.Add($"-GPSLatitude={Math.Abs(p.Latitude).ToString("0.########", Inv)}");
            args.Add($"-GPSLatitudeRef={(p.Latitude >= 0 ? "N" : "S")}");
            args.Add($"-GPSLongitude={Math.Abs(p.Longitude).ToString("0.########", Inv)}");
            args.Add($"-GPSLongitudeRef={(p.Longitude >= 0 ? "E" : "W")}");
            if (p.Altitude is { } alt)
            {
                args.Add($"-GPSAltitude={Math.Abs(alt).ToString("0.###", Inv)}");
                args.Add($"-GPSAltitudeRef={(alt >= 0 ? "0" : "1")}");
            }
            else
            {
                // A moved photo must not keep the altitude of its old position.
                args.Add("-GPSAltitude=");
                args.Add("-GPSAltitudeRef=");
            }

            if (change.GpsTimeUtc is { } t)
            {
                var u = t.UtcDateTime;
                args.Add($"-GPSDateStamp={u.ToString("yyyy:MM:dd", Inv)}");
                args.Add($"-GPSTimeStamp={u.ToString("HH:mm:ss", Inv)}");
            }
            else
            {
                args.Add("-GPSDateStamp=");
                args.Add("-GPSTimeStamp=");
            }

            // Drop stale embedded XMP GPS (e.g. written by another app) so EXIF is the single source.
            args.AddRange(XmpGpsDeletes());
        }
        else
        {
            args.Add("-GPS:all=");
            args.AddRange(XmpGpsDeletes());
        }
        args.Add(file);
        return args;
    }

    /// <summary>Arguments that write GPS into an XMP sidecar, creating it when <paramref name="exists"/> is false.</summary>
    public static List<string> BuildSidecarArgs(string sidecarPath, bool exists, string mediaPath, GeoTagChange change, WriteOptions options)
    {
        var args = new List<string> { "-charset", "filename=utf8" };
        if (exists)
        {
            if (!options.KeepBackups) args.Add("-overwrite_original");
        }

        if (change.Location is { } p)
        {
            args.Add($"-XMP-exif:GPSLatitude={p.Latitude.ToString("0.########", Inv)}");
            args.Add($"-XMP-exif:GPSLongitude={p.Longitude.ToString("0.########", Inv)}");
            if (p.Altitude is { } alt)
            {
                args.Add($"-XMP-exif:GPSAltitude={Math.Abs(alt).ToString("0.###", Inv)}");
                args.Add($"-XMP-exif:GPSAltitudeRef={(alt >= 0 ? "0" : "1")}");
            }
            else
            {
                args.Add("-XMP-exif:GPSAltitude=");
                args.Add("-XMP-exif:GPSAltitudeRef=");
            }
            args.Add(change.GpsTimeUtc is { } t
                ? $"-XMP-exif:GPSDateTime={t.UtcDateTime.ToString("yyyy:MM:dd HH:mm:ss", Inv)}Z"
                : "-XMP-exif:GPSDateTime=");
        }
        else
        {
            args.AddRange(XmpGpsDeletes());
            args.Add("-XMP-exif:GPSDateTime=");
        }

        if (exists)
        {
            args.Add(sidecarPath);
        }
        else
        {
            // Creating from scratch: "-o file.xmp" with no source file writes a minimal sidecar.
            args.Add($"-XMP-photoshop:SidecarForExtension={Path.GetExtension(mediaPath).TrimStart('.').ToUpperInvariant()}");
            args.Add("-o");
            args.Add(sidecarPath);
        }
        return args;
    }

    private static IEnumerable<string> XmpGpsDeletes() =>
    [
        "-XMP-exif:GPSLatitude=", "-XMP-exif:GPSLongitude=", "-XMP-exif:GPSAltitude=", "-XMP-exif:GPSAltitudeRef=",
    ];

    private static List<string> CommonArgs(WriteOptions options)
    {
        var args = new List<string> { "-charset", "filename=utf8" };
        // In-place keeps the file's identity (cloud sync, ACLs, macOS extended attributes).
        if (!options.KeepBackups) args.Add("-overwrite_original_in_place");
        if (options.PreserveFileDates) args.Add("-P");
        return args;
    }

    /// <summary>Works out which files a change touches for the given options.</summary>
    public static IEnumerable<(string Path, bool IsSidecar, bool Exists)> PlanTargets(PhotoSource photo, WriteOptions options)
    {
        bool sidecarCapable = SupportedFormats.UsesSidecar(photo.PrimaryPath);

        foreach (var file in photo.MediaFiles)
        {
            bool isRawWithSidecar = SupportedFormats.UsesSidecar(file);
            if (options.Target == WriteTarget.Sidecar && isRawWithSidecar) continue;
            yield return (file, false, true);
        }

        if (!sidecarCapable) yield break;

        var existing = photo.SidecarPaths.Where(File.Exists).ToList();
        foreach (var s in existing) yield return (s, true, true);

        bool wantNew = options.Target is WriteTarget.Sidecar or WriteTarget.Both;
        if (wantNew && existing.Count == 0) yield return (photo.DefaultSidecarPath, true, false);
    }

    /// <summary>Applies a change to every file belonging to <paramref name="photo"/>.</summary>
    public static async Task<WriteResult> WriteAsync(ExifToolService exifTool, PhotoSource photo, GeoTagChange change,
        WriteOptions options, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var written = new List<string>();

        foreach (var (path, isSidecar, exists) in PlanTargets(photo, options).ToList())
        {
            // Removing GPS from a sidecar that doesn't exist yet is a no-op.
            if (isSidecar && !exists && change.Location is null) continue;

            var args = isSidecar
                ? BuildSidecarArgs(path, exists, photo.PrimaryPath, change, options)
                : BuildEmbeddedArgs(path, change, options);

            ExifToolResult r;
            try
            {
                r = await exifTool.ExecuteAsync(args, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                continue;
            }

            foreach (var e in r.ErrorLines) errors.Add($"{Path.GetFileName(path)}: {e}");
            foreach (var w in r.WarningLines) warnings.Add($"{Path.GetFileName(path)}: {w}");

            int ok = r.CountOf("image files updated") + r.CountOf("image files created") + r.CountOf("image files unchanged");
            if (ok > 0 && !r.HasErrors)
            {
                written.Add(path);
                if (isSidecar && !exists) photo.AddSidecar(path);
            }
            else if (!r.HasErrors)
            {
                errors.Add($"{Path.GetFileName(path)}: exiftool did not update the file. {r.Output.Trim()} {r.Errors.Trim()}".Trim());
            }
        }

        return new WriteResult(photo, errors.Count == 0, errors, warnings, written);
    }
}
