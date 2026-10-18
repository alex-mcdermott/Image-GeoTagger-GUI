using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Geotagger.Core.Geo;
using Geotagger.Core.Photos;
using Geotagger.Services;

namespace Geotagger.ViewModels;

public sealed record ThemeOption(string Value, string Name)
{
    public override string ToString() => Name;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IUiServices _ui;
    private readonly Func<string> _cacheInfo;
    private readonly Action _clearCaches;

    public SettingsViewModel(AppSettings s, string exifToolStatus, IUiServices ui, Func<string> cacheInfo, Action clearCaches)
    {
        _ui = ui;
        _cacheInfo = cacheInfo;
        _clearCaches = clearCaches;
        _writeTargetIndex = (int)s.WriteTarget;
        _keepBackups = s.KeepBackups;
        _preserveFileDates = s.PreserveFileDates;
        _pairRawWithJpeg = s.PairRawWithJpeg;
        _includeSubfolders = s.IncludeSubfolders;
        _offerFolderGpx = s.OfferFolderGpx;
        _exifToolPath = s.ExifToolPath ?? "";
        _homeText = s.HomeLatitude is { } lat && s.HomeLongitude is { } lon ? CoordinateFormatter.ToDecimal(new GeoPoint(lat, lon)) : "";
        _theme = Themes.FirstOrDefault(t => t.Value == s.Theme) ?? Themes[0];
        ExifToolStatus = exifToolStatus;
        _cacheText = cacheInfo();
    }

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new("System", "Use system setting"),
        new("Light", "Light"),
        new("Dark", "Dark"),
    ];

    public string ExifToolStatus { get; }

    [ObservableProperty] private int _writeTargetIndex;
    [ObservableProperty] private bool _keepBackups;
    [ObservableProperty] private bool _preserveFileDates;
    [ObservableProperty] private bool _pairRawWithJpeg;
    [ObservableProperty] private bool _includeSubfolders;
    [ObservableProperty] private bool _offerFolderGpx;
    [ObservableProperty] private string _exifToolPath;
    [ObservableProperty] private ThemeOption _theme;
    [ObservableProperty] private string _cacheText;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HomeError))] private string _homeText;

    public string? HomeError =>
        string.IsNullOrWhiteSpace(HomeText) || CoordinateParser.TryParse(HomeText, out _)
            ? null
            : "Couldn't read those coordinates. Try \"51.5007, -0.1246\" or paste a map link.";

    public bool CanSave => HomeError is null;

    [RelayCommand]
    private void ClearHome() => HomeText = "";

    public string WriteTargetDescription => (WriteTarget)WriteTargetIndex switch
    {
        WriteTarget.Embedded => "GPS tags are written into each file (RAW, JPEG and video). Existing XMP sidecars are updated to match.",
        WriteTarget.Sidecar => "RAW files are never modified: locations go into .xmp sidecars (Lightroom/Adobe convention). JPEG/HEIC/TIFF files and videos are tagged directly.",
        _ => "GPS tags are written into each file and into an .xmp sidecar next to each RAW file.",
    };

    partial void OnWriteTargetIndexChanged(int value) => OnPropertyChanged(nameof(WriteTargetDescription));

    [RelayCommand]
    private async Task BrowseExifToolAsync()
    {
        var path = await _ui.PickExifToolAsync();
        if (path is not null) ExifToolPath = path;
    }

    [RelayCommand]
    private void ClearCaches()
    {
        _clearCaches();
        CacheText = _cacheInfo();
    }

    public void ApplyTo(AppSettings s)
    {
        s.WriteTarget = (WriteTarget)WriteTargetIndex;
        s.KeepBackups = KeepBackups;
        s.PreserveFileDates = PreserveFileDates;
        s.PairRawWithJpeg = PairRawWithJpeg;
        s.IncludeSubfolders = IncludeSubfolders;
        s.OfferFolderGpx = OfferFolderGpx;
        s.ExifToolPath = string.IsNullOrWhiteSpace(ExifToolPath) ? null : ExifToolPath.Trim();
        s.Theme = Theme.Value;
        if (CoordinateParser.TryParse(HomeText, out var home))
        {
            s.HomeLatitude = Math.Round(home.Latitude, 7);
            s.HomeLongitude = Math.Round(home.Longitude, 7);
        }
        else if (string.IsNullOrWhiteSpace(HomeText))
        {
            s.HomeLatitude = s.HomeLongitude = null;
        }
    }
}
