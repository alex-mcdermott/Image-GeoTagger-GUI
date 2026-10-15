using System.Globalization;
using System.Text.Json;
using Geotagger.Core.Geo;

namespace Geotagger.Services;

public sealed record GeocodeResult(string Name, string Detail, GeoPoint Location, GeoPoint? SouthWest, GeoPoint? NorthEast)
{
    public override string ToString() => Name;
}

/// <summary>Place search via OpenStreetMap Nominatim (interactive use only, one request at a time).</summary>
public sealed class Geocoder(HttpClient http)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _last = DateTime.MinValue;

    public async Task<IReadOnlyList<GeocodeResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Nominatim's usage policy: at most one request per second.
            var wait = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - _last);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            _last = DateTime.UtcNow;

            string url = "https://nominatim.openstreetmap.org/search?format=jsonv2&limit=8&q=" + Uri.EscapeDataString(query.Trim());
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.AcceptLanguage.ParseAdd(CultureInfo.CurrentUICulture.Name.Length > 0 ? CultureInfo.CurrentUICulture.Name : "en");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var results = new List<GeocodeResult>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (!TryDouble(e, "lat", out double lat) || !TryDouble(e, "lon", out double lon)) continue;
                string display = e.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? "" : "";
                int comma = display.IndexOf(',');
                string name = comma > 0 ? display[..comma] : display;
                string detail = comma > 0 ? display[(comma + 1)..].Trim() : "";

                GeoPoint? sw = null, ne = null;
                if (e.TryGetProperty("boundingbox", out var bb) && bb.ValueKind == JsonValueKind.Array && bb.GetArrayLength() == 4 &&
                    double.TryParse(bb[0].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double s) &&
                    double.TryParse(bb[1].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) &&
                    double.TryParse(bb[2].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double w) &&
                    double.TryParse(bb[3].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double east))
                {
                    sw = new GeoPoint(s, w);
                    ne = new GeoPoint(n, east);
                }
                results.Add(new GeocodeResult(name, detail, new GeoPoint(lat, lon), sw, ne));
            }
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool TryDouble(JsonElement e, string name, out double value)
    {
        value = 0;
        return e.TryGetProperty(name, out var v) &&
               double.TryParse(v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText(),
                   NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
