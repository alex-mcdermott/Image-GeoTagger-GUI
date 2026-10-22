#!/usr/bin/env bash
# Builds Geotagger.app for macOS (ExifTool bundled) and zips it.
# Usage: build/package-macos.sh [osx-arm64|osx-x64] [version]
set -euo pipefail

RID="${1:-osx-arm64}"
VERSION="${2:-1.0.0}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/artifacts/publish/$RID"
APP="$ROOT/artifacts/$RID/Geotagger.app"
ZIP="$ROOT/artifacts/Geotagger-$VERSION-$RID.zip"

rm -rf "$OUT" "$APP" "$ZIP"
dotnet publish "$ROOT/src/Geotagger/Geotagger.csproj" -c Release -r "$RID" --self-contained true \
  -p:UseAppHost=true -p:DebugType=none -p:Version="$VERSION" -o "$OUT"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$OUT/." "$APP/Contents/MacOS/"
sed "s/__VERSION__/$VERSION/g" "$ROOT/build/macos/Info.plist" > "$APP/Contents/Info.plist"
cp "$ROOT/LICENSE" "$APP/Contents/Resources/"

# App icon from the 1024px master.
ICONSET="$(mktemp -d)/Geotagger.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
  sips -z $size $size "$ROOT/build/macos/icon-1024.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z $double $double "$ROOT/build/macos/icon-1024.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/Geotagger.icns"

# ExifTool (Perl distribution; macOS ships with Perl). Found via Contents/Resources/exiftool.
EXIFTOOL_VERSION="$(curl -fsSL https://exiftool.org/ver.txt | tr -d '[:space:]')"
TMP="$(mktemp -d)"
curl -fsSL --retry 3 "https://exiftool.org/Image-ExifTool-$EXIFTOOL_VERSION.tar.gz" -o "$TMP/exiftool.tgz" \
  || curl -fsSL --retry 3 --retry-all-errors "https://sourceforge.net/projects/exiftool/files/Image-ExifTool-$EXIFTOOL_VERSION.tar.gz/download" -o "$TMP/exiftool.tgz"
tar -xzf "$TMP/exiftool.tgz" -C "$TMP"
mkdir -p "$APP/Contents/Resources/exiftool"
cp -R "$TMP/Image-ExifTool-$EXIFTOOL_VERSION/exiftool" "$TMP/Image-ExifTool-$EXIFTOOL_VERSION/lib" "$APP/Contents/Resources/exiftool/"
chmod +x "$APP/Contents/Resources/exiftool/exiftool" "$APP/Contents/MacOS/Geotagger"
echo "Bundled ExifTool $EXIFTOOL_VERSION"

# Ad-hoc signature so Apple silicon will run it (it is not notarised).
codesign --force --deep --sign - "$APP"

(cd "$(dirname "$APP")" && ditto -c -k --keepParent "Geotagger.app" "$ZIP")
echo "Created $ZIP"
