using System.Diagnostics;

namespace Geotagger.Core.ExifTool;

/// <summary>How to launch exiftool: an executable plus any leading arguments (e.g. a Perl script path).</summary>
public sealed record ExifToolLocation(string FileName, IReadOnlyList<string> PrefixArguments)
{
    public ExifToolLocation(string fileName) : this(fileName, []) { }

    public string Display => PrefixArguments.Count == 0 ? FileName : $"{FileName} {string.Join(' ', PrefixArguments)}";
}

public static class ExifToolLocator
{
    private static string ExeName => OperatingSystem.IsWindows() ? "exiftool.exe" : "exiftool";

    /// <summary>Where the built-in downloader installs exiftool.</summary>
    public static string ManagedInstallDirectory => Path.Combine(AppPaths.DataDirectory, "exiftool");

    /// <summary>Candidate paths in priority order (they may not exist).</summary>
    public static IEnumerable<string> Candidates(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath)) yield return configuredPath!;

        string? env = Environment.GetEnvironmentVariable("GEOTAGGER_EXIFTOOL");
        if (!string.IsNullOrWhiteSpace(env)) yield return env!;

        // Bundled next to the app (release builds ship it), including inside a macOS .app bundle.
        string baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "exiftool", ExeName);
        yield return Path.Combine(baseDir, ExeName);
        if (OperatingSystem.IsMacOS())
            yield return Path.GetFullPath(Path.Combine(baseDir, "..", "Resources", "exiftool", "exiftool"));

        // Installed by our own downloader.
        yield return Path.Combine(ManagedInstallDirectory, ExeName);

        // On the PATH.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = dir.Trim().Trim('"');
            if (trimmed.Length == 0) continue;
            yield return Path.Combine(trimmed, ExeName);
        }

        // Common install locations.
        if (OperatingSystem.IsWindows())
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(pf, "ExifTool", "exiftool.exe");
            yield return Path.Combine(local, "Programs", "ExifTool", "ExifTool.exe");
            yield return Path.Combine(pf, "ExifToolGUI", "exiftool.exe");
            if (!string.IsNullOrEmpty(pf86)) yield return Path.Combine(pf86, "ExifToolGUI", "exiftool.exe");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "exiftool.exe");
        }
        else
        {
            yield return "/opt/homebrew/bin/exiftool";
            yield return "/usr/local/bin/exiftool";
            yield return "/opt/local/bin/exiftool";
            yield return "/usr/bin/exiftool";
        }
    }

    public static ExifToolLocation ToLocation(string path)
    {
        // A bare Perl script (e.g. from the Image-ExifTool tarball) without the execute bit
        // can still be run through perl.
        if (!OperatingSystem.IsWindows() && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var mode = File.GetUnixFileMode(path);
                if ((mode & UnixFileMode.UserExecute) == 0) return new ExifToolLocation("perl", [path]);
            }
            catch { /* fall through */ }
        }
        if (OperatingSystem.IsWindows() && path.EndsWith(".pl", StringComparison.OrdinalIgnoreCase))
            return new ExifToolLocation("perl", [path]);
        return new ExifToolLocation(path);
    }

    /// <summary>Finds the first working exiftool and returns it with its version.</summary>
    public static async Task<(ExifToolLocation Location, string Version)?> FindAsync(string? configuredPath, CancellationToken ct = default)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var candidate in Candidates(configuredPath))
        {
            string full;
            try { full = Path.GetFullPath(candidate); } catch { continue; }
            if (!seen.Add(full) || !File.Exists(full)) continue;

            var location = ToLocation(full);
            var version = await GetVersionAsync(location, ct).ConfigureAwait(false);
            if (version is not null) return (location, version);
        }
        return null;
    }

    /// <summary>Runs <c>exiftool -ver</c>; returns null if it isn't a working exiftool.</summary>
    public static async Task<string?> GetVersionAsync(ExifToolLocation location, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = location.FileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            foreach (var a in location.PrefixArguments) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("-ver");

            using var p = Process.Start(psi);
            if (p is null) return null;
            p.StandardInput.Close();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            string output = await p.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            string v = output.Trim();
            return p.ExitCode == 0 && v.Length > 0 && char.IsDigit(v[0]) ? v : null;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
