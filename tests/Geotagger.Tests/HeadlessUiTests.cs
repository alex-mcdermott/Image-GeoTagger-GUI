using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Geotagger.Core;
using Geotagger.Core.Geo;
using Geotagger.Core.Photos;
using Geotagger.Services;
using Geotagger.ViewModels;
using Geotagger.Views;

[assembly: AvaloniaTestApplication(typeof(Geotagger.Tests.TestAppBuilder))]

namespace Geotagger.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

internal sealed class FakeUi : IUiServices
{
    public List<string> Messages { get; } = [];
    public Task<string?> PickFolderAsync(string? suggestedStart) => Task.FromResult<string?>(null);
    public Task<IReadOnlyList<string>> PickGpxFilesAsync(string? suggestedStart) => Task.FromResult<IReadOnlyList<string>>([]);
    public Task<string?> PickExifToolAsync() => Task.FromResult<string?>(null);
    public Task<UnsavedChoice> AskUnsavedChangesAsync(int count) => Task.FromResult(UnsavedChoice.Discard);
    public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(true);
    public Task<ExifToolChoice> AskExifToolMissingAsync() => Task.FromResult(ExifToolChoice.Cancel);
    public Task ShowMessageAsync(string title, string message) { Messages.Add($"{title}: {message}"); return Task.CompletedTask; }
    public Task<bool> ShowSettingsAsync(SettingsViewModel settings) => Task.FromResult(false);
    public Task CopyTextAsync(string text) => Task.CompletedTask;
    public Task<string?> GetClipboardTextAsync() => Task.FromResult<string?>(null);
}

/// <summary>
/// Drives the real main view headlessly: open a folder, load a track log, auto-tag, place a photo by
/// "dropping" it on the map, save, and check the files with exiftool. Screenshots are written to
/// the test output folder (ui-*.png) for a visual check.
/// </summary>
public class HeadlessUiTests
{
    private static string LondonId =>
        TimeZoneInfo.GetSystemTimeZones().FirstOrDefault(t => t.Id is "Europe/London" or "GMT Standard Time")?.Id ?? "UTC";

    [AvaloniaFact]
    public async Task End_to_end_geotagging_workflow()
    {
        await using var probe = await TestExifTool.RequireAsync();
        using var root = new TempDir();
        AppPaths.OverrideRoot(Path.Combine(root.Path, "appdata"));
        try
        {
            // Three photos: two taken during the walk, one long before it.
            string folder = Path.Combine(root.Path, "photos");
            Directory.CreateDirectory(folder);
            var a = TestFiles.CreateJpeg(Path.Combine(folder, "a.jpg"), 320, 240, SkiaSharp.SKColors.SeaGreen);
            var b = TestFiles.CreateJpeg(Path.Combine(folder, "b.jpg"), 240, 320, SkiaSharp.SKColors.IndianRed);
            var c = TestFiles.CreateJpeg(Path.Combine(folder, "c.jpg"), 320, 240, SkiaSharp.SKColors.Goldenrod);
            await TestExifTool.RunAsync("-overwrite_original", "-DateTimeOriginal=2024:06:01 10:00:30", a);
            await TestExifTool.RunAsync("-overwrite_original", "-DateTimeOriginal=2024:06:01 10:01:30", b);
            await TestExifTool.RunAsync("-overwrite_original", "-DateTimeOriginal=2024:06:01 07:00:00", c);
            File.Copy(TestFiles.DataPath("walk.gpx"), Path.Combine(folder, "walk.gpx"));

            var tiles = new FakeTileHandler();
            using var http = new HttpClient(tiles);
            var ui = new FakeUi();
            var settings = new AppSettings { CameraTimeZoneId = LondonId };
            var vm = new MainViewModel(ui, settings, http);
            var view = new MainView { DataContext = vm };
            var window = new Window { Width = 1500, Height = 950, Content = view };
            window.Show();

            await vm.InitializeAsync(folder, []);
            Assert.True(vm.IsExifToolReady, string.Join("\n", ui.Messages));
            Assert.Equal(3, vm.Photos.Count);
            Assert.True(vm.IsInfoOpen); // offers to load walk.gpx

            await vm.LoadTracksAsync([Path.Combine(folder, "walk.gpx")]);
            Assert.Single(vm.Tracks);
            Assert.StartsWith("2 of 3", vm.MatchSummary);

            vm.AutoTagAllCommand.Execute(null);
            Assert.Equal(2, vm.PendingCount);

            // Place the remaining photo as if it had been dragged onto the map.
            var photoC = vm.Photos.Single(p => p.FileName == "c.jpg");
            vm.DropOnMap([photoC], new GeoPoint(51.5007, -0.1246));
            Assert.Equal(3, vm.PendingCount);
            Assert.True(vm.Undo.CanUndo);

            await WaitFor(() => vm.Photos.All(p => p.Thumbnail is not null));
            await SnapshotAsync(window, "ui-before-save.png");
            Assert.NotEmpty(vm.MapMarkers);
            Assert.True(tiles.Requests > 0, "the map should request tiles");

            await vm.SaveCommand.ExecuteAsync(null);
            Assert.Equal(0, vm.PendingCount);
            Assert.All(vm.Photos, p => Assert.True(p.IsSaved));
            Assert.False(vm.Undo.CanUndo);
            await SnapshotAsync(window, "ui-after-save.png");

            // Independent check of what landed in the files.
            Assert.Equal(51.5005, double.Parse(await TestExifTool.RunAsync("-n", "-s3", "-Composite:GPSLatitude", a), System.Globalization.CultureInfo.InvariantCulture), 6);
            Assert.Equal("2024:06:01 09:00:30Z", await TestExifTool.RunAsync("-s3", "-GPSDateTime", a));
            Assert.StartsWith("51.5007", await TestExifTool.RunAsync("-n", "-s3", "-Composite:GPSLatitude", c));

            window.Close();
            await vm.DisposeAsync();
        }
        finally
        {
            AppPaths.OverrideRoot(null);
        }
    }

    [AvaloniaFact]
    public void Map_renders_markers_tracks_and_handles_zoom()
    {
        var map = new Map.MapView();
        var window = new Window { Width = 800, Height = 600, Content = map };
        window.Show();
        map.SetView(new GeoPoint(51.5, -0.12), 14);
        map.Tracks = [new Map.MapTrack([new GeoPoint(51.49, -0.13), new GeoPoint(51.51, -0.11)], Avalonia.Media.Colors.Magenta, "t")];
        map.Markers = [new Map.MapMarker(new GeoPoint(51.5, -0.12), [], isSelected: true, isPending: true)];
        Dispatcher.UIThread.RunJobs();

        var centre = map.GeoToScreen(new GeoPoint(51.5, -0.12));
        Assert.Equal(400, centre.X, 1);
        Assert.Equal(300, centre.Y, 1);
        var back = map.ScreenToGeo(new Point(400, 300));
        Assert.Equal(51.5, back.Latitude, 5);

        map.ZoomAt(new Point(100, 100), 16, animate: false);
        var anchor = map.ScreenToGeo(new Point(100, 100));
        Assert.Equal(16, map.Zoom, 6);
        map.ZoomAt(new Point(100, 100), 14, animate: false);
        Assert.Equal(anchor.Latitude, map.ScreenToGeo(new Point(100, 100)).Latitude, 5);

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Assert.NotNull(window.CaptureRenderedFrame());
        window.Close();
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task SnapshotAsync(Window window, string name)
    {
        // Let tiles and layout settle.
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(AppContext.BaseDirectory, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
