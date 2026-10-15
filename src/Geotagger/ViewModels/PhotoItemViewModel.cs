using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Geotagger.Core.Geo;
using Geotagger.Core.Photos;
using Geotagger.Map;

namespace Geotagger.ViewModels;

public enum ChangeSource { Manual, TrackLog }

/// <summary>An unsaved location change. <see cref="Location"/> null with <see cref="HasChange"/> means "remove".</summary>
public readonly record struct PendingState(bool HasChange, GeoPoint? Location, ChangeSource Source, DateTimeOffset? GpsTimeUtc)
{
    public static PendingState None => default;
    public static PendingState Place(GeoPoint p, ChangeSource source = ChangeSource.Manual, DateTimeOffset? gpsTime = null) =>
        new(true, p, source, gpsTime);
    public static PendingState Remove => new(true, null, ChangeSource.Manual, null);
}

public enum LocationStatus { None, Saved, Pending, PendingRemoval }

public sealed partial class PhotoItemViewModel : ObservableObject, IMapPhoto
{
    public PhotoItemViewModel(PhotoSource source, PhotoMetadata? metadata, GeoPoint? originalLocation)
    {
        Source = source;
        DateTaken = metadata?.DateTaken;
        TimeZoneOffset = metadata?.TimeZoneOffset;
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

    public DateTime? DateTaken { get; }

    public TimeSpan? TimeZoneOffset { get; }

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
        nameof(IsSaved), nameof(IsPendingRemoval), nameof(StatusText))]
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
        LocationStatus.Pending => Pending.Source == ChangeSource.TrackLog ? "New location from track log (unsaved)" : "New location (unsaved)",
        LocationStatus.PendingRemoval => "Location will be removed (unsaved)",
        _ => "No location",
    };

    public string LocationText => EffectiveLocation is { } p ? CoordinateFormatter.ToDecimal(p) : "No location";

    public override string ToString() => FileName;
}
