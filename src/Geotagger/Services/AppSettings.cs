using System.Text.Json;
using System.Text.Json.Serialization;
using Geotagger.Core;
using Geotagger.Core.Photos;

namespace Geotagger.Services;

public sealed class AppSettings
{
    public string? ExifToolPath { get; set; }
    public WriteTarget WriteTarget { get; set; } = WriteTarget.Embedded;
    public bool KeepBackups { get; set; }
    public bool PreserveFileDates { get; set; } = true;
    public bool PairRawWithJpeg { get; set; } = true;
    public bool IncludeSubfolders { get; set; }
    public bool OfferFolderGpx { get; set; } = true;
    public string MapStyle { get; set; } = "streets";
    public string Theme { get; set; } = "System";
    public string? CameraTimeZoneId { get; set; }
    public bool PreferEmbeddedOffset { get; set; } = true;
    public double ToleranceMinutes { get; set; } = 10;
    public bool ReplaceExistingLocations { get; set; }
    public string? LastFolder { get; set; }
    public List<string> RecentFolders { get; set; } = [];
    public double? MapLatitude { get; set; }
    public double? MapLongitude { get; set; }
    public double? MapZoom { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public double? FilmstripHeight { get; set; }
    public double? SidePaneWidth { get; set; }

    [JsonIgnore]
    public WriteOptions WriteOptions => new()
    {
        Target = WriteTarget,
        KeepBackups = KeepBackups,
        PreserveFileDates = PreserveFileDates,
    };

    public void AddRecentFolder(string folder)
    {
        RecentFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        RecentFolders.Insert(0, folder);
        if (RecentFolders.Count > 8) RecentFolders.RemoveRange(8, RecentFolders.Count - 8);
        LastFolder = folder;
    }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string FilePath => Path.Combine(AppPaths.SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch
        {
            // A corrupt settings file should never stop the app from starting.
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // Non-fatal.
        }
    }
}
