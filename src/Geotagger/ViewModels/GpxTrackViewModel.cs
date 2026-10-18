using System.Globalization;
using Avalonia.Media;
using Geotagger.Core.Gpx;
using Geotagger.Map;

namespace Geotagger.ViewModels;

public sealed class GpxTrackViewModel
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0xC2, 0x39, 0xB3), // magenta
        Color.FromRgb(0x00, 0x99, 0xBC), // teal
        Color.FromRgb(0x88, 0x17, 0x98), // purple
        Color.FromRgb(0x10, 0x89, 0x3E), // green
        Color.FromRgb(0xE7, 0x48, 0x56), // red
        Color.FromRgb(0x5C, 0x2D, 0x91), // indigo
    ];

    public GpxTrackViewModel(GpxFile file, int index)
    {
        File = file;
        Color = Palette[index % Palette.Length];
        Brush = new SolidColorBrush(Color);
        MapTracks = file.Tracks
            .SelectMany(t => t.Segments)
            .Where(s => s.Points.Count > 0)
            .Select(s => new MapTrack(s.Points, Color, file.DisplayName))
            .ToList();
    }

    public GpxFile File { get; }

    public Color Color { get; }

    public IBrush Brush { get; }

    public IReadOnlyList<MapTrack> MapTracks { get; }

    public string Name => File.DisplayName;

    public string Details
    {
        get
        {
            string points = $"{File.PointCount:N0} points";
            if (File.StartTime is not { } s || File.EndTime is not { } e) return points + " · no timestamps";
            var ls = s.ToLocalTime();
            var le = e.ToLocalTime();
            string range = ls.Date == le.Date
                ? $"{ls.ToString("d MMM", CultureInfo.CurrentCulture)} {ls:HH:mm}–{le:HH:mm}"
                : $"{ls.ToString("d MMM HH:mm", CultureInfo.CurrentCulture)} – {le.ToString("d MMM HH:mm", CultureInfo.CurrentCulture)}";
            return $"{points} · {range}";
        }
    }

    public string ToolTip => $"{File.FilePath}\n{Details} (shown in this computer's time zone)";
}
