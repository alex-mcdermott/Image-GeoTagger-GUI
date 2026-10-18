using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using Geotagger.Core;
using Geotagger.Core.ExifTool;
using Geotagger.Core.Geo;
using Geotagger.Core.Gpx;
using Geotagger.Core.Matching;
using Geotagger.Core.Photos;
using Geotagger.Map;
using Geotagger.Services;

namespace Geotagger.ViewModels;

public enum PhotoFilter { All, WithLocation, WithoutLocation, Unsaved }

public sealed record FilterOption(PhotoFilter Value, string Name)
{
    public override string ToString() => Name;
}

public sealed record TimeZoneOption(string? Id, string Name)
{
    public override string ToString() => Name;
}

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IUiServices _ui;
    private readonly HttpClient _http;
    private ExifToolService? _exifTool;
    private CancellationTokenSource? _thumbnailCts;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _searchCts;
    private TrackLog? _trackLog;
    private readonly Dictionary<PhotoSource, PhotoItemViewModel> _bySource = [];
    private List<string> _folderGpxFiles = [];
    private Func<Task>? _infoAction;

    public MainViewModel(IUiServices ui, AppSettings settings, HttpClient http)
    {
        _ui = ui;
        _http = http;
        Settings = settings;
        Thumbnails = new ThumbnailService(() => _exifTool, Path.Combine(AppPaths.CacheDirectory, "thumbnails"));
        TileCache = new TileCache(http, Path.Combine(AppPaths.CacheDirectory, "tiles"));
        Geocoder = new Geocoder(http);
        Undo.Changed += OnUndoChanged;

        var zones = TimeZoneInfo.GetSystemTimeZones().Select(tz => new TimeZoneOption(tz.Id, tz.DisplayName)).ToList();
        // Settings may come from another OS (Windows vs IANA zone IDs); keep a resolvable zone selectable.
        if (settings.CameraTimeZoneId is { } savedZone && zones.All(z => z.Id != savedZone) &&
            TimeZoneInfo.TryFindSystemTimeZoneById(savedZone, out var resolved))
            zones.Insert(0, new TimeZoneOption(savedZone, resolved.DisplayName));
        TimeZones = [new TimeZoneOption(null, $"This computer: {TimeZoneInfo.Local.DisplayName}"), .. zones];
        Filters =
        [
            new(PhotoFilter.All, "All photos"),
            new(PhotoFilter.WithLocation, "With location"),
            new(PhotoFilter.WithoutLocation, "Without location"),
            new(PhotoFilter.Unsaved, "Unsaved changes"),
        ];

        _selectedTimeZone = TimeZones.FirstOrDefault(t => t.Id == settings.CameraTimeZoneId) ?? TimeZones[0];
        _preferEmbeddedOffset = settings.PreferEmbeddedOffset;
        _toleranceMinutes = settings.ToleranceMinutes;
        _replaceExistingLocations = settings.ReplaceExistingLocations;
        _isFilmMode = settings.FilmMode;
        if (settings.HomeLatitude is { } homeLat && settings.HomeLongitude is { } homeLon)
            _homeLocation = new GeoPoint(homeLat, homeLon);
        _filter = Filters[0];
        _mapStyle = MapStyles.ById(settings.MapStyle);
    }

    // ------------------------------------------------------------------ Services & collections

    public AppSettings Settings { get; }
    public UndoManager Undo { get; } = new();
    public TileCache TileCache { get; }
    public ThumbnailService Thumbnails { get; }
    public Geocoder Geocoder { get; }
    public ExifToolService? ExifTool => _exifTool;

    public ObservableCollection<PhotoItemViewModel> Photos { get; } = [];
    public ObservableCollection<PhotoItemViewModel> VisiblePhotos { get; } = [];
    public ObservableCollection<GpxTrackViewModel> Tracks { get; } = [];
    public ObservableCollection<GeocodeResult> SearchResults { get; } = [];
    public IReadOnlyList<TimeZoneOption> TimeZones { get; }
    public IReadOnlyList<FilterOption> Filters { get; }
    public IReadOnlyList<MapStyle> AllMapStyles => MapStyles.All;

    /// <summary>Raised when the view should change the filmstrip selection to match.</summary>
    public event Action<IReadOnlyList<PhotoItemViewModel>>? SelectionSyncRequested;
    public event Action<IReadOnlyCollection<GeoPoint>, double>? FitRequested;
    public event Action<GeoPoint, double?>? CenterRequested;

    // ------------------------------------------------------------------ Observable state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(IsSingleSelection), nameof(IsMultiSelection), nameof(SelectedPhoto), nameof(PhotoCountText))]
    [NotifyCanExecuteChangedFor(nameof(RemoveLocationCommand), nameof(RevertSelectionCommand), nameof(AutoTagSelectedCommand),
        nameof(ApplyCoordinatesCommand), nameof(CopyCoordinatesCommand), nameof(ShowSelectionOnMapCommand), nameof(PlaceSelectionAtSearchPinCommand),
        nameof(PlaceSelectionAtHomeCommand))]
    private IReadOnlyList<PhotoItemViewModel> _selectedPhotos = [];

    [ObservableProperty] private IReadOnlyList<MapMarker> _mapMarkers = [];
    [ObservableProperty] private IReadOnlyList<MapTrack> _mapTracks = [];
    [ObservableProperty] private MapStyle _mapStyle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchPin))]
    [NotifyCanExecuteChangedFor(nameof(PlaceSelectionAtSearchPinCommand))]
    private GeoPoint? _searchPin;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isSearchResultsOpen;
    [ObservableProperty] private bool _isSearching;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(FolderName), nameof(HasFolder))]
    private string? _currentFolder;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private double _busyProgress;
    [ObservableProperty] private bool _isBusyIndeterminate = true;
    [ObservableProperty] private string _statusText = "Open a folder of photos to get started.";
    [ObservableProperty] private string _exifToolStatus = "ExifTool not found";
    [ObservableProperty] private bool _isExifToolReady;
    [ObservableProperty] private FilterOption _filter;

    [ObservableProperty] private bool _isInfoOpen;
    [ObservableProperty] private string _infoTitle = "";
    [ObservableProperty] private string _infoMessage = "";
    [ObservableProperty] private FAInfoBarSeverity _infoSeverity;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasInfoAction))] private string? _infoActionText;

    [ObservableProperty] private Bitmap? _selectionPreview;
    [ObservableProperty] private string _coordinateInput = "";
    [ObservableProperty] private string? _coordinateError;

    [ObservableProperty] private TimeZoneOption _selectedTimeZone;
    [ObservableProperty] private bool _preferEmbeddedOffset;
    [ObservableProperty] private string _clockCorrectionText = "+00:00:00";
    [ObservableProperty] private string? _clockCorrectionError;
    [ObservableProperty] private double _toleranceMinutes;
    [ObservableProperty] private bool _replaceExistingLocations;
    [ObservableProperty] private string _matchSummary = "";
    [ObservableProperty] private string _trackTimeHint = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(SaveButtonText), nameof(PhotoCountText), nameof(HasPendingChanges))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand))]
    private int _pendingCount;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PhotoCountText))] private int _taggedCount;

    // ------------------------------------------------------------------ Derived state

    public bool HasPhotos => Photos.Count > 0;
    public bool HasFolder => CurrentFolder is not null;
    public bool HasTracks => Tracks.Count > 0;
    public bool HasSearchPin => SearchPin is not null;
    public bool HasInfoAction => !string.IsNullOrEmpty(InfoActionText);
    public bool HasPendingChanges => PendingCount > 0;
    public bool HasSelection => SelectedPhotos.Count > 0;
    public bool IsSingleSelection => SelectedPhotos.Count == 1;
    public bool IsMultiSelection => SelectedPhotos.Count > 1;
    public PhotoItemViewModel? SelectedPhoto => SelectedPhotos.Count == 1 ? SelectedPhotos[0] : null;
    public string? FolderName => CurrentFolder is null ? null : Path.GetFileName(CurrentFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    public string WindowTitle => (FolderName is null ? "Geotagger" : $"{FolderName} — Geotagger") + (PendingCount > 0 ? " •" : "");
    public string SaveButtonText => PendingCount == 0 ? "Save" : $"Save {PendingCount}";
    public IReadOnlyList<string> RecentFolders => Settings.RecentFolders.Where(Directory.Exists).Take(6).ToList();
    public bool HasRecentFolders => RecentFolders.Count > 0;

    public string PhotoCountText
    {
        get
        {
            if (Photos.Count == 0) return "No photos";
            int videos = Photos.Count(p => p.IsVideo), stills = Photos.Count - videos;
            var parts = new List<string>();
            if (stills > 0) parts.Add($"{stills} photo{(stills == 1 ? "" : "s")}");
            if (videos > 0) parts.Add($"{videos} video{(videos == 1 ? "" : "s")}");
            parts.Add($"{TaggedCount} with location");
            if (PendingCount > 0) parts.Add($"{PendingCount} unsaved");
            if (SelectedPhotos.Count > 0) parts.Add($"{SelectedPhotos.Count} selected");
            return string.Join(" · ", parts);
        }
    }

    public string SelectionTitle => SelectedPhotos.Count switch
    {
        0 => "Nothing selected",
        1 => SelectedPhotos[0].FileName,
        _ => $"{SelectedPhotos.Count} photos selected",
    };

    public string SelectionDate
    {
        get
        {
            if (SelectedPhoto is { } p) return p.DateTimeText;
            var dates = SelectedPhotos.Where(p => p.DateTaken.HasValue).Select(p => p.DateTaken!.Value).OrderBy(d => d).ToList();
            if (dates.Count == 0) return "";
            string fmt = (dates[^1] - dates[0]).TotalMinutes < 1 ? "HH:mm:ss" : "HH:mm";
            return dates[0].Date == dates[^1].Date
                ? $"{dates[0].ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture)}, {dates[0].ToString(fmt, CultureInfo.CurrentCulture)}–{dates[^1].ToString(fmt, CultureInfo.CurrentCulture)}"
                : $"{dates[0].ToString("d MMM yyyy", CultureInfo.CurrentCulture)} – {dates[^1].ToString("d MMM yyyy", CultureInfo.CurrentCulture)}";
        }
    }

    public string SelectionCamera => SelectedPhoto is { } p
        ? string.Join(" · ", new[] { p.Camera, p.Lens }.Where(s => !string.IsNullOrWhiteSpace(s)))
        : "";

    public string SelectionFiles
    {
        get
        {
            if (SelectedPhoto is not { } p) return "";
            var names = p.Source.MediaFiles.Concat(p.Source.SidecarPaths).Select(Path.GetFileName);
            return string.Join(", ", names);
        }
    }

    public string SelectionLocation
    {
        get
        {
            if (SelectedPhotos.Count == 0) return "";
            if (SelectedPhoto is { } p)
                return p.EffectiveLocation is { } g ? $"{CoordinateFormatter.ToDecimal(g)}\n{CoordinateFormatter.ToDms(g)}" : "No location";
            int with = SelectedPhotos.Count(x => x.HasLocation);
            var distinct = SelectedPhotos.Where(x => x.EffectiveLocation is not null)
                .Select(x => (Math.Round(x.EffectiveLocation!.Value.Latitude, 6), Math.Round(x.EffectiveLocation!.Value.Longitude, 6))).Distinct().Count();
            if (with == 0) return "None of these photos have a location";
            string where = distinct == 1 ? $"all at {CoordinateFormatter.ToDecimal(SelectedPhotos.First(x => x.HasLocation).EffectiveLocation!.Value)}" : $"{distinct} different places";
            return with == SelectedPhotos.Count ? $"All {with} have a location ({where})" : $"{with} of {SelectedPhotos.Count} have a location ({where})";
        }
    }

    public string SelectionAltitude => SelectedPhoto?.EffectiveLocation?.Altitude is { } a ? CoordinateFormatter.FormatAltitude(a) : "";

    public bool HasSelectionAltitude => SelectionAltitude.Length > 0;

    public string SelectionStatus
    {
        get
        {
            if (SelectedPhoto is { } p) return p.Status == LocationStatus.None ? "" : p.StatusText;
            int pending = SelectedPhotos.Count(x => x.IsPending);
            return pending > 0 ? $"{pending} unsaved change{(pending == 1 ? "" : "s")}" : "";
        }
    }

    public bool SelectionHasPending => SelectedPhotos.Any(p => p.IsPending);

    public CameraClock Clock => new()
    {
        TimeZoneId = SelectedTimeZone.Id,
        PreferEmbeddedOffset = PreferEmbeddedOffset,
        Correction = CameraClock.TryParseCorrection(ClockCorrectionText, out var c) ? c : TimeSpan.Zero,
    };

    // ------------------------------------------------------------------ Startup / exiftool

    public async Task InitializeAsync(string? folder, IReadOnlyList<string> gpxFiles)
    {
        await EnsureExifToolAsync();
        if (folder is not null && Directory.Exists(folder)) await LoadFolderAsync(folder);
        if (gpxFiles.Count > 0) await LoadTracksAsync(gpxFiles);
    }

    public async Task<bool> EnsureExifToolAsync()
    {
        if (_exifTool is not null) return true;

        var found = await ExifToolLocator.FindAsync(Settings.ExifToolPath);
        if (found is { } f)
        {
            _exifTool = await ExifToolService.CreateAsync(f.Location, poolSize: 3);
            IsExifToolReady = true;
            ExifToolStatus = $"ExifTool {f.Version}";
            return true;
        }

        while (true)
        {
            var choice = await _ui.AskExifToolMissingAsync();
            if (choice == ExifToolChoice.Download)
            {
                IsBusy = true;
                IsBusyIndeterminate = false;
                BusyProgress = 0;
                BusyText = "Downloading ExifTool from exiftool.org…";
                try
                {
                    var progress = new Progress<double>(v => BusyProgress = v * 100);
                    await ExifToolInstaller.InstallAsync(_http, progress: progress);
                    IsBusy = false;
                    if (await EnsureExifToolAsync()) return true;
                }
                catch (Exception ex)
                {
                    IsBusy = false;
                    await _ui.ShowMessageAsync("Download failed",
                        $"{ex.Message}\n\nYou can install ExifTool yourself from https://exiftool.org and then choose \"Locate ExifTool\".");
                }
            }
            else if (choice == ExifToolChoice.Browse)
            {
                var path = await _ui.PickExifToolAsync();
                if (path is null) continue;
                if (await ExifToolLocator.GetVersionAsync(ExifToolLocator.ToLocation(path)) is not null)
                {
                    Settings.ExifToolPath = path;
                    SaveSettings();
                    if (await EnsureExifToolAsync()) return true;
                }
                else
                {
                    await _ui.ShowMessageAsync("That isn't ExifTool", $"\"{path}\" didn't respond like ExifTool.");
                }
            }
            else
            {
                StatusText = "ExifTool is needed to read and write photo metadata.";
                return false;
            }
        }
    }

    private async Task RestartExifToolAsync()
    {
        var old = _exifTool;
        _exifTool = null;
        IsExifToolReady = false;
        if (old is not null) await old.DisposeAsync();
        await EnsureExifToolAsync();
    }

    // ------------------------------------------------------------------ Folders

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        var folder = await _ui.PickFolderAsync(CurrentFolder ?? Settings.LastFolder);
        if (folder is not null) await LoadFolderAsync(folder);
    }

    [RelayCommand]
    private async Task OpenRecentAsync(string folder) => await LoadFolderAsync(folder);

    [RelayCommand]
    private async Task ReloadFolderAsync()
    {
        if (CurrentFolder is not null) await LoadFolderAsync(CurrentFolder);
    }

    public async Task LoadFolderAsync(string folder)
    {
        if (!Directory.Exists(folder))
        {
            ShowInfo(FAInfoBarSeverity.Error, "Folder not found", folder);
            return;
        }
        if (!await ConfirmDiscardPendingAsync()) return;
        if (!await EnsureExifToolAsync()) return;

        _thumbnailCts?.Cancel();
        IsBusy = true;
        IsBusyIndeterminate = true;
        BusyText = "Scanning folder…";
        try
        {
            var scan = await Task.Run(() => FolderScanner.Scan(folder, Settings.IncludeSubfolders, Settings.PairRawWithJpeg));
            BusyText = $"Reading metadata from {scan.Photos.Count} photos…";
            IsBusyIndeterminate = false;
            BusyProgress = 0;

            int total = Math.Max(1, scan.Photos.Count);
            var progress = new Progress<int>(n => BusyProgress = 100.0 * n / total);
            var meta = await MetadataReader.ReadMediaAsync(_exifTool!, scan.Photos.Select(p => p.PrimaryPath).ToList(), progress);
            var sidecarPaths = scan.Photos.SelectMany(p => p.SidecarPaths).ToList();
            var sidecars = sidecarPaths.Count > 0
                ? await MetadataReader.ReadSidecarsAsync(_exifTool!, sidecarPaths)
                : new Dictionary<string, SidecarMetadata>();

            var items = scan.Photos
                .Select(src =>
                {
                    var (date, offset) = ResolveDate(src, meta, sidecars);
                    return new PhotoItemViewModel(src, meta.GetValueOrDefault(src.PrimaryPath), ResolveLocation(src, meta, sidecars), date, offset);
                })
                .OrderBy(p => p.DateTaken ?? DateTime.MaxValue)
                .ThenBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Photos.Clear();
            _bySource.Clear();
            foreach (var item in items)
            {
                Photos.Add(item);
                _bySource[item.Source] = item;
            }
            Undo.Clear();
            SelectedPhotos = [];
            SelectionPreview = null;
            CurrentFolder = folder;
            Settings.AddRecentFolder(folder);
            SaveSettings();
            OnPropertyChanged(nameof(HasPhotos));
            OnPropertyChanged(nameof(RecentFolders));
            OnPropertyChanged(nameof(HasRecentFolders));

            Filter = Filters[0];
            ApplyFilter();
            OnPhotosChanged();
            UpdateMatchSummary();

            int located = items.Count(p => p.HasLocation);
            StatusText = $"Loaded {items.Count} photos from {folder}";
            FitToEverything(animate: false);

            var loadedTracks = Tracks.Select(t => t.File.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _folderGpxFiles = scan.GpxFiles.Where(g => !loadedTracks.Contains(g)).ToList();
            if (_folderGpxFiles.Count > 0 && Settings.OfferFolderGpx)
            {
                ShowInfo(FAInfoBarSeverity.Informational, "Track logs found",
                    $"This folder contains {_folderGpxFiles.Count} GPX file{(_folderGpxFiles.Count == 1 ? "" : "s")}. Load {(_folderGpxFiles.Count == 1 ? "it" : "them")} to " +
                    (IsFilmMode ? "place your photos along the route?" : "auto-tag photos by time?"),
                    "Load track logs", () => LoadTracksAsync(_folderGpxFiles));
            }
            else if (items.Count == 0)
            {
                ShowInfo(FAInfoBarSeverity.Warning, "No photos found",
                    Settings.IncludeSubfolders ? "No supported photos in this folder or its subfolders." : "No supported photos in this folder. (Subfolders can be included in Settings.)");
            }
            else
            {
                CloseInfo();
            }

            StartThumbnails(items);
            _ = located;
        }
        catch (Exception ex)
        {
            ShowInfo(FAInfoBarSeverity.Error, "Couldn't open the folder", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static (DateTime? Date, TimeSpan? Offset) ResolveDate(PhotoSource src, Dictionary<string, PhotoMetadata> meta,
        Dictionary<string, SidecarMetadata> sidecars)
    {
        foreach (var s in src.SidecarPaths)
            if (sidecars.TryGetValue(s, out var d) && d.DateTaken is not null) return (d.DateTaken, d.TimeZoneOffset);
        var m = meta.GetValueOrDefault(src.PrimaryPath);
        return (m?.DateTaken, m?.TimeZoneOffset);
    }

    private static GeoPoint? ResolveLocation(PhotoSource src, Dictionary<string, PhotoMetadata> meta, Dictionary<string, SidecarMetadata> sidecars)
    {
        // An XMP sidecar is what Lightroom & co. read for raw files, so it wins when it has a position.
        foreach (var s in src.SidecarPaths)
            if (sidecars.TryGetValue(s, out var d) && d.Location is not null) return d.Location;
        return meta.GetValueOrDefault(src.PrimaryPath)?.Location;
    }

    private void StartThumbnails(IReadOnlyList<PhotoItemViewModel> items)
    {
        _thumbnailCts?.Cancel();
        var cts = _thumbnailCts = new CancellationTokenSource();
        var requests = items.Select(p => new ThumbnailRequest(p.Source, p.Orientation)).ToList();
        _ = Task.Run(async () =>
        {
            try
            {
                await Thumbnails.GenerateAsync(requests, ThumbnailService.ThumbnailSize, (src, bmp) =>
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (cts.IsCancellationRequested || !_bySource.TryGetValue(src, out var vm)) return;
                        vm.Thumbnail = bmp;
                        if (ReferenceEquals(SelectedPhoto, vm) && SelectionPreview is null) SelectionPreview = bmp;
                    }), cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => StatusText = "Some thumbnails couldn't be created: " + ex.Message);
            }
        });
    }

    // ------------------------------------------------------------------ Track logs

    [RelayCommand]
    private async Task AddGpxAsync()
    {
        var files = await _ui.PickGpxFilesAsync(CurrentFolder ?? Settings.LastFolder);
        if (files.Count > 0) await LoadTracksAsync(files);
    }

    public async Task LoadTracksAsync(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        var errors = new List<string>();
        var loaded = new List<GpxFile>();
        IsBusy = true;
        IsBusyIndeterminate = true;
        BusyText = "Reading track logs…";
        try
        {
            foreach (var path in list)
            {
                if (Tracks.Any(t => string.Equals(t.File.FilePath, path, StringComparison.OrdinalIgnoreCase))) continue;
                try
                {
                    var gpx = await Task.Run(() => GpxParser.Load(path));
                    if (gpx.PointCount == 0) errors.Add($"{Path.GetFileName(path)}: no track points");
                    else loaded.Add(gpx);
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        foreach (var gpx in loaded) Tracks.Add(new GpxTrackViewModel(gpx, Tracks.Count));
        _folderGpxFiles.RemoveAll(f => loaded.Any(l => string.Equals(l.FilePath, f, StringComparison.OrdinalIgnoreCase)));
        OnTracksChanged();

        if (errors.Count > 0)
            ShowInfo(FAInfoBarSeverity.Warning, "Some track logs couldn't be read", string.Join("\n", errors));
        else if (loaded.Count > 0 && IsFilmMode)
        {
            ShowInfo(FAInfoBarSeverity.Success, $"Loaded {loaded.Count} track log{(loaded.Count == 1 ? "" : "s")}",
                "Drag photos onto the track to set where and when each one was taken. Hover the track to see the time at any point.");
        }
        else if (loaded.Count > 0)
        {
            int untimed = loaded.Sum(g => g.PointCount - g.TimedPointCount);
            ShowInfo(FAInfoBarSeverity.Success, $"Loaded {loaded.Count} track log{(loaded.Count == 1 ? "" : "s")}",
                MatchSummary + (untimed > 0 ? $" ({untimed} points have no timestamp and can't be used for matching.)" : ""),
                HasPhotos ? "Auto-tag all photos" : null, HasPhotos ? () => { AutoTagAll(); return Task.CompletedTask; } : null);
        }

        if (loaded.Count > 0 && (IsFilmMode || Photos.All(p => !p.HasLocation))) FitToEverything();
    }

    [RelayCommand]
    private void RemoveTrack(GpxTrackViewModel track)
    {
        Tracks.Remove(track);
        OnTracksChanged();
    }

    [RelayCommand]
    private void ClearTracks()
    {
        Tracks.Clear();
        OnTracksChanged();
    }

    private void OnTracksChanged()
    {
        _trackLog = Tracks.Count > 0 ? TrackLog.FromFiles(Tracks.Select(t => t.File)) : null;
        MapTracks = Tracks.SelectMany(t => t.MapTracks).ToList();
        OnPropertyChanged(nameof(HasTracks));
        OnPropertyChanged(nameof(ShowAutoTagControls));
        OnPropertyChanged(nameof(FilmHint));
        AutoTagAllCommand.NotifyCanExecuteChanged();
        AutoTagSelectedCommand.NotifyCanExecuteChanged();
        UpdateMatchSummary();
    }

    private TrackMatch? Match(PhotoItemViewModel photo)
    {
        if (_trackLog is null) return null;
        var utc = Clock.ToUtc(photo.DateTaken, photo.TimeZoneOffset);
        return utc is { } t ? _trackLog.Match(t, TimeSpan.FromMinutes(Math.Max(0.1, ToleranceMinutes))) : null;
    }

    private void UpdateMatchSummary()
    {
        if (_trackLog is null || _trackLog.Count == 0)
        {
            MatchSummary = "";
            TrackTimeHint = "";
            return;
        }

        var clock = Clock;
        var tz = clock.ResolveTimeZone();
        // Show the track's time span on the camera's clock so it can be compared with photo times.
        DateTime ToCamera(DateTimeOffset utc)
        {
            var shifted = utc - clock.Correction;
            return TimeZoneInfo.ConvertTime(shifted, tz).DateTime;
        }
        var start = ToCamera(_trackLog.Start!.Value);
        var end = ToCamera(_trackLog.End!.Value);
        string track = start.Date == end.Date
            ? $"{start.ToString("d MMM", CultureInfo.CurrentCulture)} {start:HH:mm}–{end:HH:mm}"
            : $"{start.ToString("d MMM HH:mm", CultureInfo.CurrentCulture)} – {end.ToString("d MMM HH:mm", CultureInfo.CurrentCulture)}";

        var dates = Photos.Where(p => p.DateTaken.HasValue).Select(p => p.DateTaken!.Value).OrderBy(d => d).ToList();
        string photos = dates.Count == 0 ? "" :
            dates[0].Date == dates[^1].Date
                ? $"{dates[0].ToString("d MMM", CultureInfo.CurrentCulture)} {dates[0]:HH:mm}–{dates[^1]:HH:mm}"
                : $"{dates[0].ToString("d MMM HH:mm", CultureInfo.CurrentCulture)} – {dates[^1].ToString("d MMM HH:mm", CultureInfo.CurrentCulture)}";
        TrackTimeHint = $"Track (camera time): {track}" + (photos.Length > 0 ? $"\nPhotos (camera time): {photos}" : "");

        if (Photos.Count == 0)
        {
            MatchSummary = "Open a folder to match photos against this track.";
            return;
        }
        int matched = Photos.Count(p => Match(p) is not null);
        MatchSummary = matched == Photos.Count
            ? $"All {matched} photos fall within the track log."
            : $"{matched} of {Photos.Count} photos fall within the track log. Photos outside it can be placed by hand, or check the time zone and clock correction.";
    }

    partial void OnSelectedTimeZoneChanged(TimeZoneOption value)
    {
        Settings.CameraTimeZoneId = value.Id;
        UpdateMatchSummary();
    }

    partial void OnPreferEmbeddedOffsetChanged(bool value)
    {
        Settings.PreferEmbeddedOffset = value;
        UpdateMatchSummary();
    }

    partial void OnToleranceMinutesChanged(double value)
    {
        if (double.IsNaN(value) || value <= 0) return;
        Settings.ToleranceMinutes = value;
        UpdateMatchSummary();
    }

    partial void OnReplaceExistingLocationsChanged(bool value) => Settings.ReplaceExistingLocations = value;

    partial void OnClockCorrectionTextChanged(string value)
    {
        ClockCorrectionError = CameraClock.TryParseCorrection(value, out _) ? null : "Use ±hh:mm:ss, e.g. +00:01:30 if the camera was 90 s slow";
        UpdateMatchSummary();
    }

    private bool CanAutoTagSelected() => HasTracks && HasSelection;
    private bool CanAutoTagAll() => HasTracks && HasPhotos;

    [RelayCommand(CanExecute = nameof(CanAutoTagSelected))]
    private void AutoTagSelected() => AutoTag(SelectedPhotos, "Auto-tag selected photos");

    [RelayCommand(CanExecute = nameof(CanAutoTagAll))]
    private void AutoTagAll() => AutoTag(Photos.ToList(), "Auto-tag all photos");

    private void AutoTag(IReadOnlyList<PhotoItemViewModel> targets, string description)
    {
        if (_trackLog is null) return;
        var changes = new List<(PhotoItemViewModel, PendingState)>();
        int keptExisting = 0, noMatch = 0, noDate = 0;
        foreach (var p in targets)
        {
            bool fromTrack = p.IsPending && p.Pending.Source == ChangeSource.TrackLog;
            if (!ReplaceExistingLocations && p.HasLocation && !fromTrack) { keptExisting++; continue; }
            if (p.DateTaken is null) { noDate++; continue; }
            var m = Match(p);
            if (m is null) { noMatch++; continue; }
            changes.Add((p, PendingState.Place(m.Value.Location, ChangeSource.TrackLog, m.Value.PhotoTimeUtc)));
        }

        var changed = Undo.Apply(description, changes);
        OnPhotosChanged();

        var notes = new List<string>();
        if (noMatch > 0) notes.Add($"{noMatch} outside the track log");
        if (keptExisting > 0) notes.Add($"{keptExisting} already had a location (kept)");
        if (noDate > 0) notes.Add($"{noDate} without a capture time");
        string detail = notes.Count > 0 ? "Skipped: " + string.Join(", ", notes) + "." : "";

        if (changes.Count == 0)
        {
            ShowInfo(FAInfoBarSeverity.Warning, "No photos were tagged", detail.Length > 0 ? detail + " Check the camera time zone and clock correction." : "");
        }
        else
        {
            ShowInfo(FAInfoBarSeverity.Success, $"Tagged {changes.Count} photo{(changes.Count == 1 ? "" : "s")} from the track log",
                (detail + " Review on the map, then Save to write the locations.").Trim());
            FitRequested?.Invoke(changes.Select(c => c.Item2.Location!.Value).ToList(), 17);
        }
        _ = changed;
    }

    // ------------------------------------------------------------------ Home location

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHomeLocation))]
    [NotifyCanExecuteChangedFor(nameof(PlaceSelectionAtHomeCommand))]
    private GeoPoint? _homeLocation;

    public bool HasHomeLocation => HomeLocation is not null;

    private bool CanPlaceAtHome() => HasSelection && HomeLocation is not null;

    [RelayCommand(CanExecute = nameof(CanPlaceAtHome))]
    private void PlaceSelectionAtHome()
    {
        if (HomeLocation is not { } home) return;
        PlaceAt(SelectedPhotos, home, SelectedPhotos.Count == 1 ? $"Placed {SelectedPhotos[0].FileName} at home" : $"Placed {SelectedPhotos.Count} photos at home");
    }

    public void SetHomeLocation(GeoPoint? home)
    {
        HomeLocation = home is { } h ? new GeoPoint(Math.Round(h.Latitude, 7), Math.Round(h.Longitude, 7)) : null;
        Settings.HomeLatitude = HomeLocation?.Latitude;
        Settings.HomeLongitude = HomeLocation?.Longitude;
        SaveSettings();
        StatusText = home is null ? "Home location cleared." : "Home location set. Select photos and press Home (Ctrl+H), or drop them on the house pin.";
    }

    // ------------------------------------------------------------------ Film mode

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDigitalMode), nameof(ShowAutoTagControls), nameof(TimeZoneLabel), nameof(FilmHint))]
    private bool _isFilmMode;

    public bool IsDigitalMode => !IsFilmMode;

    public bool ShowAutoTagControls => HasTracks && !IsFilmMode;

    public string TimeZoneLabel => IsFilmMode ? "Time zone for the photos' dates" : "Camera clock time zone";

    public string FilmHint => HasTracks
        ? "Film mode: drag photos onto the track to set where and when each was taken"
        : "Film mode: add the GPX track you recorded while shooting, then drag photos onto it";

    partial void OnIsFilmModeChanged(bool value)
    {
        Settings.FilmMode = value;
        StatusText = value
            ? "Film mode: dropping photos on the track sets their location, date taken and file dates."
            : "Digital mode: photos keep their own capture times.";
    }

    /// <summary>Formats a track time in the photos' time zone (with the day if the tracks span several days).</summary>
    public string FormatTrackTime(DateTimeOffset time)
    {
        var zone = Clock.ResolveTimeZone();
        var local = TimeZoneInfo.ConvertTime(time, zone);
        bool multiDay = _trackLog?.Start is { } s && _trackLog.End is { } e &&
                        TimeZoneInfo.ConvertTime(s, zone).Date != TimeZoneInfo.ConvertTime(e, zone).Date;
        return local.ToString(multiDay ? "ddd d MMM, HH:mm:ss" : "HH:mm:ss", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Film mode: gives photos the position and time of a point on the track. Several photos dropped
    /// together get consecutive seconds so they stay in filmstrip order when sorted by date.
    /// </summary>
    public void FilmPlace(IReadOnlyList<PhotoItemViewModel> photos, TrackSnap snap)
    {
        if (photos.Count == 0) return;
        var ordered = photos.OrderBy(p => Photos.IndexOf(p)).ToList();
        var zone = Clock.ResolveTimeZone();
        long ticks = snap.Time.UtcTicks;
        long rounded = (ticks + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond;
        var start = new DateTimeOffset(rounded, TimeSpan.Zero);

        var changes = ordered.Select((p, i) =>
        {
            var utc = start.AddSeconds(i);
            return (p, PendingState.Film(snap.Location, utc, TimeZoneInfo.ConvertTime(utc, zone)));
        }).ToList();

        string when = FormatTrackTime(start);
        string description = ordered.Count == 1 ? $"Set {ordered[0].FileName} to {when}" : $"Set {ordered.Count} photos to {when}";
        Undo.Apply(description, changes);
        OnPhotosChanged();
        StatusText = $"{description} at {CoordinateFormatter.ToDecimal(snap.Location)}" +
                     (ordered.Count > 1 ? " (one second apart, in filmstrip order)" : "") + ". Save to write to the files.";
        if (!ordered.All(p => SelectedPhotos.Contains(p))) RequestSelection(ordered);
    }

    // ------------------------------------------------------------------ Editing

    public void PlaceAt(IReadOnlyList<PhotoItemViewModel> photos, GeoPoint location, string description)
    {
        if (photos.Count == 0) return;
        var point = location.WithoutAltitude();
        Undo.Apply(description, photos.Select(p => (p, PendingState.Place(point))));
        OnPhotosChanged();
        StatusText = $"{description}: {CoordinateFormatter.ToDecimal(point)}. Save to write to {(photos.Count == 1 ? "the file" : "the files")}.";
    }

    public void DropOnMap(IReadOnlyList<PhotoItemViewModel> photos, GeoPoint location)
    {
        PlaceAt(photos, location, photos.Count == 1 ? $"Placed {photos[0].FileName}" : $"Placed {photos.Count} photos");
        if (!photos.All(p => SelectedPhotos.Contains(p))) RequestSelection(photos);
    }

    public void MoveMarkers(IReadOnlyList<MapMarker> markers, GeoPoint to)
    {
        var photos = markers.SelectMany(m => m.Photos).OfType<PhotoItemViewModel>().ToList();
        PlaceAt(photos, to, photos.Count == 1 ? $"Moved {photos[0].FileName}" : $"Moved {photos.Count} photos");
    }

    public void PlaceSelectionAt(GeoPoint location) =>
        PlaceAt(SelectedPhotos, location, SelectedPhotos.Count == 1 ? $"Placed {SelectedPhotos[0].FileName}" : $"Placed {SelectedPhotos.Count} photos");

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveLocation()
    {
        var targets = SelectedPhotos.Where(p => p.HasLocation).ToList();
        if (targets.Count == 0) return;
        Undo.Apply(targets.Count == 1 ? "Remove location" : $"Remove {targets.Count} locations", targets.Select(p => (p, PendingState.Remove)));
        OnPhotosChanged();
        StatusText = $"Location will be removed from {targets.Count} photo{(targets.Count == 1 ? "" : "s")} when you save.";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RevertSelection()
    {
        Undo.Apply("Revert", SelectedPhotos.Select(p => (p, PendingState.None)));
        OnPhotosChanged();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ApplyCoordinates()
    {
        if (!CoordinateParser.TryParse(CoordinateInput, out var p))
        {
            CoordinateError = "Couldn't read those coordinates. Try \"51.5007, -0.1246\" or 51°30'02\"N 0°07'28\"W.";
            return;
        }
        CoordinateError = null;
        PlaceSelectionAt(p);
        CoordinateInput = "";
        CenterRequested?.Invoke(p, null);
    }

    partial void OnCoordinateInputChanged(string value) => CoordinateError = null;

    private bool CanCopyCoordinates() => SelectedPhotos.Any(p => p.HasLocation);

    [RelayCommand(CanExecute = nameof(CanCopyCoordinates))]
    private async Task CopyCoordinatesAsync()
    {
        var p = SelectedPhotos.FirstOrDefault(x => x.HasLocation)?.EffectiveLocation;
        if (p is null) return;
        await _ui.CopyTextAsync(CoordinateFormatter.ToDecimal(p.Value));
        StatusText = $"Copied {CoordinateFormatter.ToDecimal(p.Value)}";
    }

    /// <summary>Pastes coordinates from the clipboard onto the selection. Returns false if the clipboard had none.</summary>
    public async Task<bool> PasteCoordinatesAsync()
    {
        if (!HasSelection) return false;
        var text = await _ui.GetClipboardTextAsync();
        if (!CoordinateParser.TryParse(text, out var p)) return false;
        PlaceSelectionAt(p);
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void UndoEdit()
    {
        var changed = Undo.Undo();
        OnPhotosChanged();
        if (changed.Count > 0) StatusText = "Undone.";
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void RedoEdit()
    {
        Undo.Redo();
        OnPhotosChanged();
    }

    private bool CanUndo() => Undo.CanUndo;
    private bool CanRedo() => Undo.CanRedo;

    private void OnUndoChanged()
    {
        UndoEditCommand.NotifyCanExecuteChanged();
        RedoEditCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasPendingChanges))]
    private async Task DiscardAsync()
    {
        if (!await _ui.ConfirmAsync("Discard changes?", $"Discard {PendingCount} unsaved location change{(PendingCount == 1 ? "" : "s")}? You can undo this until you save.", "Discard"))
            return;
        Undo.Apply("Discard changes", Photos.Where(p => p.IsPending).Select(p => (p, PendingState.None)));
        OnPhotosChanged();
        StatusText = "Changes discarded.";
    }

    // ------------------------------------------------------------------ Saving

    [RelayCommand(CanExecute = nameof(HasPendingChanges))]
    private async Task SaveAsync()
    {
        if (!await EnsureExifToolAsync()) return;
        var exifTool = _exifTool!;
        var items = Photos.Where(p => p.IsPending).ToList();
        if (items.Count == 0) return;

        var options = Settings.WriteOptions;
        IsBusy = true;
        IsBusyIndeterminate = false;
        BusyProgress = 0;
        BusyText = $"Saving {items.Count} photo{(items.Count == 1 ? "" : "s")}…";

        var results = new ConcurrentBag<(PhotoItemViewModel Photo, WriteResult Result, PendingState Intended)>();
        try
        {
            int done = 0;
            using var gate = new SemaphoreSlim(exifTool.PoolSize);
            await Task.WhenAll(items.Select(async p =>
            {
                var intended = p.Pending;
                await gate.WaitAsync();
                try
                {
                    var result = await GeoWriter.WriteAsync(exifTool, p.Source,
                        new GeoTagChange(intended.Location, intended.GpsTimeUtc, intended.CaptureTime), options);
                    results.Add((p, result, intended));
                }
                catch (Exception ex)
                {
                    results.Add((p, new WriteResult(p.Source, false, [ex.Message], [], []), intended));
                }
                finally
                {
                    gate.Release();
                    int d = Interlocked.Increment(ref done);
                    Dispatcher.UIThread.Post(() => BusyProgress = 100.0 * d / items.Count);
                }
            }));

            // Read every saved file back so what we show is what's really on disk.
            BusyText = "Verifying…";
            IsBusyIndeterminate = true;
            var ok = results.Where(r => r.Result.Success).ToList();
            var sources = ok.Select(r => r.Photo.Source).ToList();
            var meta = await MetadataReader.ReadMediaAsync(exifTool, sources.Select(s => s.PrimaryPath).ToList());
            var sidecarPaths = sources.SelectMany(s => s.SidecarPaths).ToList();
            var sidecars = sidecarPaths.Count > 0 ? await MetadataReader.ReadSidecarsAsync(exifTool, sidecarPaths) : new Dictionary<string, SidecarMetadata>();

            var problems = results.Where(r => !r.Result.Success).SelectMany(r => r.Result.Errors).ToList();
            int savedPhotos = 0;
            int savedFiles = 0;
            foreach (var (photo, result, intended) in ok)
            {
                var actual = ResolveLocation(photo.Source, meta, sidecars);
                var (actualDate, actualOffset) = ResolveDate(photo.Source, meta, sidecars);
                bool locationOk = intended.Location is { } want
                    ? actual is { } got && Math.Abs(got.Latitude - want.Latitude) < 2e-5 && Math.Abs(got.Longitude - want.Longitude) < 2e-5
                    : actual is null;
                bool dateOk = intended.CaptureTime is not { } wantDate ||
                              actualDate is { } gotDate && Math.Abs((gotDate - wantDate.DateTime).TotalSeconds) < 1;
                bool matches = locationOk && dateOk;
                photo.OriginalLocation = actual;
                photo.OriginalDateTaken = actualDate;
                photo.OriginalTimeZoneOffset = actualOffset;
                if (!dateOk)
                {
                    problems.Add($"{photo.FileName}: the date taken reads back as {actualDate?.ToString("g", CultureInfo.CurrentCulture) ?? "nothing"} after saving.");
                }
                else if (matches)
                {
                    // Only clear the pending edit if it wasn't changed while saving.
                    if (photo.Pending == intended) photo.Pending = PendingState.None;
                    savedPhotos++;
                    savedFiles += result.FilesWritten.Count;
                }
                else
                {
                    problems.Add($"{photo.FileName}: the file reads back as {(actual is { } a ? CoordinateFormatter.ToDecimal(a) : "no location")} after saving.");
                }
            }

            Undo.Clear();
            OnPhotosChanged();
            ApplyFilter();

            if (problems.Count > 0)
            {
                ShowInfo(FAInfoBarSeverity.Error, $"{items.Count - savedPhotos} photo{(items.Count - savedPhotos == 1 ? "" : "s")} couldn't be saved",
                    string.Join("\n", problems.Take(6)) + (problems.Count > 6 ? $"\n…and {problems.Count - 6} more" : ""));
            }
            else
            {
                string where = options.Target switch
                {
                    WriteTarget.Sidecar => " (XMP sidecars)",
                    WriteTarget.Both => " (photos and XMP sidecars)",
                    _ => "",
                };
                ShowInfo(FAInfoBarSeverity.Success, $"Saved {savedPhotos} photo{(savedPhotos == 1 ? "" : "s")}",
                    $"{savedFiles} file{(savedFiles == 1 ? "" : "s")} updated{where}{(options.KeepBackups ? "; originals kept as _original backups" : "")}.");
            }
            StatusText = $"Saved {savedPhotos} photo{(savedPhotos == 1 ? "" : "s")}.";
        }
        catch (Exception ex)
        {
            ShowInfo(FAInfoBarSeverity.Error, "Saving failed", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Asks to save/discard pending edits. Returns false if the user cancelled.</summary>
    public async Task<bool> ConfirmDiscardPendingAsync()
    {
        if (PendingCount == 0) return true;
        switch (await _ui.AskUnsavedChangesAsync(PendingCount))
        {
            case UnsavedChoice.Save:
                await SaveAsync();
                return PendingCount == 0;
            case UnsavedChoice.Discard:
                foreach (var p in Photos) p.Pending = PendingState.None;
                Undo.Clear();
                OnPhotosChanged();
                return true;
            default:
                return false;
        }
    }

    // ------------------------------------------------------------------ Selection

    /// <summary>Called by the view when the filmstrip selection changes.</summary>
    public void SetSelectionFromView(IReadOnlyList<PhotoItemViewModel> selection)
    {
        foreach (var p in SelectedPhotos) p.IsSelected = false;
        foreach (var p in selection) p.IsSelected = true;
        SelectedPhotos = selection;
        CoordinateError = null;
        RebuildMarkers();
        RefreshSelectionDetails();
        LoadPreview();
    }

    private void RequestSelection(IReadOnlyList<PhotoItemViewModel> photos)
    {
        if (photos.Any(p => !VisiblePhotos.Contains(p)))
        {
            Filter = Filters[0];
        }
        SelectionSyncRequested?.Invoke(photos);
    }

    public void SelectFromMap(IReadOnlyList<MapMarker> markers, KeyModifiers modifiers)
    {
        var photos = markers.SelectMany(m => m.Photos).OfType<PhotoItemViewModel>().ToList();
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        List<PhotoItemViewModel> result;
        if (modifiers.HasFlag(command))
        {
            bool allSelected = photos.All(p => p.IsSelected);
            result = allSelected
                ? SelectedPhotos.Except(photos).ToList()
                : SelectedPhotos.Union(photos).ToList();
        }
        else if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            result = SelectedPhotos.Union(photos).ToList();
        }
        else
        {
            result = photos;
        }
        RequestSelection(result);
    }

    [RelayCommand]
    private void SelectAll() => RequestSelection(VisiblePhotos.ToList());

    [RelayCommand]
    private void SelectNone() => RequestSelection([]);

    [RelayCommand]
    private void SelectWithoutLocation() => RequestSelection(Photos.Where(p => !p.HasLocation).ToList());

    private void LoadPreview()
    {
        _previewCts?.Cancel();
        var photo = SelectedPhoto;
        if (photo is null)
        {
            SelectionPreview = null;
            return;
        }
        SelectionPreview = photo.Thumbnail;
        var cts = _previewCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                var bmp = await Thumbnails.GetPreviewAsync(new ThumbnailRequest(photo.Source, photo.Orientation), cts.Token);
                if (bmp is not null)
                    Dispatcher.UIThread.Post(() => { if (!cts.IsCancellationRequested && ReferenceEquals(SelectedPhoto, photo)) SelectionPreview = bmp; });
            }
            catch (OperationCanceledException) { }
            catch { /* keep the thumbnail */ }
        });
    }

    // ------------------------------------------------------------------ Map

    [RelayCommand(CanExecute = nameof(CanCopyCoordinates))]
    private void ShowSelectionOnMap()
    {
        var points = SelectedPhotos.Where(p => p.HasLocation).Select(p => p.EffectiveLocation!.Value).ToList();
        if (points.Count > 0) FitRequested?.Invoke(points, 17);
    }

    [RelayCommand]
    private void ZoomToPhotos() => FitToEverything();

    public void FitToEverything(bool animate = true)
    {
        var points = Photos.Where(p => p.HasLocation).Select(p => p.EffectiveLocation!.Value).ToList();
        foreach (var t in Tracks)
        {
            var pts = t.File.AllTrackPoints.ToList();
            if (pts.Count == 0) continue;
            points.Add(new GeoPoint(pts.Min(p => p.Latitude), pts.Min(p => p.Longitude)));
            points.Add(new GeoPoint(pts.Max(p => p.Latitude), pts.Max(p => p.Longitude)));
        }
        if (points.Count > 0) FitRequested?.Invoke(points, 17);
    }

    [RelayCommand]
    private void SetMapStyle(MapStyle style) => MapStyle = style;

    partial void OnMapStyleChanged(MapStyle value) => Settings.MapStyle = value.Id;

    [RelayCommand]
    private async Task SearchAsync()
    {
        string q = SearchText.Trim();
        if (q.Length == 0) return;

        if (CoordinateParser.TryParse(q, out var p))
        {
            SearchPin = p;
            IsSearchResultsOpen = false;
            CenterRequested?.Invoke(p, 16);
            return;
        }

        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        IsSearching = true;
        try
        {
            var results = await Geocoder.SearchAsync(q, cts.Token);
            if (cts.IsCancellationRequested) return;
            SearchResults.Clear();
            foreach (var r in results) SearchResults.Add(r);
            if (results.Count == 0)
            {
                ShowInfo(FAInfoBarSeverity.Informational, "No places found", $"Nothing matched \"{q}\".");
                IsSearchResultsOpen = false;
            }
            else
            {
                IsSearchResultsOpen = true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowInfo(FAInfoBarSeverity.Warning, "Search failed", ex.Message);
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private void GoToSearchResult(GeocodeResult result)
    {
        SearchPin = result.Location;
        IsSearchResultsOpen = false;
        if (result.SouthWest is { } sw && result.NorthEast is { } ne)
            FitRequested?.Invoke([sw, ne], 17);
        else
            CenterRequested?.Invoke(result.Location, 16);
    }

    private bool CanPlaceAtSearchPin() => HasSelection && SearchPin is not null;

    [RelayCommand(CanExecute = nameof(CanPlaceAtSearchPin))]
    private void PlaceSelectionAtSearchPin()
    {
        if (SearchPin is { } p) PlaceSelectionAt(p);
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchPin = null;
        SearchText = "";
        SearchResults.Clear();
        IsSearchResultsOpen = false;
    }

    private void RebuildMarkers()
    {
        MapMarkers = Photos
            .Where(p => p.EffectiveLocation is not null)
            .GroupBy(p => (Math.Round(p.EffectiveLocation!.Value.Latitude, 6), Math.Round(p.EffectiveLocation!.Value.Longitude, 6)))
            .Select(g => new MapMarker(g.First().EffectiveLocation!.Value, g.ToList<IMapPhoto>(), g.Any(p => p.IsSelected), g.Any(p => p.IsPending)))
            .ToList();
    }

    // ------------------------------------------------------------------ Filtering & refresh

    partial void OnFilterChanged(FilterOption value) => ApplyFilter();

    private void ApplyFilter()
    {
        var selection = SelectedPhotos;
        IEnumerable<PhotoItemViewModel> items = Filter.Value switch
        {
            PhotoFilter.WithLocation => Photos.Where(p => p.HasLocation),
            PhotoFilter.WithoutLocation => Photos.Where(p => !p.HasLocation),
            PhotoFilter.Unsaved => Photos.Where(p => p.IsPending),
            _ => Photos,
        };
        VisiblePhotos.Clear();
        foreach (var p in items) VisiblePhotos.Add(p);
        SelectionSyncRequested?.Invoke(selection.Where(VisiblePhotos.Contains).ToList());
    }

    private void OnPhotosChanged()
    {
        PendingCount = Photos.Count(p => p.IsPending);
        TaggedCount = Photos.Count(p => p.HasLocation);
        RebuildMarkers();
        RefreshSelectionDetails();
        CopyCoordinatesCommand.NotifyCanExecuteChanged();
        ShowSelectionOnMapCommand.NotifyCanExecuteChanged();
        AutoTagAllCommand.NotifyCanExecuteChanged();
    }

    private void RefreshSelectionDetails()
    {
        foreach (var name in new[]
                 {
                     nameof(SelectionTitle), nameof(SelectionDate), nameof(SelectionCamera), nameof(SelectionFiles),
                     nameof(SelectionLocation), nameof(SelectionAltitude), nameof(HasSelectionAltitude), nameof(SelectionStatus),
                     nameof(SelectionHasPending), nameof(PhotoCountText),
                 })
            OnPropertyChanged(name);
        CopyCoordinatesCommand.NotifyCanExecuteChanged();
        ShowSelectionOnMapCommand.NotifyCanExecuteChanged();
    }

    // ------------------------------------------------------------------ Info bar & settings

    public void ShowInfo(FAInfoBarSeverity severity, string title, string message, string? actionText = null, Func<Task>? action = null)
    {
        InfoSeverity = severity;
        InfoTitle = title;
        InfoMessage = message;
        InfoActionText = action is null ? null : actionText;
        _infoAction = action;
        IsInfoOpen = false;
        IsInfoOpen = true;
    }

    [RelayCommand]
    private void CloseInfo() => IsInfoOpen = false;

    [RelayCommand]
    private async Task InfoActionAsync()
    {
        var action = _infoAction;
        IsInfoOpen = false;
        _infoAction = null;
        if (action is not null) await action();
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var vm = new SettingsViewModel(Settings, ExifToolStatus + (_exifTool is not null ? $" — {_exifTool.Location.Display}" : ""), _ui,
            () => $"Thumbnails {Thumbnails.CacheSizeBytes() / 1048576.0:0.#} MB · map tiles {TileCache.DiskUsageBytes(Path.Combine(AppPaths.CacheDirectory, "tiles")) / 1048576.0:0.#} MB",
            () => { Thumbnails.ClearCache(); TileCache.ClearDisk(); });

        bool oldPair = Settings.PairRawWithJpeg, oldSub = Settings.IncludeSubfolders;
        string? oldExif = Settings.ExifToolPath;
        if (!await _ui.ShowSettingsAsync(vm)) return;

        vm.ApplyTo(Settings);
        SaveSettings();
        App.ApplyTheme(Settings.Theme);
        HomeLocation = Settings.HomeLatitude is { } hl && Settings.HomeLongitude is { } hn ? new GeoPoint(hl, hn) : null;

        if (!string.Equals(oldExif, Settings.ExifToolPath, StringComparison.Ordinal)) await RestartExifToolAsync();
        if ((oldPair != Settings.PairRawWithJpeg || oldSub != Settings.IncludeSubfolders) && CurrentFolder is not null)
            await LoadFolderAsync(CurrentFolder);
    }

    public void SaveSettings() => SettingsStore.Save(Settings);

    public async ValueTask DisposeAsync()
    {
        _thumbnailCts?.Cancel();
        _previewCts?.Cancel();
        TileCache.Dispose();
        if (_exifTool is not null) await _exifTool.DisposeAsync();
    }
}
