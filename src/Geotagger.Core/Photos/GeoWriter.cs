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
/// <see cref="CaptureTime"/>, when set (film mode), also rewrites the date taken and the file's
/// created/modified dates; its offset is the photo's local time zone.
/// </summary>
public sealed record GeoTagChange(GeoPoint? Location, DateTimeOffset? GpsTimeUtc = null, DateTimeOffset? CaptureTime = null)
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
        if (SupportedFormats.IsVideo(file)) return BuildVideoArgs(file, change, options);

        // When the capture time changes, the file dates are set explicitly instead of preserved.
        var args = CommonArgs(options, preserveDates: options.PreserveFileDates && change.CaptureTime is null);
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

        if (change.CaptureTime is { } taken)
        {
            args.Add($"-DateTimeOriginal={taken.ToString("yyyy:MM:dd HH:mm:ss", Inv)}");
            args.Add($"-OffsetTimeOriginal={FormatOffset(taken.Offset)}");
            args.Add("-SubSecTimeOriginal=");
            args.AddRange(FileDateArgs(taken));
        }
        args.Add(file);
        return args;
    }

    /// <summary>
    /// Arguments for QuickTime/MP4 videos. Players and libraries read GPS from different places, so
    /// every common one is written: Apple's Keys location (Photos, macOS), ©xyz user data (Google
    /// Photos, Windows, Android), XMP (Lightroom, Bridge) and the camera's own embedded EXIF GPS
    /// block when it has one (e.g. Fujifilm). QuickTime dates are stored as UTC.
    /// </summary>
    public static List<string> BuildVideoArgs(string file, GeoTagChange change, WriteOptions options)
    {
        var args = CommonArgs(options, preserveDates: options.PreserveFileDates && change.CaptureTime is null);
        args.Add("-api");
        args.Add("QuickTimeUTC=1");

        if (change.Location is { } p)
        {
            string lat = p.Latitude.ToString("0.########", Inv), lon = p.Longitude.ToString("0.########", Inv);
            string coords = p.Altitude is { } a ? $"{lat}, {lon}, {a.ToString("0.###", Inv)}" : $"{lat}, {lon}";
            args.Add($"-Keys:GPSCoordinates={coords}");
            args.Add($"-UserData:GPSCoordinates={coords}");
            args.Add($"-XMP-exif:GPSLatitude={lat}");
            args.Add($"-XMP-exif:GPSLongitude={lon}");
            // Embedded camera GPS (only updated if the camera wrote one; nothing is created otherwise).
            args.Add($"-GPS:GPSLatitude={Math.Abs(p.Latitude).ToString("0.########", Inv)}");
            args.Add($"-GPS:GPSLatitudeRef={(p.Latitude >= 0 ? "N" : "S")}");
            args.Add($"-GPS:GPSLongitude={Math.Abs(p.Longitude).ToString("0.########", Inv)}");
            args.Add($"-GPS:GPSLongitudeRef={(p.Longitude >= 0 ? "E" : "W")}");
            if (p.Altitude is { } alt)
            {
                string abs = Math.Abs(alt).ToString("0.###", Inv), reference = alt >= 0 ? "0" : "1";
                args.AddRange([$"-XMP-exif:GPSAltitude={abs}", $"-XMP-exif:GPSAltitudeRef={reference}",
                    $"-GPS:GPSAltitude={abs}", $"-GPS:GPSAltitudeRef={reference}"]);
            }
            else
            {
                args.AddRange(["-XMP-exif:GPSAltitude=", "-XMP-exif:GPSAltitudeRef=", "-GPS:GPSAltitude=", "-GPS:GPSAltitudeRef="]);
            }
            if (change.GpsTimeUtc is { } t)
            {
                var u = t.UtcDateTime;
                args.Add($"-XMP-exif:GPSDateTime={u.ToString("yyyy:MM:dd HH:mm:ss", Inv)}Z");
                args.Add($"-GPS:GPSDateStamp={u.ToString("yyyy:MM:dd", Inv)}");
                args.Add($"-GPS:GPSTimeStamp={u.ToString("HH:mm:ss", Inv)}");
            }
            else
            {
                args.AddRange(["-XMP-exif:GPSDateTime=", "-GPS:GPSDateStamp=", "-GPS:GPSTimeStamp="]);
            }
        }
        else
        {
            args.AddRange(["-Keys:GPSCoordinates=", "-UserData:GPSCoordinates=", "-ItemList:GPSCoordinates=", "-GPS:all="]);
            args.AddRange(XmpGpsDeletes());
            args.Add("-XMP-exif:GPSDateTime=");
        }

        if (change.CaptureTime is { } taken)
        {
            string withZone = FormatDateWithZone(taken);
            // exiftool converts these to UTC (QuickTimeUTC), as the QuickTime spec requires.
            args.AddRange([$"-QuickTime:CreateDate={withZone}", $"-QuickTime:ModifyDate={withZone}",
                $"-Track*Date={withZone}", $"-Media*Date={withZone}", $"-Keys:CreationDate={withZone}",
                $"-XMP-exif:DateTimeOriginal={withZone}",
                $"-ExifIFD:DateTimeOriginal={taken.ToString("yyyy:MM:dd HH:mm:ss", Inv)}"]);
            args.AddRange(FileDateArgs(taken));
        }
        args.Add(file);
        return args;
    }

    /// <summary>Sets the file system's modified (and, on Windows, created) date without touching the contents.</summary>
    public static List<string> BuildFileDateArgs(string file, DateTimeOffset taken) =>
        ["-charset", "filename=utf8", .. FileDateArgs(taken), file];

    /// <summary>macOS keeps the creation date separately; exiftool sets it via MacOS:FileCreateDate.</summary>
    public static List<string> BuildMacCreateDateArgs(string file, DateTimeOffset taken) =>
        ["-charset", "filename=utf8", $"-MacOS:FileCreateDate={FormatDateWithZone(taken)}", file];

    private static IEnumerable<string> FileDateArgs(DateTimeOffset taken)
    {
        yield return $"-FileModifyDate={FormatDateWithZone(taken)}";
        if (OperatingSystem.IsWindows()) yield return $"-FileCreateDate={FormatDateWithZone(taken)}";
    }

    private static string FormatDateWithZone(DateTimeOffset t) =>
        t.ToString("yyyy:MM:dd HH:mm:ss", Inv) + FormatOffset(t.Offset);

    private static string FormatOffset(TimeSpan offset) =>
        (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", Inv);

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

        if (change.CaptureTime is { } taken)
        {
            string value = FormatDateWithZone(taken);
            args.Add($"-XMP-exif:DateTimeOriginal={value}");
            args.Add($"-XMP-photoshop:DateCreated={value}");
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

    private static List<string> CommonArgs(WriteOptions options, bool preserveDates)
    {
        var args = new List<string> { "-charset", "filename=utf8" };
        // In-place keeps the file's identity (cloud sync, ACLs, macOS extended attributes).
        if (!options.KeepBackups) args.Add("-overwrite_original_in_place");
        if (preserveDates) args.Add("-P");
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
        var targets = PlanTargets(photo, options).ToList();

        foreach (var (path, isSidecar, exists) in targets)
        {
            // Removing GPS from a sidecar that doesn't exist yet is a no-op.
            if (isSidecar && !exists && change.Location is null && change.CaptureTime is null) continue;

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

        if (change.CaptureTime is { } taken)
        {
            // Raw files left untouched in sidecar mode still get their file dates set (contents unchanged).
            var embedded = targets.Where(t => !t.IsSidecar).Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var media in photo.MediaFiles.Where(m => !embedded.Contains(m)))
            {
                var r = await exifTool.ExecuteAsync(BuildFileDateArgs(media, taken), ct).ConfigureAwait(false);
                foreach (var e in r.ErrorLines) errors.Add($"{Path.GetFileName(media)}: {e}");
            }

            // Best effort: the macOS creation date needs Apple's SetFile tool, which may not be installed.
            if (OperatingSystem.IsMacOS())
                foreach (var media in photo.MediaFiles)
                    await exifTool.ExecuteAsync(BuildMacCreateDateArgs(media, taken), ct).ConfigureAwait(false);
        }

        return new WriteResult(photo, errors.Count == 0, errors, warnings, written);
    }
}
