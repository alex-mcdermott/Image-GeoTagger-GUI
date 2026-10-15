using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Geotagger.Core.Geo;

/// <summary>
/// Parses coordinates typed or pasted by the user. Accepts decimal degrees
/// ("51.5007, -0.1246"), hemisphere letters ("51.5007N 0.1246W"), degrees-minutes-seconds
/// ("51°30'02.5"N 0°07'28.6"W"), degrees and decimal minutes, and map links that contain
/// coordinates (Google Maps "@lat,lon", "?q=lat,lon", OpenStreetMap "#map=z/lat/lon").
/// </summary>
public static partial class CoordinateParser
{
    public static bool TryParse(string? text, out GeoPoint point)
    {
        point = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = Normalise(text);

        if (TryParseUrl(s, out point)) return true;

        foreach (var (a, b) in Splits(s))
        {
            if (TryParseComponent(a, isLatitudeHint: true, out double v1, out char h1) &&
                TryParseComponent(b, isLatitudeHint: false, out double v2, out char h2))
            {
                static bool IsLat(char h) => h is 'N' or 'S';
                static bool IsLon(char h) => h is 'E' or 'W';
                // Two latitudes or two longitudes is not a position.
                if ((IsLat(h1) && IsLat(h2)) || (IsLon(h1) && IsLon(h2))) continue;
                // Letters can put longitude first ("0.12W 51.5N").
                if (IsLon(h1) || IsLat(h2)) (v1, v2) = (v2, v1);
                var p = new GeoPoint(v1, v2);
                if (p.IsValid) { point = p; return true; }
            }
        }
        return false;
    }

    private static string Normalise(string text)
    {
        var sb = new StringBuilder(text.Trim());
        sb.Replace('′', '\'').Replace('’', '\'').Replace('‘', '\'').Replace('`', '\'').Replace('´', '\'')
          .Replace('″', '"').Replace('”', '"').Replace('“', '"').Replace("''", "\"")
          .Replace('º', '°').Replace('˚', '°').Replace('−', '-').Replace('–', '-')
          .Replace(' ', ' ').Replace(' ', ' ');
        return sb.ToString();
    }

    private static bool TryParseUrl(string s, out GeoPoint point)
    {
        point = default;
        if (!s.Contains("://", StringComparison.Ordinal) && !s.StartsWith("geo:", StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var regex in new[] { AtPattern(), QueryPattern(), OsmPattern(), GeoUriPattern() })
        {
            var m = regex.Match(s);
            if (m.Success &&
                double.TryParse(m.Groups["lat"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
                double.TryParse(m.Groups["lon"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
            {
                var p = new GeoPoint(lat, lon);
                if (p.IsValid) { point = p; return true; }
            }
        }
        return false;
    }

    /// <summary>Candidate ways to split the input into a latitude part and a longitude part.</summary>
    private static IEnumerable<(string, string)> Splits(string s)
    {
        // 1. Explicit separators.
        foreach (char sep in new[] { ',', ';', '/', '|', '\t' })
        {
            int idx = s.IndexOf(sep);
            if (idx > 0 && s.IndexOf(sep, idx + 1) < 0)
                yield return (s[..idx], s[(idx + 1)..]);
        }

        // 2. Split right after the first hemisphere letter that follows a number ("51.5N 0.1W"),
        //    or right before the second hemisphere letter when letters prefix numbers ("N51.5 W0.1").
        string upper = s.ToUpperInvariant();
        var letters = HemisphereLetters().Matches(upper);
        if (letters.Count == 2)
        {
            int first = letters[0].Index, second = letters[1].Index;
            string before = upper[..first].TrimEnd(' ', '"', '\'', '°');
            bool suffixStyle = before.Length > 0 && char.IsDigit(before[^1]);
            yield return suffixStyle ? (s[..(first + 1)], s[(first + 1)..]) : (s[..second], s[second..]);
        }

        // 3. Two whitespace-separated decimal numbers.
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2) yield return (parts[0], parts[1]);
    }

    private static bool TryParseComponent(string raw, bool isLatitudeHint, out double value, out char hemisphere)
    {
        value = 0;
        hemisphere = '\0';
        string s = raw.Trim().ToUpperInvariant();
        if (s.Length == 0) return false;

        var hem = HemisphereLetters().Matches(s);
        if (hem.Count > 1) return false;
        if (hem.Count == 1)
        {
            hemisphere = hem[0].Value[0];
            s = s.Remove(hem[0].Index, 1);
        }

        bool negative = false;
        s = s.Trim();
        if (s.StartsWith('-')) { negative = true; s = s[1..]; }
        else if (s.StartsWith('+')) s = s[1..];

        var numbers = NumberPattern().Matches(s);
        if (numbers.Count is 0 or > 3) return false;

        // Anything other than numbers, unit symbols and whitespace means this isn't a coordinate.
        string leftover = NumberPattern().Replace(s, " ");
        if (leftover.Any(c => !char.IsWhiteSpace(c) && c is not ('°' or '\'' or '"' or ':' or 'D' or 'M'))) return false;

        double deg = double.Parse(numbers[0].Value, CultureInfo.InvariantCulture);
        double min = numbers.Count > 1 ? double.Parse(numbers[1].Value, CultureInfo.InvariantCulture) : 0;
        double sec = numbers.Count > 2 ? double.Parse(numbers[2].Value, CultureInfo.InvariantCulture) : 0;
        if (numbers.Count > 1 && (min >= 60 || sec >= 60 || deg != Math.Floor(deg))) return false;

        value = deg + min / 60.0 + sec / 3600.0;
        if (negative || hemisphere is 'S' or 'W') value = -value;
        return true;
    }

    [GeneratedRegex(@"@(?<lat>-?\d{1,2}(?:\.\d+)?),(?<lon>-?\d{1,3}(?:\.\d+)?)")]
    private static partial Regex AtPattern();

    [GeneratedRegex(@"[?&](?:q|ll|query|center|daddr|destination)=(?:loc:)?(?<lat>-?\d{1,2}(?:\.\d+)?)(?:,|%2C)\s*(?<lon>-?\d{1,3}(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex QueryPattern();

    [GeneratedRegex(@"map=\d+(?:\.\d+)?/(?<lat>-?\d{1,2}(?:\.\d+)?)/(?<lon>-?\d{1,3}(?:\.\d+)?)")]
    private static partial Regex OsmPattern();

    [GeneratedRegex(@"^geo:(?<lat>-?\d{1,2}(?:\.\d+)?),(?<lon>-?\d{1,3}(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex GeoUriPattern();

    [GeneratedRegex(@"(?<![A-Z])[NSEW](?![A-Z])")]
    private static partial Regex HemisphereLetters();

    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex NumberPattern();
}
