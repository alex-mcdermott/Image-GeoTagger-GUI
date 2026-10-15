namespace Geotagger.Map;

/// <summary>A raster tile layer. The URL template uses {z}, {x}, {y} and optionally {s} for subdomains.</summary>
public sealed record TileLayer(string Id, string UrlTemplate, int MaxZoom, string[]? Subdomains = null, int MaxConnections = 2)
{
    public string GetUrl(int z, int x, int y)
    {
        string url = UrlTemplate.Replace("{z}", z.ToString()).Replace("{x}", x.ToString()).Replace("{y}", y.ToString());
        if (Subdomains is { Length: > 0 })
            url = url.Replace("{s}", Subdomains[(x + y) % Subdomains.Length]);
        return url;
    }
}

public sealed record MapStyle(string Id, string Name, IReadOnlyList<TileLayer> Layers, string Attribution, bool IsDark = false)
{
    public override string ToString() => Name;
}

public static class MapStyles
{
    private static readonly TileLayer Osm = new("osm", "https://tile.openstreetmap.org/{z}/{x}/{y}.png", 19);

    private static readonly TileLayer EsriImagery = new("esri-imagery",
        "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}", 19, MaxConnections: 4);

    private static readonly TileLayer EsriLabels = new("esri-labels",
        "https://server.arcgisonline.com/ArcGIS/rest/services/Reference/World_Boundaries_and_Places/MapServer/tile/{z}/{y}/{x}", 19, MaxConnections: 4);

    private static readonly TileLayer OpenTopo = new("opentopomap", "https://{s}.tile.opentopomap.org/{z}/{x}/{y}.png", 17, ["a", "b", "c"]);

    private static readonly TileLayer CartoDark = new("carto-dark", "https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png", 20, ["a", "b", "c", "d"], 4);

    public static readonly MapStyle Streets = new("streets", "Streets", [Osm], "© OpenStreetMap contributors");

    public static readonly MapStyle Satellite = new("satellite", "Satellite", [EsriImagery],
        "Imagery © Esri, Maxar, Earthstar Geographics");

    public static readonly MapStyle Hybrid = new("hybrid", "Hybrid", [EsriImagery, EsriLabels],
        "Imagery © Esri, Maxar, Earthstar Geographics · Labels © Esri");

    public static readonly MapStyle Topo = new("topo", "Terrain", [OpenTopo],
        "© OpenStreetMap contributors, SRTM · Style © OpenTopoMap (CC-BY-SA)");

    public static readonly MapStyle Dark = new("dark", "Dark", [CartoDark], "© OpenStreetMap contributors © CARTO", IsDark: true);

    public static IReadOnlyList<MapStyle> All { get; } = [Streets, Satellite, Hybrid, Topo, Dark];

    public static MapStyle ById(string? id) => All.FirstOrDefault(s => s.Id == id) ?? Streets;
}
