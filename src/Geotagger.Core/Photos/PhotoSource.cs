namespace Geotagger.Core.Photos;

/// <summary>
/// A logical photo: the primary file (the raw when there is one) plus any companion files
/// with the same base name (e.g. the JPEG of a RAW+JPEG pair) and existing XMP sidecars.
/// </summary>
public sealed class PhotoSource
{
    public PhotoSource(string primaryPath, IReadOnlyList<string>? companions = null, IReadOnlyList<string>? sidecars = null)
    {
        PrimaryPath = Path.GetFullPath(primaryPath);
        CompanionPaths = companions ?? [];
        SidecarPaths = sidecars ?? [];
    }

    public string PrimaryPath { get; }

    public IReadOnlyList<string> CompanionPaths { get; }

    /// <summary>Existing XMP sidecars belonging to the primary file.</summary>
    public IReadOnlyList<string> SidecarPaths { get; private set; }

    public string FileName => Path.GetFileName(PrimaryPath);

    public string Directory => Path.GetDirectoryName(PrimaryPath)!;

    public bool IsRaw => SupportedFormats.IsRaw(PrimaryPath);

    /// <summary>Every media file that should receive the same location.</summary>
    public IEnumerable<string> MediaFiles => CompanionPaths.Prepend(PrimaryPath);

    /// <summary>Adobe-style sidecar path: same folder, same base name, ".xmp".</summary>
    public string DefaultSidecarPath => Path.Combine(Directory, Path.GetFileNameWithoutExtension(PrimaryPath) + ".xmp");

    public void AddSidecar(string path)
    {
        if (!SidecarPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            SidecarPaths = [.. SidecarPaths, path];
    }

    public override string ToString() => FileName;
}
