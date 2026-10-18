using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using Geotagger.Services;
using Geotagger.ViewModels;

namespace Geotagger.Views;

public partial class MainWindow : FAAppWindow, IUiServices
{
    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private bool _closeConfirmed;
    private bool _disposed;

    // Required by the XAML loader / previewer.
    public MainWindow() : this(new AppSettings(), App.CreateHttpClient(), new StartupArguments(null, [])) { }

    public MainWindow(AppSettings settings, HttpClient http, StartupArguments startup)
    {
        InitializeComponent();
        _settings = settings;

        using (var ico = AssetLoader.Open(new Uri("avares://Geotagger/Assets/Geotagger.ico")))
            ((Window)this).Icon = new WindowIcon(ico);
        using (var png = AssetLoader.Open(new Uri("avares://Geotagger/Assets/Geotagger-64.png")))
            Icon = new Bitmap(png);

        if (settings.WindowWidth is { } w && settings.WindowHeight is { } h && w > 600 && h > 400)
        {
            Width = w;
            Height = h;
        }
        if (settings.WindowMaximized) WindowState = WindowState.Maximized;

        _vm = new MainViewModel(this, settings, http);
        DataContext = _vm;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        AddHandler(DragDrop.DropEvent, OnWindowDrop);

        Opened += async (_, _) => await _vm.InitializeAsync(startup.Folder, startup.GpxFiles);
        Closing += OnClosing;
        Closed += (_, _) => DisposeViewModel();
    }

    internal MainViewModel ViewModel => _vm;

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        SaveWindowState();
        if (_closeConfirmed || _vm.PendingCount == 0) return;

        e.Cancel = true;
        if (await _vm.ConfirmDiscardPendingAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private void SaveWindowState()
    {
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }
        View.SaveLayout();
        _vm.SaveSettings();
    }

    private void DisposeViewModel()
    {
        if (_disposed) return;
        _disposed = true;
        // Stop the exiftool processes; they'd exit on their own once stdin closes, but be tidy.
        try { _vm.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(4)); } catch { /* exiting anyway */ }
    }

    // ------------------------------------------------------------------ Dropping folders / GPX files from the OS

    private static List<string> LocalPaths(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];

    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        if (e.Handled) return;
        var paths = LocalPaths(e);
        bool useful = paths.Any(p => Directory.Exists(p) || p.EndsWith(".gpx", StringComparison.OrdinalIgnoreCase));
        e.DragEffects = useful ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnWindowDrop(object? sender, DragEventArgs e)
    {
        if (e.Handled) return;
        var paths = LocalPaths(e);
        var folder = paths.FirstOrDefault(Directory.Exists);
        var gpx = paths.Where(p => p.EndsWith(".gpx", StringComparison.OrdinalIgnoreCase) && File.Exists(p)).ToList();
        e.Handled = folder is not null || gpx.Count > 0;
        if (folder is not null) await _vm.LoadFolderAsync(folder);
        if (gpx.Count > 0) await _vm.LoadTracksAsync(gpx);
    }

    // ------------------------------------------------------------------ IUiServices

    public async Task<string?> PickFolderAsync(string? suggestedStart)
    {
        var options = new FolderPickerOpenOptions { Title = "Open a folder of photos", AllowMultiple = false };
        if (suggestedStart is not null && Directory.Exists(suggestedStart))
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(suggestedStart);
        var result = await StorageProvider.OpenFolderPickerAsync(options);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<IReadOnlyList<string>> PickGpxFilesAsync(string? suggestedStart)
    {
        var options = new FilePickerOpenOptions
        {
            Title = "Load GPX track logs",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("GPS track logs (*.gpx)") { Patterns = ["*.gpx"], AppleUniformTypeIdentifiers = ["com.topografix.gpx", "public.xml"], MimeTypes = ["application/gpx+xml"] },
                FilePickerFileTypes.All,
            ],
        };
        if (suggestedStart is not null && Directory.Exists(suggestedStart))
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(suggestedStart);
        var result = await StorageProvider.OpenFilePickerAsync(options);
        return result.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickExifToolAsync()
    {
        var options = new FilePickerOpenOptions
        {
            Title = "Locate ExifTool",
            AllowMultiple = false,
            FileTypeFilter = OperatingSystem.IsWindows()
                ? [new FilePickerFileType("ExifTool (exiftool.exe)") { Patterns = ["exiftool*.exe"] }, FilePickerFileTypes.All]
                : [FilePickerFileTypes.All],
        };
        var result = await StorageProvider.OpenFilePickerAsync(options);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<UnsavedChoice> AskUnsavedChangesAsync(int count)
    {
        var dialog = new FAContentDialog
        {
            Title = "Save your changes?",
            Content = $"You have {count} unsaved location change{(count == 1 ? "" : "s")}. Save {(count == 1 ? "it" : "them")} to the files before continuing?",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Primary,
        };
        return await dialog.ShowAsync(this) switch
        {
            FAContentDialogResult.Primary => UnsavedChoice.Save,
            FAContentDialogResult.Secondary => UnsavedChoice.Discard,
            _ => UnsavedChoice.Cancel,
        };
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var dialog = new FAContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = confirmText,
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Close,
        };
        return await dialog.ShowAsync(this) == FAContentDialogResult.Primary;
    }

    public async Task<ExifToolChoice> AskExifToolMissingAsync()
    {
        var text = new TextBlock
        {
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 440,
            Text = "Geotagger uses ExifTool by Phil Harvey to read and write photo metadata, but it couldn't find it on this computer.\n\n" +
                   "It can download the latest version from exiftool.org for you (about 10 MB), or you can point it at a copy you already have.",
        };
        var dialog = new FAContentDialog
        {
            Title = "ExifTool is needed",
            Content = text,
            PrimaryButtonText = "Download ExifTool",
            SecondaryButtonText = "Locate ExifTool…",
            CloseButtonText = "Not now",
            DefaultButton = FAContentDialogButton.Primary,
        };
        return await dialog.ShowAsync(this) switch
        {
            FAContentDialogResult.Primary => ExifToolChoice.Download,
            FAContentDialogResult.Secondary => ExifToolChoice.Browse,
            _ => ExifToolChoice.Cancel,
        };
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new FAContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 460 },
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync(this);
    }

    public async Task<bool> ShowSettingsAsync(SettingsViewModel settings)
    {
        var dialog = new FAContentDialog
        {
            Title = "Settings",
            Content = new SettingsView { DataContext = settings },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = FAContentDialogButton.Primary,
        };
        settings.PropertyChanged += (_, _) => dialog.IsPrimaryButtonEnabled = settings.CanSave;
        return await dialog.ShowAsync(this) == FAContentDialogResult.Primary;
    }

    public async Task CopyTextAsync(string text)
    {
        if (Clipboard is { } c) await c.SetTextAsync(text);
    }

    public async Task<string?> GetClipboardTextAsync() =>
        Clipboard is { } c ? await c.TryGetTextAsync() : null;
}
