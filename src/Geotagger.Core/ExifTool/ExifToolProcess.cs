using System.Diagnostics;
using System.Text;

namespace Geotagger.Core.ExifTool;

public sealed record ExifToolResult(string Output, string Errors)
{
    public IEnumerable<string> ErrorLines =>
        SplitLines(Errors).Where(l => l.StartsWith("Error", StringComparison.OrdinalIgnoreCase));

    public IEnumerable<string> WarningLines =>
        SplitLines(Errors).Where(l => l.StartsWith("Warning", StringComparison.OrdinalIgnoreCase));

    public bool HasErrors => ErrorLines.Any();

    /// <summary>Parses exiftool's summary line, e.g. "    1 image files updated".</summary>
    public int CountOf(string what)
    {
        foreach (var line in SplitLines(Output))
        {
            string t = line.Trim();
            if (t.EndsWith(what, StringComparison.OrdinalIgnoreCase))
            {
                var first = t.Split(' ', 2)[0];
                if (int.TryParse(first, out int n)) return n;
            }
        }
        return 0;
    }

    private static IEnumerable<string> SplitLines(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed class ExifToolException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// One long-running exiftool process in <c>-stay_open</c> mode. Commands are serialised; use
/// <see cref="ExifToolService"/> for a small pool.
/// </summary>
public sealed class ExifToolProcess : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly ExifToolLocation _location;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private int _sequence;

    // Per-command state, guarded by _gate.
    private StringBuilder _stdout = new();
    private StringBuilder _stderr = new();
    private TaskCompletionSource? _stdoutDone;
    private TaskCompletionSource? _stderrDone;
    private string _readyToken = "";

    public ExifToolProcess(ExifToolLocation location) => _location = location;

    public bool IsRunning => _process is { HasExited: false };

    private void EnsureStarted()
    {
        if (IsRunning) return;
        _process?.Dispose();

        var psi = new ProcessStartInfo
        {
            FileName = _location.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = Utf8NoBom,
        };
        foreach (var a in _location.PrefixArguments) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("-stay_open");
        psi.ArgumentList.Add("True");
        psi.ArgumentList.Add("-@");
        psi.ArgumentList.Add("-");

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => OnLine(e.Data, isError: false);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data, isError: true);
        p.Exited += (_, _) =>
        {
            var ex = new ExifToolException("exiftool exited unexpectedly.");
            _stdoutDone?.TrySetException(ex);
            _stderrDone?.TrySetException(ex);
        };

        if (!p.Start()) throw new ExifToolException($"Could not start exiftool at '{_location.FileName}'.");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _process = p;
    }

    private void OnLine(string? line, bool isError)
    {
        if (line is null) return;
        if (line.Trim() == _readyToken && _readyToken.Length > 0)
        {
            (isError ? _stderrDone : _stdoutDone)?.TrySetResult();
            return;
        }
        (isError ? _stderr : _stdout).AppendLine(line);
    }

    /// <summary>Runs one exiftool command (one argument per element) and waits for it to finish.</summary>
    public async Task<ExifToolResult> ExecuteAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStarted();
            int id = Interlocked.Increment(ref _sequence);
            _readyToken = "{ready" + id + "}";
            _stdout = new StringBuilder();
            _stderr = new StringBuilder();
            _stdoutDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stderrDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var sb = new StringBuilder();
            foreach (var arg in args)
            {
                if (arg.Contains('\n') || arg.Contains('\r'))
                    throw new ArgumentException("exiftool arguments cannot contain line breaks.", nameof(args));
                sb.Append(arg).Append('\n');
            }
            // -echo4 prints to stderr after processing, so we know when stderr is complete too.
            sb.Append("-echo4\n").Append(_readyToken).Append('\n');
            sb.Append("-execute").Append(id).Append('\n');

            var stdin = _process!.StandardInput;
            await stdin.WriteAsync(sb.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await Task.WhenAll(_stdoutDone.Task, _stderrDone.Task).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The process is mid-command; the only safe way to recover is to restart it.
                Kill();
                throw;
            }

            return new ExifToolResult(_stdout.ToString(), _stderr.ToString());
        }
        finally
        {
            _readyToken = "";
            _gate.Release();
        }
    }

    private void Kill()
    {
        try { _process?.Kill(entireProcessTree: true); } catch { /* already gone */ }
        _process?.Dispose();
        _process = null;
    }

    public async ValueTask DisposeAsync()
    {
        var p = _process;
        if (p is null) return;
        try
        {
            if (!p.HasExited)
            {
                await p.StandardInput.WriteAsync("-stay_open\nFalse\n").ConfigureAwait(false);
                await p.StandardInput.FlushAsync().ConfigureAwait(false);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { Kill(); }
            }
        }
        catch
        {
            Kill();
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            _gate.Dispose();
        }
    }
}
