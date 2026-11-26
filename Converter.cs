using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Orbit;

/// <summary>Local file conversion: images with Windows' own codecs, video/audio through ffmpeg when installed.</summary>
public static class Converter
{
    public sealed record Format(string Id, string Label, string Ext, string Kind, string Note);

    public static readonly Format[] ImageFormats =
    {
        new("jpg", "JPG", ".jpg", "image", "Smallest for photos"),
        new("png", "PNG", ".png", "image", "Lossless, keeps transparency"),
        new("tiff", "TIFF", ".tiff", "image", "Lossless, for print"),
        new("bmp", "BMP", ".bmp", "image", "Uncompressed"),
        new("gif", "GIF", ".gif", "image", "256 colours"),
    };

    public static readonly Format[] VideoFormats =
    {
        new("mp4", "MP4", ".mp4", "video", "H.264 — plays everywhere"),
        new("webm", "WEBM", ".webm", "video", "VP9 — small, for the web"),
        new("gifv", "GIF", ".gif", "video", "Animated, 12 fps, 640 px"),
        new("mp3v", "MP3", ".mp3", "video", "Audio track only"),
    };

    public static readonly Format[] AudioFormats =
    {
        new("mp3", "MP3", ".mp3", "audio", "Small, plays everywhere"),
        new("m4a", "M4A", ".m4a", "audio", "AAC, Apple-friendly"),
        new("ogg", "OGG", ".ogg", "audio", "Vorbis, open format"),
        new("wav", "WAV", ".wav", "audio", "Uncompressed"),
    };

    static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif", ".avif", ".jxr", ".wdp", ".dds" };
    static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".mpg", ".mpeg", ".3gp", ".flv" };
    static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma", ".aiff" };

    public static string KindOf(string path)
    {
        var ext = Path.GetExtension(path);
        return ImageExts.Contains(ext) ? "image" : VideoExts.Contains(ext) ? "video" : AudioExts.Contains(ext) ? "audio" : null;
    }

    public static Format[] FormatsFor(string kind) => kind switch { "image" => ImageFormats, "video" => VideoFormats, "audio" => AudioFormats, _ => Array.Empty<Format>() };

    // ---------------------------------------------------------------------
    // Images
    // ---------------------------------------------------------------------
    public static byte[] EncodeImage(string src, Format fmt, int quality, int maxSide)
    {
        BitmapSource frame;
        using (var fs = File.OpenRead(src))
        {
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame = dec.Frames[0];
        }
        if (maxSide > 0 && Math.Max(frame.PixelWidth, frame.PixelHeight) > maxSide)
        {
            double s = (double)maxSide / Math.Max(frame.PixelWidth, frame.PixelHeight);
            frame = new TransformedBitmap(frame, new ScaleTransform(s, s));
        }
        // formats without transparency: put the picture on white instead of black
        bool hasAlpha = frame.Format == PixelFormats.Bgra32 || frame.Format == PixelFormats.Pbgra32 || frame.Format == PixelFormats.Prgba64 || frame.Format == PixelFormats.Rgba64;
        if (hasAlpha && fmt.Id is "jpg" or "bmp")
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var rect = new Rect(0, 0, frame.PixelWidth, frame.PixelHeight);
                dc.DrawRectangle(Brushes.White, null, rect);
                dc.DrawImage(frame, rect);
            }
            var rtb = new RenderTargetBitmap(frame.PixelWidth, frame.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            frame = new FormatConvertedBitmap(rtb, PixelFormats.Bgr24, null, 0);
        }
        BitmapEncoder enc = fmt.Id switch
        {
            "jpg" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 10, 100) },
            "png" => new PngBitmapEncoder(),
            "tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Zip },
            "bmp" => new BmpBitmapEncoder(),
            "gif" => new GifBitmapEncoder(),
            _ => throw new NotSupportedException(fmt.Label)
        };
        enc.Frames.Add(BitmapFrame.Create(frame));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    // ---------------------------------------------------------------------
    // Video / audio through ffmpeg
    // ---------------------------------------------------------------------
    public static string FindFfmpeg()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir.Trim(), "ffmpeg.exe"); if (File.Exists(p)) return p; } catch { }
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var p in new[]
                 {
                     Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", "ffmpeg.exe"),
                     @"C:\ProgramData\chocolatey\bin\ffmpeg.exe", @"C:\ffmpeg\bin\ffmpeg.exe"
                 })
            if (File.Exists(p)) return p;
        try
        {
            var pk = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(pk))
                foreach (var d in Directory.EnumerateDirectories(pk, "*FFmpeg*"))
                    foreach (var f in Directory.EnumerateFiles(d, "ffmpeg.exe", SearchOption.AllDirectories)) return f;
        }
        catch { }
        return null;
    }

    static string FfmpegArgs(Format f) => f.Id switch
    {
        "mp4" => "-c:v libx264 -preset medium -crf 23 -pix_fmt yuv420p -c:a aac -b:a 160k -movflags +faststart",
        "webm" => "-c:v libvpx-vp9 -crf 33 -b:v 0 -row-mt 1 -c:a libopus -b:a 128k",
        "gifv" => "-vf \"fps=12,scale=640:-1:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse\" -an",
        "mp3v" or "mp3" => "-vn -c:a libmp3lame -q:a 2",
        "m4a" => "-vn -c:a aac -b:a 192k",
        "ogg" => "-vn -c:a libvorbis -q:a 5",
        "wav" => "-vn -c:a pcm_s16le",
        _ => throw new NotSupportedException(f.Label)
    };

    public static async Task<string> RunFfmpegAsync(string ffmpeg, string src, string dest, Format f)
    {
        var psi = new ProcessStartInfo(ffmpeg, $"-hide_banner -loglevel error -y -i \"{src}\" {FfmpegArgs(f)} \"{dest}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            try { File.Delete(dest); } catch { }
            return err.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? $"ffmpeg exited with code {p.ExitCode}";
        }
        return null;
    }

    public static string OutputPath(string src, string ext)
    {
        var dir = Path.GetDirectoryName(src)!;
        var name = Path.GetFileNameWithoutExtension(src);
        var dest = Path.Combine(dir, name + ext);
        if (string.Equals(dest, src, StringComparison.OrdinalIgnoreCase) || File.Exists(dest))
            for (int i = 2; ; i++)
            {
                dest = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(dest)) break;
            }
        return dest;
    }
}
