using System.Globalization;
using System.Text;
using System.Xml;

namespace Geotagger.Core.Gpx;

/// <summary>
/// Streaming GPX 1.0/1.1 reader. Namespaces are ignored so files from most loggers
/// (phones, Garmin, Strava exports, etc.) are accepted, including ones with extensions.
/// </summary>
public static class GpxParser
{
    public static GpxFile Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
        return Parse(stream, path);
    }

    public static GpxFile Parse(Stream stream, string? filePath = null)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };

        var file = new GpxFile { FilePath = filePath };
        var stack = new List<string>();
        var text = new StringBuilder();
        GpxTrack? track = null;
        GpxSegment? segment = null;
        PointBuilder? point = null;

        using var reader = XmlReader.Create(stream, settings);
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                {
                    string name = reader.LocalName;
                    stack.Add(name);
                    text.Clear();
                    OnStart(name, reader);
                    if (reader.IsEmptyElement) OnEnd();
                    break;
                }
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                    text.Append(reader.Value);
                    break;
                case XmlNodeType.EndElement:
                    OnEnd();
                    break;
            }
        }

        foreach (var t in file.Tracks) t.Segments.RemoveAll(s => s.Points.Count == 0);
        file.Tracks.RemoveAll(t => t.Segments.Count == 0);
        return file;

        void OnStart(string name, XmlReader r)
        {
            switch (name)
            {
                case "gpx":
                    file.Creator = r.GetAttribute("creator");
                    break;
                case "trk":
                    track = new GpxTrack();
                    file.Tracks.Add(track);
                    segment = null;
                    break;
                case "rte":
                    track = new GpxTrack();
                    file.Tracks.Add(track);
                    segment = new GpxSegment();
                    track.Segments.Add(segment);
                    break;
                case "trkseg":
                    if (track is null) { track = new GpxTrack(); file.Tracks.Add(track); }
                    segment = new GpxSegment();
                    track.Segments.Add(segment);
                    break;
                case "trkpt" or "rtept" or "wpt":
                    point = PointBuilder.FromAttributes(r);
                    break;
            }
        }

        void OnEnd()
        {
            if (stack.Count == 0) return;
            string name = stack[^1];
            string parent = stack.Count > 1 ? stack[^2] : "";
            stack.RemoveAt(stack.Count - 1);

            switch (name)
            {
                case "ele" when point is not null && parent is "trkpt" or "rtept" or "wpt":
                    point.Elevation = ReadDouble(text.ToString());
                    break;
                case "time" when point is not null && parent is "trkpt" or "rtept" or "wpt":
                    point.Time = ReadTime(text.ToString());
                    break;
                case "name" when parent is "trk" or "rte" && track is not null:
                    track.Name ??= text.ToString().Trim();
                    break;
                case "name" when parent is "metadata" or "gpx":
                    file.Name ??= text.ToString().Trim();
                    break;
                case "trkpt" or "rtept":
                    if (point?.Build() is { } tp)
                    {
                        if (track is null) { track = new GpxTrack(); file.Tracks.Add(track); }
                        if (segment is null) { segment = new GpxSegment(); track.Segments.Add(segment); }
                        segment.Points.Add(tp);
                    }
                    point = null;
                    break;
                case "wpt":
                    if (point?.Build() is { } wp) file.Waypoints.Add(wp);
                    point = null;
                    break;
                case "trkseg":
                    segment = null;
                    break;
                case "trk" or "rte":
                    track = null;
                    segment = null;
                    break;
            }
            text.Clear();
        }
    }

    private static double? ReadDouble(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;

    internal static DateTimeOffset? ReadTime(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return null;
        // GPX mandates UTC ("Z"), but some writers omit the designator or use offsets.
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t
            : null;
    }

    private sealed class PointBuilder
    {
        private double _lat;
        private double _lon;
        private bool _valid;
        public double? Elevation;
        public DateTimeOffset? Time;

        public static PointBuilder FromAttributes(XmlReader r)
        {
            var b = new PointBuilder();
            b._valid = double.TryParse(r.GetAttribute("lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out b._lat) &
                       double.TryParse(r.GetAttribute("lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out b._lon);
            b._valid &= b._lat is >= -90 and <= 90 && b._lon is >= -180 and <= 180;
            return b;
        }

        public GpxTrackPoint? Build() => _valid ? new GpxTrackPoint(_lat, _lon, Elevation, Time) : null;
    }
}
