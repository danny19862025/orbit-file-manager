using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using Svg.Skia;

namespace Orbit;

/// <summary>Turns SVG text, SVG/PNG files and Skia bitmaps into Avalonia bitmaps (safe on any thread).</summary>
public static class Render
{
    static readonly object svgGate = new();

    public static Bitmap Svg(string svg, int px, string tint = null)
    {
        if (string.IsNullOrEmpty(svg)) return null;
        if (tint != null) svg = svg.Replace("currentColor", tint);
        SKPicture pic;
        lock (svgGate)
        {
            var s = new SKSvg();
            pic = s.FromSvg(svg);
        }
        if (pic == null) return null;
        var (w, h) = ViewBox(svg, pic.CullRect);
        if (w <= 0.01 || h <= 0.01) return null;
        using var bmp = new SKBitmap(new SKImageInfo(px, px, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(SKColors.Transparent);
            float s = (float)(px / Math.Max(w, h));
            c.Translate((float)((px - w * s) / 2), (float)((px - h * s) / 2));
            c.Scale(s);
            c.ClipRect(new SKRect(0, 0, (float)w, (float)h));
            c.DrawPicture(pic);
        }
        pic.Dispose();
        return ToAvalonia(bmp);
    }

    public static Bitmap SvgFile(string file, int px)
    {
        string text;
        if (file.EndsWith(".svgz", StringComparison.OrdinalIgnoreCase))
        {
            using var fs = File.OpenRead(file);
            using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
            using var r = new StreamReader(gz);
            text = r.ReadToEnd();
        }
        else text = File.ReadAllText(file);
        return Svg(text, px);
    }

    /// <summary>An icon file of any supported kind (svg, svgz, png, xpm is skipped).</summary>
    public static Bitmap IconFile(string file, int px)
    {
        if (file == null) return null;
        try
        {
            if (file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".svgz", StringComparison.OrdinalIgnoreCase)) return SvgFile(file, px);
            using var fs = File.OpenRead(file);
            return Fit(fs, px);
        }
        catch { return null; }
    }

    /// <summary>Decodes an image so it fits in a px × px box (keeps the aspect ratio, never upscales much).</summary>
    public static Bitmap Fit(Stream s, int px)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        ms.Position = 0;
        using var codec = SKCodec.Create(ms);
        if (codec == null) return null;
        var info = codec.Info;
        ms.Position = 0;
        if (info.Width <= 0 || info.Height <= 0) return null;
        if (info.Width >= info.Height) return Bitmap.DecodeToWidth(ms, Math.Min(px, Math.Max(info.Width, 1)), BitmapInterpolationMode.HighQuality);
        return Bitmap.DecodeToHeight(ms, Math.Min(px, Math.Max(info.Height, 1)), BitmapInterpolationMode.HighQuality);
    }

    public static (int w, int h) Dimensions(string file)
    {
        try
        {
            using var codec = SKCodec.Create(file);
            return codec == null ? (0, 0) : (codec.Info.Width, codec.Info.Height);
        }
        catch { return (0, 0); }
    }

    public static Bitmap ToAvalonia(SKBitmap bmp)
    {
        var wb = new WriteableBitmap(new PixelSize(bmp.Width, bmp.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = wb.Lock())
        {
            var src = bmp.GetPixels();
            int rowBytes = bmp.Width * 4;
            unsafe
            {
                for (int y = 0; y < bmp.Height; y++)
                    Buffer.MemoryCopy((byte*)src + y * bmp.RowBytes, (byte*)fb.Address + y * fb.RowBytes, fb.RowBytes, rowBytes);
            }
        }
        return wb;
    }

    static (double, double) ViewBox(string svg, SKRect cull)
    {
        // The renderer draws at the width/height size when they're given (scaling the viewBox to fit), so frame to those first.
        var root = Regex.Match(svg, @"<svg\b[^>]*>").Value;
        var mw = Regex.Match(root, @"\swidth\s*=\s*[""']\s*([\d.]+)\s*(px|pt)?\s*[""']");
        var mh = Regex.Match(root, @"\sheight\s*=\s*[""']\s*([\d.]+)\s*(px|pt)?\s*[""']");
        if (mw.Success && mh.Success && double.TryParse(mw.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var aw)
            && double.TryParse(mh.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ah) && aw > 0 && ah > 0)
            return mw.Groups[2].Value == "pt" ? (aw * 4 / 3, ah * 4 / 3) : (aw, ah);
        var m = Regex.Match(svg, @"<svg[^>]*?viewBox\s*=\s*[""']\s*[-\d.]+[\s,]+[-\d.]+[\s,]+([\d.]+)[\s,]+([\d.]+)");
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
                      && double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && w > 0 && h > 0)
            return (w, h);
        return (Math.Max(1, cull.Right), Math.Max(1, cull.Bottom));
    }
}
