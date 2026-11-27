using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SkiaSharp;

namespace Orbit;

/// <summary>Local file conversion: pictures with Skia (plus ImageMagick or ffmpeg for TIFF / BMP / GIF), video and audio with ffmpeg.</summary>
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
        { ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif", ".avif" };
    static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".mpg", ".mpeg", ".3gp", ".flv", ".ogv" };
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
    static SKBitmap Decode(string src)
    {
        var bmp = SKBitmap.Decode(src);
        if (bmp != null) return bmp;
        // formats Skia can't read (HEIC, AVIF, some TIFFs): let ffmpeg turn them into PNG first
        var ff = FindFfmpeg();
        if (ff != null)
        {
            var png = IconService.RunBytes(ff, new[] { "-v", "error", "-i", src, "-frames:v", "1", "-f", "image2pipe", "-vcodec", "png", "-" }, 30000);
            if (png is { Length: > 0 }) return SKBitmap.Decode(png);
        }
        throw new InvalidDataException("This picture's format can't be read here.");
    }

    public static byte[] EncodeImage(string src, Format fmt, int quality, int maxSide)
    {
        using var original = Decode(src);
        SKBitmap frame = original;
        if (maxSide > 0 && Math.Max(original.Width, original.Height) > maxSide)
        {
            double s = (double)maxSide / Math.Max(original.Width, original.Height);
            frame = original.Resize(new SKImageInfo((int)Math.Round(original.Width * s), (int)Math.Round(original.Height * s)), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        try
        {
            // formats without transparency: put the picture on white instead of black
            if (fmt.Id is "jpg" or "bmp" && frame.AlphaType != SKAlphaType.Opaque)
            {
                var flat = new SKBitmap(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                using (var c = new SKCanvas(flat)) { c.Clear(SKColors.White); c.DrawBitmap(frame, 0, 0); }
                if (!ReferenceEquals(frame, original)) frame.Dispose();
                frame = flat;
            }
            using var image = SKImage.FromBitmap(frame);
            switch (fmt.Id)
            {
                case "jpg": return image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 10, 100)).ToArray();
                case "png": return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
                case "tiff" or "bmp" or "gif":
                    var png = image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
                    return Transcode(png, fmt.Id) ?? throw new NotSupportedException($"{fmt.Label} needs ImageMagick or ffmpeg — install one of them to convert to {fmt.Label}.");
                default: throw new NotSupportedException(fmt.Label);
            }
        }
        finally
        {
            if (!ReferenceEquals(frame, original)) frame.Dispose();
        }
    }

    /// <summary>PNG bytes → TIFF / BMP / GIF through ImageMagick (preferred) or ffmpeg.</summary>
    static byte[] Transcode(byte[] png, string id)
    {
        var magick = IconService.Which("magick") ?? IconService.Which("convert");
        if (magick != null)
        {
            var args = id == "tiff" ? new[] { "png:-", "-compress", "zip", "tiff:-" } : new[] { "png:-", id + ":-" };
            var r = Pipe(magick, args, png);
            if (r is { Length: > 0 }) return r;
        }
        var ff = FindFfmpeg();
        if (ff != null)
        {
            var codec = id switch { "tiff" => "tiff", "bmp" => "bmp", _ => "gif" };
            var extra = id == "gif" ? new[] { "-vf", "split[a][b];[a]palettegen[p];[b][p]paletteuse" } : Array.Empty<string>();
            var args = new[] { "-v", "error", "-f", "png_pipe", "-i", "-" }.Concat(extra).Concat(new[] { "-frames:v", "1", "-c:v", codec, "-f", "image2pipe", "-" });
            var r = Pipe(ff, args, png);
            if (r is { Length: > 0 }) return r;
        }
        return null;
    }

    static byte[] Pipe(string exe, IEnumerable<string> args, byte[] input)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardError.BaseStream.CopyToAsync(Stream.Null);
            using var ms = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
            p.StandardInput.BaseStream.Write(input);
            p.StandardInput.Close();
            if (!p.WaitForExit(60000)) { try { p.Kill(true); } catch { } return null; }
            copy.Wait(5000);
            return p.ExitCode == 0 ? ms.ToArray() : null;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------------
    // Video / audio through ffmpeg
    // ---------------------------------------------------------------------
    public static string FindFfmpeg() => IconService.Which("ffmpeg");

    static string[] FfmpegArgs(Format f) => f.Id switch
    {
        "mp4" => new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "23", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart" },
        "webm" => new[] { "-c:v", "libvpx-vp9", "-crf", "33", "-b:v", "0", "-row-mt", "1", "-c:a", "libopus", "-b:a", "128k" },
        "gifv" => new[] { "-vf", "fps=12,scale=640:-1:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse", "-an" },
        "mp3v" or "mp3" => new[] { "-vn", "-c:a", "libmp3lame", "-q:a", "2" },
        "m4a" => new[] { "-vn", "-c:a", "aac", "-b:a", "192k" },
        "ogg" => new[] { "-vn", "-c:a", "libvorbis", "-q:a", "5" },
        "wav" => new[] { "-vn", "-c:a", "pcm_s16le" },
        _ => throw new NotSupportedException(f.Label)
    };

    public static async Task<string> RunFfmpegAsync(string ffmpeg, string src, string dest, Format f)
    {
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", src }.Concat(FfmpegArgs(f)).Append(dest)) psi.ArgumentList.Add(a);
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
        if (string.Equals(dest, src, StringComparison.Ordinal) || File.Exists(dest))
            for (int i = 2; ; i++)
            {
                dest = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(dest)) break;
            }
        return dest;
    }
}
