using System.Formats.Tar;
using System.IO.Compression;

namespace Geotagger.Core.ExifTool;

/// <summary>
/// Downloads the current exiftool release from exiftool.org into a private folder.
/// Windows gets the official 64-bit package; macOS/Linux get the Perl distribution
/// (both macOS and Linux ship with Perl).
/// </summary>
public static class ExifToolInstaller
{
    private const string BaseUrl = "https://exiftool.org/";

    public static async Task<string> GetLatestVersionAsync(HttpClient http, CancellationToken ct = default)
    {
        string v = (await http.GetStringAsync(BaseUrl + "ver.txt", ct).ConfigureAwait(false)).Trim();
        if (v.Length == 0 || !char.IsDigit(v[0])) throw new ExifToolException($"Unexpected version string from exiftool.org: '{v}'.");
        return v;
    }

    /// <summary>Downloads and installs exiftool; returns the path of the executable/script.</summary>
    public static async Task<string> InstallAsync(HttpClient http, string? installDirectory = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        installDirectory ??= ExifToolLocator.ManagedInstallDirectory;
        string version = await GetLatestVersionAsync(http, ct).ConfigureAwait(false);

        string work = Path.Combine(Path.GetTempPath(), "geotagger-exiftool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                string zip = Path.Combine(work, "exiftool.zip");
                await DownloadAsync(http, [
                    $"{BaseUrl}exiftool-{version}_64.zip",
                    $"https://sourceforge.net/projects/exiftool/files/exiftool-{version}_64.zip/download",
                ], zip, progress, ct).ConfigureAwait(false);

                string extracted = Path.Combine(work, "x");
                ZipFile.ExtractToDirectory(zip, extracted);
                // Layout: exiftool-VER_64/exiftool(-k).exe + exiftool_files/
                string exe = Directory.EnumerateFiles(extracted, "exiftool*.exe", SearchOption.AllDirectories).FirstOrDefault()
                             ?? throw new ExifToolException("The downloaded package did not contain exiftool.exe.");
                string root = Path.GetDirectoryName(exe)!;
                ReplaceDirectory(root, installDirectory);
                string finalExe = Path.Combine(installDirectory, "exiftool.exe");
                string movedExe = Path.Combine(installDirectory, Path.GetFileName(exe));
                if (!string.Equals(movedExe, finalExe, StringComparison.OrdinalIgnoreCase))
                    File.Move(movedExe, finalExe, overwrite: true);
                return finalExe;
            }
            else
            {
                string tgz = Path.Combine(work, "exiftool.tar.gz");
                await DownloadAsync(http, [
                    $"{BaseUrl}Image-ExifTool-{version}.tar.gz",
                    $"https://sourceforge.net/projects/exiftool/files/Image-ExifTool-{version}.tar.gz/download",
                ], tgz, progress, ct).ConfigureAwait(false);

                string extracted = Path.Combine(work, "x");
                Directory.CreateDirectory(extracted);
                await using (var fs = File.OpenRead(tgz))
                await using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                {
                    await TarFile.ExtractToDirectoryAsync(gz, extracted, overwriteFiles: true, ct).ConfigureAwait(false);
                }
                string script = Directory.EnumerateFiles(extracted, "exiftool", SearchOption.AllDirectories).FirstOrDefault()
                                ?? throw new ExifToolException("The downloaded package did not contain the exiftool script.");
                ReplaceDirectory(Path.GetDirectoryName(script)!, installDirectory);
                string finalScript = Path.Combine(installDirectory, "exiftool");
                File.SetUnixFileMode(finalScript,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                return finalScript;
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task DownloadAsync(HttpClient http, IReadOnlyList<string> urls, string destination,
        IProgress<double>? progress, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(destination);
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    read += n;
                    if (total > 0) progress?.Report((double)read / total.Value);
                }
                if (read < 1024) throw new ExifToolException($"Download from {url} was unexpectedly small.");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
        }
        throw new ExifToolException("Could not download exiftool: " + last?.Message, last);
    }

    private static void ReplaceDirectory(string source, string destination)
    {
        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        try
        {
            Directory.Move(source, destination);
        }
        catch (IOException)
        {
            // Different volumes: copy instead.
            CopyDirectory(source, destination);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }
}
