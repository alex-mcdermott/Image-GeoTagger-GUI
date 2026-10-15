using System.Threading.Channels;

namespace Geotagger.Core.ExifTool;

/// <summary>A small pool of stay-open exiftool processes so reads, thumbnails and writes overlap.</summary>
public sealed class ExifToolService : IAsyncDisposable
{
    private readonly Channel<ExifToolProcess> _available;
    private readonly List<ExifToolProcess> _all = [];

    private ExifToolService(ExifToolLocation location, string version, int poolSize)
    {
        Location = location;
        Version = version;
        _available = Channel.CreateUnbounded<ExifToolProcess>();
        for (int i = 0; i < poolSize; i++)
        {
            var p = new ExifToolProcess(location);
            _all.Add(p);
            _available.Writer.TryWrite(p);
        }
    }

    public ExifToolLocation Location { get; }

    public string Version { get; }

    public int PoolSize => _all.Count;

    public static async Task<ExifToolService> CreateAsync(ExifToolLocation location, int poolSize = 3, CancellationToken ct = default)
    {
        string? version = await ExifToolLocator.GetVersionAsync(location, ct).ConfigureAwait(false);
        if (version is null) throw new ExifToolException($"'{location.FileName}' did not respond like exiftool.");
        return new ExifToolService(location, version, Math.Max(1, poolSize));
    }

    public async Task<ExifToolResult> ExecuteAsync(IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var process = await _available.Reader.ReadAsync(ct).ConfigureAwait(false);
        try
        {
            return await process.ExecuteAsync(args, ct).ConfigureAwait(false);
        }
        finally
        {
            _available.Writer.TryWrite(process);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _available.Writer.TryComplete();
        await Task.WhenAll(_all.Select(p => p.DisposeAsync().AsTask())).ConfigureAwait(false);
    }
}
