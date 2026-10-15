using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Geotagger.Map;

public readonly record struct TileKey(string LayerId, int Z, int X, int Y)
{
    public TileKey Parent(int levels) => this with { Z = Z - levels, X = X >> levels, Y = Y >> levels };
}

/// <summary>
/// Memory + disk cache for map tiles with a small worker pool. The map view tells the cache which
/// tiles it currently wants (nearest-first); stale requests are dropped automatically when the
/// view moves on, so panning quickly doesn't queue up hundreds of downloads.
/// </summary>
public sealed class TileCache : IDisposable
{
    private const int MemoryCapacity = 384;
    private static readonly TimeSpan DiskMaxAge = TimeSpan.FromDays(30);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly string _diskRoot;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostLimits = new();

    // Worker-shared state (guarded by _lock).
    private List<(TileLayer Layer, TileKey Key)> _wanted = [];
    private readonly HashSet<TileKey> _inflight = [];

    // UI-thread-only state.
    private readonly Dictionary<TileKey, LinkedListNode<(TileKey Key, Bitmap Bitmap)>> _memory = [];
    private readonly LinkedList<(TileKey Key, Bitmap Bitmap)> _lru = new();
    private readonly Dictionary<TileKey, DateTime> _failed = [];
    private readonly Queue<(Bitmap Bitmap, DateTime When)> _graveyard = new();
    private bool _invalidatePosted;

    public TileCache(HttpClient http, string diskRoot, int workers = 6)
    {
        _http = http;
        _diskRoot = diskRoot;
        for (int i = 0; i < workers; i++) _ = Task.Run(WorkerLoop);
    }

    /// <summary>Raised on the UI thread (coalesced) when new tiles are available.</summary>
    public event Action? TilesChanged;

    public bool TryGet(TileKey key, out Bitmap bitmap)
    {
        if (_memory.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            bitmap = node.Value.Bitmap;
            return true;
        }
        bitmap = null!;
        return false;
    }

    public bool HasFailedRecently(TileKey key) =>
        _failed.TryGetValue(key, out var when) && DateTime.UtcNow - when < RetryAfter;

    /// <summary>Replaces the set of wanted tiles (first = most important). UI thread.</summary>
    public void SetWanted(List<(TileLayer Layer, TileKey Key)> wanted)
    {
        wanted.RemoveAll(w => _memory.ContainsKey(w.Key) || HasFailedRecently(w.Key));
        int count;
        lock (_lock)
        {
            _wanted = wanted;
            count = wanted.Count;
        }
        if (count > 0) _signal.Release(Math.Min(count, 6));
    }

    public void ClearMemory()
    {
        foreach (var n in _lru) _graveyard.Enqueue((n.Bitmap, DateTime.UtcNow));
        _lru.Clear();
        _memory.Clear();
        _failed.Clear();
    }

    public static long DiskUsageBytes(string root) =>
        Directory.Exists(root)
            ? new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
            : 0;

    public void ClearDisk()
    {
        try { if (Directory.Exists(_diskRoot)) Directory.Delete(_diskRoot, recursive: true); } catch { /* in use */ }
    }

    private async Task WorkerLoop()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            (TileLayer Layer, TileKey Key)? job = null;
            lock (_lock)
            {
                for (int i = 0; i < _wanted.Count; i++)
                {
                    var w = _wanted[i];
                    if (_inflight.Contains(w.Key)) continue;
                    _wanted.RemoveAt(i);
                    _inflight.Add(w.Key);
                    job = w;
                    break;
                }
            }

            if (job is null)
            {
                try { await _signal.WaitAsync(ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                continue;
            }

            Bitmap? bitmap = null;
            try
            {
                bitmap = await LoadAsync(job.Value.Layer, job.Value.Key, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                bitmap = null;
            }

            var key = job.Value.Key;
            Dispatcher.UIThread.Post(() => Complete(key, bitmap));
        }
    }

    private void Complete(TileKey key, Bitmap? bitmap)
    {
        lock (_lock) _inflight.Remove(key);

        if (bitmap is null)
        {
            _failed[key] = DateTime.UtcNow;
            return;
        }

        if (_memory.ContainsKey(key))
        {
            _graveyard.Enqueue((bitmap, DateTime.UtcNow));
        }
        else
        {
            _memory[key] = _lru.AddFirst((key, bitmap));
            while (_lru.Count > MemoryCapacity)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _memory.Remove(last.Value.Key);
                // The renderer may still reference an evicted bitmap for a frame; dispose later.
                _graveyard.Enqueue((last.Value.Bitmap, DateTime.UtcNow));
            }
        }

        while (_graveyard.Count > 0 && DateTime.UtcNow - _graveyard.Peek().When > TimeSpan.FromSeconds(5))
            _graveyard.Dequeue().Bitmap.Dispose();

        if (!_invalidatePosted)
        {
            _invalidatePosted = true;
            Dispatcher.UIThread.Post(() =>
            {
                _invalidatePosted = false;
                TilesChanged?.Invoke();
            }, DispatcherPriority.Render);
        }
    }

    private async Task<Bitmap?> LoadAsync(TileLayer layer, TileKey key, CancellationToken ct)
    {
        string path = Path.Combine(_diskRoot, layer.Id, key.Z.ToString(), key.X.ToString(), key.Y + ".tile");
        var info = new FileInfo(path);
        byte[]? bytes = null;

        if (info.Exists && DateTime.UtcNow - info.LastWriteTimeUtc < DiskMaxAge)
        {
            bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        else
        {
            var limiter = _hostLimits.GetOrAdd(layer.Id, _ => new SemaphoreSlim(layer.MaxConnections));
            await limiter.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var response = await _http.GetAsync(layer.GetUrl(key.Z, key.X, key.Y), ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        string tmp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
                        await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
                        File.Move(tmp, path, overwrite: true);
                    }
                    catch (IOException) { /* cache write is best effort */ }
                }
            }
            catch (HttpRequestException)
            {
                bytes = null;
            }
            finally
            {
                limiter.Release();
            }

            // Offline? Fall back to an expired copy.
            if (bytes is null && info.Exists)
                bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }

        if (bytes is null || bytes.Length == 0) return null;
        using var ms = new MemoryStream(bytes);
        return new Bitmap(ms);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _signal.Release(16);
    }
}
