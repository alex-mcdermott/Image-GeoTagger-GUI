namespace Geotagger.ViewModels;

/// <summary>Undo/redo for unsaved location edits.</summary>
public sealed class UndoManager
{
    private sealed record Entry(string Description, IReadOnlyList<(PhotoItemViewModel Photo, PendingState Before, PendingState After)> Changes);

    private readonly Stack<Entry> _undo = new();
    private readonly Stack<Entry> _redo = new();

    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => _undo.TryPeek(out var e) ? e.Description : null;
    public string? RedoDescription => _redo.TryPeek(out var e) ? e.Description : null;

    /// <summary>Applies new pending states and records them as one undoable step. Returns the photos that changed.</summary>
    public IReadOnlyList<PhotoItemViewModel> Apply(string description, IEnumerable<(PhotoItemViewModel Photo, PendingState After)> changes)
    {
        var list = new List<(PhotoItemViewModel, PendingState, PendingState)>();
        foreach (var (photo, after) in changes)
        {
            var normalised = Normalise(photo, after);
            if (normalised == photo.Pending) continue;
            list.Add((photo, photo.Pending, normalised));
            photo.Pending = normalised;
        }
        if (list.Count > 0)
        {
            _undo.Push(new Entry(description, list));
            _redo.Clear();
            Changed?.Invoke();
        }
        return list.Select(c => c.Item1).ToList();
    }

    public IReadOnlyList<PhotoItemViewModel> Undo()
    {
        if (!_undo.TryPop(out var e)) return [];
        foreach (var (photo, before, _) in e.Changes) photo.Pending = before;
        _redo.Push(e);
        Changed?.Invoke();
        return e.Changes.Select(c => c.Photo).ToList();
    }

    public IReadOnlyList<PhotoItemViewModel> Redo()
    {
        if (!_redo.TryPop(out var e)) return [];
        foreach (var (photo, _, after) in e.Changes) photo.Pending = after;
        _undo.Push(e);
        Changed?.Invoke();
        return e.Changes.Select(c => c.Photo).ToList();
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }

    /// <summary>A "change" back to what's already in the file is no change at all.</summary>
    private static PendingState Normalise(PhotoItemViewModel photo, PendingState state)
    {
        if (!state.HasChange) return PendingState.None;
        if (state.Location is null && photo.OriginalLocation is null) return PendingState.None;
        if (state.Location is { } p && photo.OriginalLocation is { } o && p.SamePlaceAs(o) &&
            Nullable.Equals(p.Altitude, o.Altitude) && state.GpsTimeUtc is null)
            return PendingState.None;
        return state;
    }
}
