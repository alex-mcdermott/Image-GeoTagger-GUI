<#
.SYNOPSIS
  Builds a self-contained Windows release of Geotagger and zips it with ExifTool bundled.
.EXAMPLE
  ./build/publish-windows.ps1 -Version 1.0.0
#>
param(
    [string]$Version = "1.0.0",
    [string]$Runtime = "win-x64",
    [switch]$NoExifTool
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "artifacts/publish/$Runtime"
$zip = Join-Path $root "artifacts/Geotagger-$Version-$Runtime.zip"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $root "src/Geotagger/Geotagger.csproj") -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    -p:Version=$Version -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
# Native debug symbols from the Skia/HarfBuzz packages aren't needed by users.
Get-ChildItem $out -Filter *.pdb | Remove-Item

if (-not $NoExifTool) {
    # Bundle the official 64-bit Windows ExifTool next to the app (found automatically at startup).
    $ver = ([string](Invoke-WebRequest "https://exiftool.org/ver.txt" -UseBasicParsing).Content).Trim()
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "geotagger-exiftool-$ver"
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    New-Item -ItemType Directory $tmp | Out-Null
    $zipPath = Join-Path $tmp "exiftool.zip"
    try {
        Invoke-WebRequest "https://exiftool.org/exiftool-${ver}_64.zip" -OutFile $zipPath
    } catch {
        Invoke-WebRequest "https://sourceforge.net/projects/exiftool/files/exiftool-${ver}_64.zip/download" -OutFile $zipPath -UserAgent "Wget"
    }
    Expand-Archive $zipPath -DestinationPath $tmp
    $exe = Get-ChildItem $tmp -Recurse -Filter "exiftool*.exe" | Select-Object -First 1
    $dest = Join-Path $out "exiftool"
    Copy-Item $exe.Directory.FullName $dest -Recurse
    Rename-Item (Join-Path $dest $exe.Name) "exiftool.exe"
    Write-Host "Bundled ExifTool $ver"
}

Copy-Item (Join-Path $root "LICENSE") $out
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip
Write-Host "Created $zip"
