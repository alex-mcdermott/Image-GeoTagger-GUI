# Geotagger

A desktop app for adding GPS locations to camera RAW files (and JPEG/HEIC/TIFF), either by placing photos on a map, the way Lightroom Classic's Map module works, or automatically from GPX track logs. It runs on Windows and macOS, uses [ExifTool](https://exiftool.org) under the hood, and has a Windows 11 (Fluent) look.

## Features

- **Open a folder** of photos. RAW+JPEG pairs are treated as one photo, and existing XMP sidecars are detected. Embedded previews give fast, correctly rotated thumbnails without decoding the RAWs.
- **Place photos on a map:**
  - Drag thumbnails onto the map.
  - Right-click the map and choose *Place selected photos here*.
  - Drag existing pins to move them.
  - Paste coordinates in almost any format: decimal, degrees-minutes-seconds, or Google Maps/OpenStreetMap links.
  - Search for a place.
- **Auto-tag from GPX track logs:**
  - Photo times are matched to the track, interpolating between fixes.
  - Settings for the camera's time zone (DST-aware), clock correction and maximum time gap.
  - A summary shows how many photos fall inside the track, and the time ranges to compare when they don't.
- **Review before writing:** unsaved changes show as orange pins and badges, with full undo/redo, until you press **Save**.
- **Safe writing:**
  - Locations go into the files, into XMP sidecars, or both.
  - File modification dates are kept, and you can choose to keep `_original` backups.
  - Every saved file is read back to verify the location landed.
- Map styles: Streets, Satellite, Hybrid, Terrain and Dark. Pins cluster when zoomed out, and hovering a pin shows a preview.
- Follows the system light/dark theme.

## Download

Get the latest build from the **Releases** page:

| Platform | File | Notes |
| --- | --- | --- |
| Windows 10/11 (x64) | `Geotagger-x.y.z-win-x64.zip` | Unzip and run `Geotagger.exe`. ExifTool is included. |
| macOS (Apple silicon) | `Geotagger-x.y.z-osx-arm64.zip` | Unzip and move `Geotagger.app` to Applications. ExifTool is included. |
| macOS (Intel) | `Geotagger-x.y.z-osx-x64.zip` | As above. |

The macOS app is not notarised. The first time, right-click it and choose **Open**, or run `xattr -dr com.apple.quarantine /Applications/Geotagger.app`.

If ExifTool isn't bundled or installed, Geotagger offers to download the latest version from exiftool.org, or you can point it at an existing copy in **Settings**.

## Using it

1. **Open folder** (Ctrl/⌘+O), or drop a folder onto the window. GPX files in the folder are offered automatically.
2. Select photos in the filmstrip (Ctrl/⌘ and Shift for multiple), then do one of the following:
   - drag them onto the map;
   - right-click the map → *Place selected photos here*;
   - paste coordinates (Ctrl/⌘+V) or type them in the Photo panel;
   - search for a place, then *Place selected photos at the search pin*.
3. With track logs, choose **Add GPX**, check the camera's time zone, and click **Auto-tag all**. If nothing matches, compare the *Track* and *Photos* times shown in the panel and adjust the time zone or the clock correction (for example `+00:01:30` if the camera clock was 90 seconds slow).
4. Check the orange pins, then **Save** (Ctrl/⌘+S).

| Shortcut | Action |
| --- | --- |
| Ctrl/⌘+O | Open folder |
| Ctrl/⌘+G | Add GPX track log |
| Ctrl/⌘+S | Save |
| Ctrl/⌘+Z / Ctrl/⌘+Y | Undo / redo |
| Ctrl/⌘+C / Ctrl/⌘+V | Copy / paste coordinates between photos |
| Ctrl/⌘+A, Esc | Select all / none |
| Ctrl/⌘+F | Search the map |
| Shift+drag on map | Box-select pins |
| Double-click map | Zoom in |

### What gets written

| Setting | RAW files | JPEG/HEIC/TIFF | XMP sidecars |
| --- | --- | --- | --- |
| *Write into the photo files* (default) | EXIF GPS tags | EXIF GPS tags | Existing sidecars updated |
| *Write XMP sidecars* | untouched | EXIF GPS tags | Created or updated (`IMG_1234.xmp`) |
| *Both* | EXIF GPS tags | EXIF GPS tags | Created or updated |

The tags written are `GPSLatitude`/`GPSLongitude` with their `Ref` tags, plus `GPSAltitude` when the track has elevation, and `GPSDateStamp`/`GPSTimeStamp` for track-log matches. Sidecars get the equivalent `XMP-exif` tags, and other sidecar content (Lightroom edits, labels and so on) is preserved. Raw image data is never re-encoded: ExifTool rewrites only the metadata.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet run --project src/Geotagger            # run the app
dotnet run --project src/Geotagger -- "D:\Photos\Trip" track.gpx   # open a folder (and GPX) directly
dotnet test                                   # unit, ExifTool integration and headless UI tests
```

The integration tests need `exiftool` on the PATH (or in a standard install location). Two opt-in tests are controlled by environment variables:
- `GEOTAGGER_SAMPLE_RAW=path\to\file.ORF` writes to a temporary copy of a real camera RAW.
- `GEOTAGGER_TEST_DOWNLOAD=1` tests the built-in ExifTool downloader.

Packaging:

```powershell
./build/publish-windows.ps1 -Version 1.2.3       # artifacts/Geotagger-1.2.3-win-x64.zip
```
```sh
bash build/package-macos.sh osx-arm64 1.2.3      # artifacts/Geotagger-1.2.3-osx-arm64.zip (run on a Mac)
```

Pushing a tag such as `v1.2.3` runs `.github/workflows/release.yml`, which builds all three packages and attaches them to a GitHub release.

### Project layout

```
src/Geotagger.Core   Platform-neutral logic: GPX parsing, track matching, time zones, ExifTool process pool, reading/writing GPS
src/Geotagger        Avalonia UI: map control, filmstrip, view models, thumbnails
tests/               xUnit tests, including a headless end-to-end UI test
build/               Icon generator and packaging scripts
```

Built with [Avalonia](https://avaloniaui.net) and [FluentAvalonia](https://github.com/amwx/FluentAvalonia). The map is a small purpose-built tile renderer (`src/Geotagger/Map`).

## Credits

- [ExifTool](https://exiftool.org) by Phil Harvey (Perl Artistic License / GPL), bundled in release builds.
- Map data © [OpenStreetMap](https://www.openstreetmap.org/copyright) contributors. Satellite imagery © Esri, Maxar, Earthstar Geographics. Terrain © [OpenTopoMap](https://opentopomap.org) (CC-BY-SA). Dark map © [CARTO](https://carto.com/attributions). Place search by [Nominatim](https://nominatim.org).
- Map tiles and search are fetched on demand for interactive use and cached locally, following the providers' usage policies.

## License

[MIT](LICENSE)
