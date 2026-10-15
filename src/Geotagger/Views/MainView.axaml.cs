using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Geotagger.Core.Geo;
using Geotagger.ViewModels;

namespace Geotagger.Views;

public sealed record PhotoDragPayload(IReadOnlyList<PhotoItemViewModel> Photos);

public partial class MainView : UserControl
{
    internal static readonly DataFormat<PhotoDragPayload> PhotoFormat =
        DataFormat.CreateInProcessFormat<PhotoDragPayload>("geotagger-photos");

    private MainViewModel? _vm;
    private bool _syncingSelection;

    // Filmstrip drag state.
    private PointerPressedEventArgs? _pressArgs;
    private Point _pressPoint;
    private PhotoItemViewModel? _pressItem;
    private bool _deferredSelect;

    public MainView()
    {
        InitializeComponent();

        Filmstrip.SelectionChanged += OnFilmstripSelectionChanged;
        Filmstrip.AddHandler(PointerPressedEvent, OnFilmstripPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Filmstrip.AddHandler(PointerMovedEvent, OnFilmstripPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        Filmstrip.AddHandler(PointerReleasedEvent, OnFilmstripPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        Filmstrip.AddHandler(PointerWheelChangedEvent, OnFilmstripWheel, RoutingStrategies.Tunnel, handledEventsToo: true);

        Map.MarkersClicked += (_, e) => _vm?.SelectFromMap(e.Markers, e.Modifiers);
        Map.BoxSelected += (_, e) => _vm?.SelectFromMap(e.Markers, e.Modifiers);
        Map.MarkersMoved += (_, e) => _vm?.MoveMarkers(e.Markers, e.To);
        Map.ItemsDropped += OnMapDrop;
        Map.DropItemCount = data => data.TryGetValue(PhotoFormat)?.Photos.Count ?? 0;
        Map.ViewChanged += OnMapViewChanged;
        // Hover cards shouldn't disappear under the controls floating over the map.
        Map.AvoidAreas = () => new Control[] { SearchPanel, MapControlsPanel, InfoBar }
            .Where(c => c.IsVisible && c.Bounds.Height > 0 && c.Bounds.Width > 0)
            .Select(c => c.TranslatePoint(default, Map) is { } p ? new Rect(p, c.Bounds.Size) : default)
            .Where(r => r.Width > 0);
        if (Map.ContextFlyout is MenuFlyout flyout) flyout.Opening += OnMapContextOpening;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.SelectionSyncRequested -= SyncSelection;
            _vm.FitRequested -= OnFitRequested;
            _vm.CenterRequested -= OnCenterRequested;
        }
        _vm = DataContext as MainViewModel;
        if (_vm is null) return;

        _vm.SelectionSyncRequested += SyncSelection;
        _vm.FitRequested += OnFitRequested;
        _vm.CenterRequested += OnCenterRequested;

        var s = _vm.Settings;
        if (s.MapLatitude is { } lat && s.MapLongitude is { } lon && s.MapZoom is { } zoom)
            Map.SetView(new GeoPoint(lat, lon), zoom);
        else
            Map.SetView(new GeoPoint(35, 0), 2.5);

        if (s.FilmstripHeight is { } fh && fh > 120) MainGrid.RowDefinitions[2].Height = new GridLength(fh);
        if (s.SidePaneWidth is { } sw && sw > 260) MainGrid.ColumnDefinitions[2].Width = new GridLength(sw);
    }

    /// <summary>Stores layout so the next launch looks the same.</summary>
    public void SaveLayout()
    {
        if (_vm is null) return;
        _vm.Settings.FilmstripHeight = MainGrid.RowDefinitions[2].ActualHeight;
        _vm.Settings.SidePaneWidth = MainGrid.ColumnDefinitions[2].ActualWidth;
        OnMapViewChanged(this, EventArgs.Empty);
    }

    private void OnFitRequested(IReadOnlyCollection<GeoPoint> points, double maxZoom) => Map.FitTo(points, maxZoom);

    private void OnCenterRequested(GeoPoint point, double? zoom) => Map.FlyTo(point, zoom is { } z ? Math.Max(z, Map.Zoom) : null);

    private void OnMapViewChanged(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        var c = Map.Center;
        _vm.Settings.MapLatitude = c.Latitude;
        _vm.Settings.MapLongitude = c.Longitude;
        _vm.Settings.MapZoom = Map.Zoom;
    }

    // ------------------------------------------------------------------ Selection sync

    private void OnFilmstripSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || _vm is null) return;
        _vm.SetSelectionFromView(OrderedSelection());
    }

    private List<PhotoItemViewModel> OrderedSelection()
    {
        var selected = Filmstrip.SelectedItems?.OfType<PhotoItemViewModel>().ToHashSet() ?? [];
        return _vm!.VisiblePhotos.Where(selected.Contains).ToList();
    }

    private void SyncSelection(IReadOnlyList<PhotoItemViewModel> photos)
    {
        if (_vm is null) return;
        _syncingSelection = true;
        try
        {
            var model = Filmstrip.Selection;
            model.BeginBatchUpdate();
            try
            {
                model.Clear();
                foreach (var p in photos)
                {
                    int i = _vm.VisiblePhotos.IndexOf(p);
                    if (i >= 0) model.Select(i);
                }
            }
            finally
            {
                model.EndBatchUpdate();
            }
        }
        finally
        {
            _syncingSelection = false;
        }
        _vm.SetSelectionFromView(OrderedSelection());
        if (photos.Count > 0 && _vm.VisiblePhotos.Contains(photos[0])) Filmstrip.ScrollIntoView(photos[0]);
    }

    // ------------------------------------------------------------------ Filmstrip → map drag

    private void OnFilmstripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressArgs = null;
        _deferredSelect = false;
        var point = e.GetCurrentPoint(Filmstrip);
        if (!point.Properties.IsLeftButtonPressed || _vm is null) return;

        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as PhotoItemViewModel;
        if (item is null) return;

        _pressArgs = e;
        _pressPoint = point.Position;
        _pressItem = item;

        // Pressing on an item of a multi-selection shouldn't collapse the selection before a drag.
        if (item.IsSelected && _vm.SelectedPhotos.Count > 1 && e.KeyModifiers == KeyModifiers.None && e.ClickCount == 1)
        {
            _deferredSelect = true;
            e.Handled = true;
        }
    }

    private async void OnFilmstripPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressArgs is null || _pressItem is null || _vm is null) return;
        var point = e.GetCurrentPoint(Filmstrip);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _pressArgs = null;
            return;
        }
        var d = point.Position - _pressPoint;
        if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;

        var args = _pressArgs;
        _pressArgs = null;
        _deferredSelect = false;

        IReadOnlyList<PhotoItemViewModel> photos = _pressItem.IsSelected ? _vm.SelectedPhotos.ToList() : [_pressItem];
        if (photos.Count == 0) return;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(PhotoFormat, new PhotoDragPayload(photos)));
        try
        {
            await DragDrop.DoDragDropAsync(args, transfer, DragDropEffects.Link | DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            _vm.StatusText = "Drag failed: " + ex.Message;
        }
    }

    private void OnFilmstripPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_deferredSelect && _pressItem is not null) SyncSelection([_pressItem]);
        _deferredSelect = false;
        _pressArgs = null;
    }

    private void OnFilmstripWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y)) return; // trackpads scroll sideways natively
        var sv = Filmstrip.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (sv is null) return;
        double max = Math.Max(0, sv.Extent.Width - sv.Viewport.Width);
        sv.Offset = new Vector(Math.Clamp(sv.Offset.X - e.Delta.Y * 150, 0, max), sv.Offset.Y);
        e.Handled = true;
    }

    private void OnMapDrop(object? sender, Map.MapDropEventArgs e)
    {
        if (_vm is null || e.Data.TryGetValue(PhotoFormat) is not { } payload) return;
        _vm.DropOnMap(payload.Photos, e.Location);
    }

    // ------------------------------------------------------------------ Map context menu & buttons

    private void OnMapContextOpening(object? sender, EventArgs e)
    {
        int n = _vm?.SelectedPhotos.Count ?? 0;
        PlaceHereMenuItem.IsEnabled = n > 0;
        PlaceHereMenuItem.Header = n switch
        {
            0 => "Place selected photos here",
            1 => "Place the selected photo here",
            _ => $"Place {n} selected photos here",
        };
        SelectHereMenuItem.IsVisible = Map.ContextMarkers is { Count: > 0 };
    }

    private void OnPlaceHereClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null && Map.ContextLocation is { } p) _vm.PlaceSelectionAt(p);
    }

    private void OnSelectHereClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null && Map.ContextMarkers is { } markers) _vm.SelectFromMap(markers, KeyModifiers.None);
    }

    private async void OnCopyContextCoordinatesClick(object? sender, RoutedEventArgs e)
    {
        if (Map.ContextLocation is not { } p) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(CoordinateFormatter.ToDecimal(p));
        if (_vm is not null) _vm.StatusText = $"Copied {CoordinateFormatter.ToDecimal(p)}";
    }

    private void OnZoomInClick(object? sender, RoutedEventArgs e) => Map.ZoomIn();

    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => Map.ZoomOut();

    // ------------------------------------------------------------------ Keyboard

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _vm is null) return;

        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        bool inText = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;
        bool handled = true;

        if (e.KeyModifiers == command)
        {
            switch (e.Key)
            {
                case Key.O: _vm.OpenFolderCommand.Execute(null); break;
                case Key.G: _vm.AddGpxCommand.Execute(null); break;
                case Key.S: _vm.SaveCommand.Execute(null); break;
                case Key.F: SearchBox.Focus(); SearchBox.SelectAll(); break;
                case Key.OemComma: _vm.OpenSettingsCommand.Execute(null); break;
                case Key.Z when !inText: _vm.UndoEditCommand.Execute(null); break;
                case Key.Y when !inText: _vm.RedoEditCommand.Execute(null); break;
                case Key.A when !inText: _vm.SelectAllCommand.Execute(null); break;
                case Key.C when !inText: _vm.CopyCoordinatesCommand.Execute(null); break;
                case Key.V when !inText:
                    if (!await _vm.PasteCoordinatesAsync()) _vm.StatusText = "The clipboard doesn't contain coordinates (or nothing is selected).";
                    break;
                default: handled = false; break;
            }
        }
        else if (e.KeyModifiers == (command | KeyModifiers.Shift) && e.Key == Key.Z && !inText)
        {
            _vm.RedoEditCommand.Execute(null);
        }
        else if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.F5)
        {
            _vm.ReloadFolderCommand.Execute(null);
        }
        else if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Escape && !inText)
        {
            _vm.SelectNoneCommand.Execute(null);
        }
        else
        {
            handled = false;
        }
        e.Handled = handled;
    }
}
