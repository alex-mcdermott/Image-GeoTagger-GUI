namespace Geotagger.Core.Matching;

/// <summary>
/// How to turn a camera's local "DateTimeOriginal" (which has no time zone) into UTC so it can
/// be compared with GPS track times.
/// </summary>
public sealed record CameraClock
{
    /// <summary>System time zone ID the camera clock was set to. Null means this computer's zone.</summary>
    public string? TimeZoneId { get; init; }

    /// <summary>Use OffsetTimeOriginal/OffsetTime from the photo when the camera recorded one.</summary>
    public bool PreferEmbeddedOffset { get; init; } = true;

    /// <summary>
    /// Added to the camera's time to get the true time. Use a positive value if the camera clock
    /// was running behind, negative if it was ahead.
    /// </summary>
    public TimeSpan Correction { get; init; }

    public TimeZoneInfo ResolveTimeZone()
    {
        if (string.IsNullOrEmpty(TimeZoneId)) return TimeZoneInfo.Local;
        return TimeZoneInfo.TryFindSystemTimeZoneById(TimeZoneId, out var tz) ? tz : TimeZoneInfo.Local;
    }

    /// <summary>Converts a camera timestamp to UTC, or null if there is no timestamp.</summary>
    public DateTimeOffset? ToUtc(DateTime? cameraLocalTime, TimeSpan? embeddedOffset)
    {
        if (cameraLocalTime is not { } local) return null;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        TimeSpan offset = PreferEmbeddedOffset && embeddedOffset is { } o
            ? o
            : ResolveTimeZone().GetUtcOffset(local);

        return new DateTimeOffset(local, offset).ToUniversalTime() + Correction;
    }

    /// <summary>Parses "+01:30:00", "-5:00", "90" (seconds), "-1h", "2m30s" style corrections.</summary>
    public static bool TryParseCorrection(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return true;
        string s = text.Trim().Replace(" ", "");
        bool negative = s.StartsWith('-');
        if (s.StartsWith('+') || s.StartsWith('-')) s = s[1..];

        if (s.Contains(':'))
        {
            var parts = s.Split(':');
            if (parts.Length is < 2 or > 3 || parts.Any(p => !int.TryParse(p, out _))) return false;
            int h = int.Parse(parts[0]);
            int m = int.Parse(parts[1]);
            int sec = parts.Length == 3 ? int.Parse(parts[2]) : 0;
            if (m >= 60 || sec >= 60) return false;
            value = new TimeSpan(h, m, sec);
        }
        else if (int.TryParse(s, out int seconds))
        {
            value = TimeSpan.FromSeconds(seconds);
        }
        else
        {
            var m = System.Text.RegularExpressions.Regex.Match(s, @"^(?:(\d+)d)?(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success || m.Length == 0) return false;
            int G(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
            value = new TimeSpan(G(1), G(2), G(3), G(4));
        }

        if (negative) value = -value;
        return true;
    }

    public static string FormatCorrection(TimeSpan value)
    {
        string sign = value < TimeSpan.Zero ? "-" : "+";
        var a = value.Duration();
        return $"{sign}{(int)a.TotalHours:00}:{a.Minutes:00}:{a.Seconds:00}";
    }
}
