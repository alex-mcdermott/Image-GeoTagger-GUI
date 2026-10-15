#:property PublishAot=false
#:package SkiaSharp@3.119.4
// Generates the app icon set. Run from the repo root:  dotnet run build/make-icon.cs
using SkiaSharp;

string outDir = Path.Combine("src", "Geotagger", "Assets");
Directory.CreateDirectory(outDir);

byte[] Render(int size)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var c = surface.Canvas;
    c.Clear(SKColors.Transparent);
    float s = size / 256f;

    // Rounded tile with a diagonal gradient.
    var tile = new SKRect(8 * s, 8 * s, 248 * s, 248 * s);
    using (var paint = new SKPaint { IsAntialias = true })
    {
        paint.Shader = SKShader.CreateLinearGradient(new SKPoint(tile.Left, tile.Top), new SKPoint(tile.Right, tile.Bottom),
            [new SKColor(0x0F, 0x6C, 0xBD), new SKColor(0x1A, 0xA0, 0x9A)], SKShaderTileMode.Clamp);
        c.DrawRoundRect(tile, 52 * s, 52 * s, paint);
    }

    // Faint "map" grid lines.
    using (var grid = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 38), StrokeWidth = Math.Max(1, 6 * s), Style = SKPaintStyle.Stroke })
    {
        c.Save();
        c.ClipRoundRect(new SKRoundRect(tile, 52 * s, 52 * s), antialias: true);
        using var path = new SKPath();
        path.MoveTo(8 * s, 170 * s); path.CubicTo(80 * s, 140 * s, 150 * s, 220 * s, 248 * s, 180 * s);
        path.MoveTo(60 * s, 8 * s); path.CubicTo(90 * s, 90 * s, 40 * s, 160 * s, 90 * s, 248 * s);
        c.DrawPath(path, grid);
        c.Restore();
    }

    // Pin with soft shadow.
    using var pin = new SKPath();
    float cx = 128 * s, cy = 104 * s, r = 62 * s, tipY = 214 * s;
    pin.MoveTo(cx, tipY);
    pin.CubicTo(cx - 14 * s, tipY - 40 * s, cx - r, cy + 46 * s, cx - r, cy);
    pin.ArcTo(new SKRect(cx - r, cy - r, cx + r, cy + r), 180, 180, false);
    pin.CubicTo(cx + r, cy + 46 * s, cx + 14 * s, tipY - 40 * s, cx, tipY);
    pin.Close();

    using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 70), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6 * s) })
    {
        c.Save(); c.Translate(0, 6 * s); c.DrawPath(pin, shadow); c.Restore();
    }
    using (var white = new SKPaint { IsAntialias = true, Color = SKColors.White })
        c.DrawPath(pin, white);

    // Lens/aperture in the pin head.
    using (var lens = new SKPaint { IsAntialias = true })
    {
        lens.Shader = SKShader.CreateLinearGradient(new SKPoint(cx - 30 * s, cy - 30 * s), new SKPoint(cx + 30 * s, cy + 30 * s),
            [new SKColor(0x0F, 0x6C, 0xBD), new SKColor(0x1A, 0xA0, 0x9A)], SKShaderTileMode.Clamp);
        c.DrawCircle(cx, cy, 30 * s, lens);
    }
    using (var glint = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 220) })
        c.DrawCircle(cx - 10 * s, cy - 10 * s, 8 * s, glint);

    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

int[] icoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var pngs = icoSizes.Select(Render).ToArray();

using (var fs = File.Create(Path.Combine(outDir, "Geotagger.ico")))
using (var w = new BinaryWriter(fs))
{
    w.Write((short)0); w.Write((short)1); w.Write((short)icoSizes.Length);
    int offset = 6 + 16 * icoSizes.Length;
    for (int i = 0; i < icoSizes.Length; i++)
    {
        int sz = icoSizes[i];
        w.Write((byte)(sz >= 256 ? 0 : sz)); w.Write((byte)(sz >= 256 ? 0 : sz));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((short)1); w.Write((short)32);
        w.Write(pngs[i].Length); w.Write(offset);
        offset += pngs[i].Length;
    }
    foreach (var p in pngs) w.Write(p);
}

File.WriteAllBytes(Path.Combine(outDir, "Geotagger-256.png"), Render(256));
File.WriteAllBytes(Path.Combine(outDir, "Geotagger-64.png"), Render(64));
// Source for the macOS .icns built in CI.
Directory.CreateDirectory(Path.Combine("build", "macos"));
File.WriteAllBytes(Path.Combine("build", "macos", "icon-1024.png"), Render(1024));
Console.WriteLine("Icons written to " + Path.GetFullPath(outDir));
