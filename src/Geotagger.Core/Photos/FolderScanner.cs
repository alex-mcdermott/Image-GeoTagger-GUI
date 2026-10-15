namespace Geotagger.Core.Photos;

public sealed record FolderScanResult(IReadOnlyList<PhotoSource> Photos, IReadOnlyList<string> GpxFiles);

public static class FolderScanner
{
    /// <summary>
    /// Lists supported photos in <paramref name="folder"/>. With <paramref name="pairRawWithJpeg"/>,
    /// files sharing a base name with a raw become companions of that raw instead of separate photos.
    /// GPX files found alongside are reported so the UI can offer to load them.
    /// </summary>
    public static FolderScanResult Scan(string folder, bool recursive = false, bool pairRawWithJpeg = true)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        var files = Directory.EnumerateFiles(folder, "*", options).ToList();
        var gpx = files.Where(f => Path.GetExtension(f).Equals(".gpx", StringComparison.OrdinalIgnoreCase))
                       .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

        var xmpByKey = files
            .Where(f => Path.GetExtension(f).Equals(".xmp", StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => SidecarKey(f), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var media = files.Where(f => SupportedFormats.IsSupported(f) && !Path.GetFileName(f).StartsWith("._", StringComparison.Ordinal));
        var photos = new List<PhotoSource>();

        foreach (var group in media.GroupBy(f => Path.Combine(Path.GetDirectoryName(f)!, Path.GetFileNameWithoutExtension(f)),
                     StringComparer.OrdinalIgnoreCase))
        {
            var members = group.OrderBy(Rank).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            bool hasRaw = SupportedFormats.IsRaw(members[0]);

            if (pairRawWithJpeg && hasRaw)
            {
                var primary = members[0];
                photos.Add(new PhotoSource(primary, members.Skip(1).ToList(), FindSidecars(primary, xmpByKey)));
            }
            else
            {
                foreach (var f in members)
                    photos.Add(new PhotoSource(f, [], SupportedFormats.IsRaw(f) ? FindSidecars(f, xmpByKey) : []));
            }
        }

        photos.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.PrimaryPath, b.PrimaryPath));
        return new FolderScanResult(photos, gpx);
    }

    // Raws first, then JPEG, then others.
    private static int Rank(string path)
    {
        if (SupportedFormats.IsRaw(path)) return 0;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" ? 1 : 2;
    }

    // "IMG_1.xmp" and "IMG_1.CR2.xmp" both map back to "dir/IMG_1" or "dir/IMG_1.CR2".
    private static string SidecarKey(string xmpPath) =>
        Path.Combine(Path.GetDirectoryName(xmpPath)!, Path.GetFileNameWithoutExtension(xmpPath));

    private static List<string> FindSidecars(string mediaPath, Dictionary<string, List<string>> xmpByKey)
    {
        var result = new List<string>();
        string dir = Path.GetDirectoryName(mediaPath)!;
        // Adobe convention: IMG_1.xmp
        if (xmpByKey.TryGetValue(Path.Combine(dir, Path.GetFileNameWithoutExtension(mediaPath)), out var adobe))
            result.AddRange(adobe);
        // darktable / digiKam convention: IMG_1.CR2.xmp
        if (xmpByKey.TryGetValue(mediaPath, out var full))
            result.AddRange(full);
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
