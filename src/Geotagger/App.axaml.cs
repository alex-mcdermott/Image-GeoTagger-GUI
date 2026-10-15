using System.Net;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Geotagger.Services;
using Geotagger.Views;

namespace Geotagger;

public partial class App : Application
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "1.0";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = SettingsStore.Load();
            ApplyTheme(settings.Theme);
            var startup = StartupArguments.Parse(desktop.Args ?? []);
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow(settings, CreateHttpClient(), startup);
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static void ApplyTheme(string theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 6,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        // Tile and geocoding services require an identifying User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Geotagger/{Version} (desktop photo geotagging app)");
        return http;
    }
}

/// <summary>Command line: Geotagger [folder] [track.gpx ...]</summary>
public sealed record StartupArguments(string? Folder, IReadOnlyList<string> GpxFiles)
{
    public static StartupArguments Parse(IReadOnlyList<string> args)
    {
        string? folder = null;
        var gpx = new List<string>();
        foreach (var a in args)
        {
            if (string.IsNullOrWhiteSpace(a) || a.StartsWith("-psn_", StringComparison.Ordinal)) continue; // macOS Finder noise
            string full;
            try { full = Path.GetFullPath(a); } catch { continue; }
            if (Directory.Exists(full)) folder ??= full;
            else if (File.Exists(full) && Path.GetExtension(full).Equals(".gpx", StringComparison.OrdinalIgnoreCase)) gpx.Add(full);
            else if (File.Exists(full)) folder ??= Path.GetDirectoryName(full);
        }
        return new StartupArguments(folder, gpx);
    }
}
