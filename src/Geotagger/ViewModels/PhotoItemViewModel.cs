using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Geotagger.Core.Geo;
using Geotagger.Core.Photos;
using Geotagger.Map;

namespace Geotagger.ViewModels;

public enum ChangeSource { Manual, TrackLog, Film }

/// <summary>
/// An unsaved change. <see cref="Location"/> null with <see cref="HasChange"/> means "remove".
/// <see cref="CaptureTime"/> (film mode) also sets the date taken; its offset is the photo's time zone.
/// </summary>
public readonly record struct PendingState(bool HasChange, GeoPoint? Location, ChangeSource Source, DateTimeOffset? GpsTimeUtc,
    DateTimeOffset? CaptureTime = null)
{
    public static PendingState Film(GeoPoint p, DateTimeOffset utc, DateTimeOffset local) =>
        new(true, p, ChangeSource.Film, utc, local);

    public static PendingState None => default;
    public static PendingState Place(GeoPoint p, ChangeSource source = ChangeSource.Manual, DateTimeOffset? gpsTime = null) =>
        new(true, p, source, gpsTime);
    public static PendingState Remove => new(true, null, ChangeSource.Manual, null);
}

public enum LocationStatus { None, Saved, Pending, PendingRemoval }

public sealed partial class PhotoItemViewModel : ObservableObject, IMapPhoto
{
    public PhotoItemViewModel(PhotoSource source, PhotoMetadata? metadata, GeoPoint? originalLocation,
        DateTime? dateTaken = null, TimeSpan? timeZoneOffset = null)
    {
        Source = source;
        _originalDateTaken = dateTaken ?? metadata?.DateTaken;
        _originalTimeZoneOffset = dateTaken is not null ? timeZoneOffset : metadata?.TimeZoneOffset;
        Orientation = metadata?.Orientation ?? 1;
        Camera = metadata?.Camera;
        Lens = metadata?.Lens;
        _originalLocation = originalLocation;
    }

    public PhotoSource Source { get; }

    public string FileName => Source.FileName;

    public string FormatLabel => Path.GetExtension(Source.PrimaryPath).TrimStart('.').ToUpperInvariant();

    public string? CompanionLabel => Source.CompanionPaths.Count == 0
        ? null
        : "+" + string.Join("+", Source.CompanionPaths.Select(p => Path.GetExtension(p).TrimStart('.').ToUpperInvariant()));

    public bool HasCompanions => Source.CompanionPaths.Count > 0;

    /// <summary>The date taken as it will be after saving (pending film-mode time, or what's in the file).</summary>
    public DateTime? DateTaken => Pending.HasChange && Pending.CaptureTime is { } t ? t.DateTime : OriginalDateTaken;

    public TimeSpan? TimeZoneOffset => Pending.HasChange && Pending.CaptureTime is { } t ? t.Offset : OriginalTimeZoneOffset;

    public bool HasNewDate => Pending.HasChange && Pending.CaptureTime is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DateTaken), nameof(DateText), nameof(TimeText), nameof(DateTimeText))]
    private DateTime? _originalDateTaken;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeZoneOffset))]
    private TimeSpan? _originalTimeZoneOffset;

    public int Orientation { get; }

    public string? Camera { get; }

    public string? Lens { get; }

    public string DateText => DateTaken?.ToString("d MMM yyyy", CultureInfo.CurrentCulture) ?? "No date";

    public string TimeText => DateTaken?.ToString("HH:mm:ss", CultureInfo.CurrentCulture) ?? "";

    public string DateTimeText => DateTaken?.ToString("ddd d MMM yyyy, HH:mm:ss", CultureInfo.CurrentCulture) ?? "Unknown";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveLocation), nameof(Status), nameof(HasLocation), nameof(IsPending), nameof(LocationText),
        nameof(IsSaved), nameof(IsPendingRemoval), nameof(StatusText))]
    private GeoPoint? _originalLocation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveLocation), nameof(Status), nameof(HasLocation), nameof(IsPending), nameof(LocationText),
        nameof(IsSaved), nameof(IsPendingRemoval), nameof(StatusText), nameof(DateTaken), nameof(DateText), nameof(TimeText),
        nameof(DateTimeText), nameof(TimeZoneOffset), nameof(HasNewDate))]
    private PendingState _pending;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _isSelected;

    public GeoPoint? EffectiveLocation => Pending.HasChange ? Pending.Location : OriginalLocation;

    public bool HasLocation => EffectiveLocation is not null;

    public bool IsPending => Pending.HasChange;

    public bool IsSaved => Status == LocationStatus.Saved;

    public bool IsPendingRemoval => Status == LocationStatus.PendingRemoval;

    public LocationStatus Status =>
        Pending.HasChange ? (Pending.Location is null ? LocationStatus.PendingRemoval : LocationStatus.Pending)
        : OriginalLocation is not null ? LocationStatus.Saved
        : LocationStatus.None;

    public string StatusText => Status switch
    {
        LocationStatus.Saved => "Location saved in file",
        LocationStatus.Pending => Pending.Source switch
        {
            ChangeSource.TrackLog => "New location from track log (unsaved)",
            ChangeSource.Film => "New location and time from track (unsaved)",
            _ => "New location (unsaved)",
        },
        LocationStatus.PendingRemoval => "Location will be removed (unsaved)",
        _ => "No location",
    };

    public string LocationText => EffectiveLocation is { } p ? CoordinateFormatter.ToDecimal(p) : "No location";

    public override string ToString() => FileName;
}
