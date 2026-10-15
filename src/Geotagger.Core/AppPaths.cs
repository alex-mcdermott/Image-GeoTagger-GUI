namespace Geotagger.Core;

/// <summary>Per-user folders for settings, caches and downloaded tools.</summary>
public static class AppPaths
{
    public const string AppName = "Geotagger";

    private static string? _overrideRoot;

    /// <summary>Redirects all app folders (used by tests).</summary>
    public static void OverrideRoot(string? root) => _overrideRoot = root;

    public static string DataDirectory => Ensure(_overrideRoot is not null
        ? Path.Combine(_overrideRoot, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName));

    public static string SettingsDirectory => Ensure(_overrideRoot is not null
        ? Path.Combine(_overrideRoot, "settings")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName));

    public static string CacheDirectory
    {
        get
        {
            if (_overrideRoot is not null) return Ensure(Path.Combine(_overrideRoot, "cache"));
            if (OperatingSystem.IsMacOS())
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Ensure(Path.Combine(home, "Library", "Caches", AppName));
            }
            return Ensure(Path.Combine(DataDirectory, "Cache"));
        }
    }

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
