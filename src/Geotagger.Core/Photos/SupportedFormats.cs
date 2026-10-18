namespace Geotagger.Core.Photos;

public static class SupportedFormats
{
    /// <summary>Camera raw formats exiftool can read (and, for nearly all, write).</summary>
    public static readonly IReadOnlySet<string> RawExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".3fr", ".arw", ".cr2", ".cr3", ".crw", ".dcr", ".dng", ".erf", ".fff", ".gpr", ".iiq", ".kdc",
        ".mef", ".mos", ".mrw", ".nef", ".nrw", ".orf", ".ori", ".pef", ".raf", ".raw", ".rw2", ".rwl",
        ".sr2", ".srf", ".srw", ".x3f",
    };

    /// <summary>Rendered image formats that can accompany raws or be geotagged on their own.</summary>
    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".heic", ".heif", ".hif", ".tif", ".tiff", ".png", ".webp", ".avif", ".jxl",
    };

    /// <summary>QuickTime/MP4-family videos, which exiftool can write GPS and dates into.</summary>
    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mov", ".mp4", ".m4v", ".3gp", ".3g2",
    };

    public static bool IsRaw(string path) => RawExtensions.Contains(Path.GetExtension(path));

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    public static bool IsSupported(string path)
    {
        string ext = Path.GetExtension(path);
        return RawExtensions.Contains(ext) || ImageExtensions.Contains(ext) || VideoExtensions.Contains(ext);
    }

    /// <summary>Raw formats Adobe tools expect sidecars for (DNG stores XMP internally).</summary>
    public static bool UsesSidecar(string path) => IsRaw(path) && !Path.GetExtension(path).Equals(".dng", StringComparison.OrdinalIgnoreCase);
}
