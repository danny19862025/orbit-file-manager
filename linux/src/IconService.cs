using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Orbit;

/// <summary>
/// Loads icons and thumbnails on worker threads, most-recently-requested first, so whatever is on screen right now
/// gets its icon first. Thumbnails come from the desktop's shared thumbnail cache (~/.cache/thumbnails) when there,
/// otherwise they're made here: pictures with Skia, videos and album art with ffmpeg, PDFs with pdftoppm.
/// </summary>
public static class IconService
{
    public static double Scale = 1.0;

    static readonly ConcurrentDictionary<string, IImage> cache = new();
    static readonly ConcurrentDictionary<string, string> typeNames = new(StringComparer.Ordinal);
    static readonly object gate = new();
    static readonly Dictionary<string, List<Action<IImage>>> waiters = new();
    static readonly List<Job> stack = new();
    static readonly SemaphoreSlim signal = new(0);

    sealed record Job(string Key, string Path, string Name, bool IsDir, int Px, int Mode);

    const int ModeIcon = 0, ModeThumb = 1, ModePack = 3;

    public static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".bmp", ".webp", ".ico", ".wbmp", ".heic", ".heif", ".avif", ".tif", ".tiff" };
    static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp", ".flv", ".ogv" };
    static readonly HashSet<string> CoverExts = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".flac", ".m4a", ".ogg", ".opus" };
    static readonly HashSet<string> ThumbExts = new(ImageExts.Concat(VideoExts).Concat(CoverExts).Concat(new[] { ".pdf", ".svg" }), StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> MediaExts = new(ImageExts.Concat(VideoExts), StringComparer.OrdinalIgnoreCase);

    public static void Init()
    {
        for (int i = 0; i < 3; i++)
            new Thread(Worker) { IsBackground = true, Name = "IconWorker" + i, Priority = ThreadPriority.BelowNormal }.Start();
    }

    static int Px(bool large) => (int)Math.Round((large ? 96 : 16) * Math.Max(1, Scale));

    public static IImage Get(FileItem f, bool large, Action<IImage> ready)
    {
        int px = Px(large);
        string key;
        int mode;
        if (UsePack(f, large))
        {
            var name = IconPack.Resolve(f.Name, f.IsDir);
            key = $"pack|{name}|{px}";
            if (cache.TryGetValue(key, out var packed)) return packed;
            return Enqueue(new Job(key, name, f.Name, f.IsDir, px, ModePack), ready);
        }
        if (large && !f.IsDir && !f.IsRemote && ThumbExts.Contains(f.Ext))
        {
            mode = ModeThumb;
            key = $"{px}|t|{f.FullPath}|{f.Modified.Ticks}";
        }
        else
        {
            mode = ModeIcon;
            var special = f.IsDir ? SpecialFolderIcon(f) : null;
            key = f.IsDir ? $"{px}|d|{special}" : $"{px}|e|{Mime.ForName(f.Name)}";
        }
        if (cache.TryGetValue(key, out var img)) return img;
        return Enqueue(new Job(key, f.FullPath, f.Name, f.IsDir, px, mode), ready);
    }

    static IImage Enqueue(Job job, Action<IImage> ready)
    {
        lock (gate)
        {
            if (waiters.TryGetValue(job.Key, out var list)) { list.Add(ready); return null; }
            waiters[job.Key] = new List<Action<IImage>> { ready };
            stack.Add(job);
        }
        signal.Release();
        return null;
    }

    // With an icon pack on: keep real thumbnails for photos/videos (large view) and the system icon for drive roots.
    static bool UsePack(FileItem f, bool large)
    {
        if (!IconPack.IsActive) return false;
        if (f.IsDir) return f.IsRemote || f.FullPath != "/";
        if (large && !f.IsRemote && AppSettings.Current.PackThumbnails && MediaExts.Contains(f.Ext)) return false;
        return true;
    }

    static string SpecialFolderIcon(FileItem f)
    {
        var p = f.FullPath;
        if (f.IsRemote) return "folder-remote";
        if (p == "/") return "drive-harddisk";
        if (p == Xdg.Home) return "user-home";
        if (p == Xdg.UserDir("DESKTOP", "Desktop")) return "user-desktop";
        if (p == Xdg.UserDir("DOCUMENTS", "Documents")) return "folder-documents";
        if (p == Xdg.UserDir("DOWNLOAD", "Downloads")) return "folder-download";
        if (p == Xdg.UserDir("MUSIC", "Music")) return "folder-music";
        if (p == Xdg.UserDir("PICTURES", "Pictures")) return "folder-pictures";
        if (p == Xdg.UserDir("VIDEOS", "Videos")) return "folder-videos";
        if (p == Xdg.UserDir("TEMPLATES", "Templates")) return "folder-templates";
        if (p == Xdg.UserDir("PUBLICSHARE", "Public")) return "folder-publicshare";
        return "folder";
    }

    /// <summary>The desktop icon for a MIME type or icon name (sidebar, Open with…).</summary>
    public static IImage Themed(IEnumerable<string> names, int px)
    {
        var list = names.ToList();
        var key = $"{px}|n|{string.Join(',', list)}";
        if (cache.TryGetValue(key, out var img)) return img;
        img = Render.IconFile(SystemIcons.Lookup(list, px), px);
        if (img != null) cache[key] = img;
        return img;
    }

    /// <summary>Forget every cached icon (after switching icon packs).</summary>
    public static void ClearAll()
    {
        CancelPending();
        cache.Clear();
        IconPack.ClearCache();
    }

    /// <summary>Drop queued work (e.g. after navigating away). Waiting items are told to retry if still on screen.</summary>
    public static void CancelPending()
    {
        var cancelled = new List<Action<IImage>>();
        lock (gate)
        {
            foreach (var j in stack)
                if (waiters.Remove(j.Key, out var ws)) cancelled.AddRange(ws);
            stack.Clear();
        }
        if (cancelled.Count > 0)
            Dispatcher.UIThread.Post(() => { foreach (var a in cancelled) a(null); }, DispatcherPriority.Background);
    }

    static void Worker()
    {
        while (true)
        {
            signal.Wait();
            Job job;
            lock (gate)
            {
                if (stack.Count == 0) continue;
                job = stack[^1];
                stack.RemoveAt(stack.Count - 1);
            }

            IImage img = null;
            try
            {
                img = job.Mode switch
                {
                    ModePack => IconPack.Render(job.Path, job.Px),
                    ModeThumb => Thumbnail(job.Path, job.Px) ?? SystemIcon(job, job.Px),
                    _ => SystemIcon(job, job.Px)
                };
            }
            catch { }

            if (img != null)
            {
                if (cache.Count > 6000) cache.Clear();
                cache[job.Key] = img;
            }

            List<Action<IImage>> ws;
            lock (gate) { waiters.Remove(job.Key, out ws); }
            if (img != null && ws != null)
                Dispatcher.UIThread.Post(() => { foreach (var a in ws) a(img); }, DispatcherPriority.Background);
        }
    }

    static IImage SystemIcon(Job job, int px)
    {
        IEnumerable<string> names;
        if (job.IsDir)
        {
            var special = job.Key.Split('|').Last();
            names = new[] { special, "folder", "inode-directory" };
        }
        else names = Mime.IconNames(Mime.ForName(job.Name));
        var img = Render.IconFile(SystemIcons.Lookup(names, px), px);
        // no system theme at all: fall back to Material so there is always something to see
        return img ?? IconPack.RenderFrom(IconPack.Material, IconPack.Resolve(job.Name, job.IsDir), px);
    }

    // ---------------------------------------------------------------------
    // Thumbnails
    // ---------------------------------------------------------------------
    static IImage Thumbnail(string path, int px)
    {
        var ext = Path.GetExtension(path);
        var cached = FromXdgCache(path, px);
        if (cached != null) return cached;
        try
        {
            if (ext.Equals(".svg", StringComparison.OrdinalIgnoreCase)) return Render.SvgFile(path, px);
            if (ImageExts.Contains(ext))
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var b = Render.Fit(fs, px);
                    if (b != null) return b;
                }
                catch { }
                return Ffmpeg(path, px, false); // HEIC, AVIF, TIFF variants Skia can't read
            }
            if (VideoExts.Contains(ext)) return Ffmpeg(path, px, true);
            if (CoverExts.Contains(ext)) return Ffmpeg(path, px, false);
            if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return Pdf(path, px);
        }
        catch { }
        return null;
    }

    /// <summary>The thumbnail the desktop (Dolphin, Nautilus…) already made, if it's still current.</summary>
    static IImage FromXdgCache(string path, int px)
    {
        try
        {
            var uri = FileUri(path);
            var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(uri))).ToLowerInvariant();
            long mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds();
            foreach (var size in px > 128 ? new[] { "x-large", "large", "xx-large" } : new[] { "large", "x-large", "normal" })
            {
                var f = Path.Combine(Xdg.Cache, "thumbnails", size, hash + ".png");
                if (!File.Exists(f)) continue;
                var bytes = File.ReadAllBytes(f);
                var stamp = PngText(bytes, "Thumb::MTime");
                if (stamp != null && long.TryParse(stamp, out var t) && t != mtime) continue; // stale
                return Render.Fit(new MemoryStream(bytes), px);
            }
        }
        catch { }
        return null;
    }

    /// <summary>A tEXt value from a PNG (the thumbnail spec stores the source's mtime there).</summary>
    static string PngText(byte[] b, string key)
    {
        int i = 8;
        while (i + 8 < b.Length)
        {
            int len = (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
            var type = Encoding.ASCII.GetString(b, i + 4, 4);
            if (type == "tEXt" && len > 0 && i + 8 + len <= b.Length)
            {
                var text = Encoding.Latin1.GetString(b, i + 8, len);
                int z = text.IndexOf('\0');
                if (z > 0 && text[..z] == key) return text[(z + 1)..];
            }
            if (type == "IDAT" || type == "IEND") break;
            i += 12 + len;
        }
        return null;
    }

    public static string FileUri(string path)
    {
        var sb = new StringBuilder("file://");
        foreach (var by in Encoding.UTF8.GetBytes(path))
        {
            char c = (char)by;
            if (by < 128 && (char.IsAsciiLetterOrDigit(c) || "-._~!$&'()*+,;=:@/".IndexOf(c) >= 0)) sb.Append(c);
            else sb.Append('%').Append(by.ToString("X2"));
        }
        return sb.ToString();
    }

    static string ffmpeg = Which("ffmpeg"), pdftoppm = Which("pdftoppm");

    public static string Which(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir, exe); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    static IImage Ffmpeg(string path, int px, bool video)
    {
        if (ffmpeg == null) return null;
        foreach (var seek in video ? new[] { "3", "0" } : new[] { (string)null })
        {
            var args = new List<string> { "-v", "error" };
            if (seek != null) { args.Add("-ss"); args.Add(seek); }
            args.AddRange(new[] { "-i", path, "-frames:v", "1", "-an", "-vf", $"scale='min({px},iw)':'min({px},ih)':force_original_aspect_ratio=decrease", "-f", "image2pipe", "-vcodec", "png", "-" });
            var bytes = RunBytes(ffmpeg, args, 8000);
            if (bytes is { Length: > 100 }) return Render.Fit(new MemoryStream(bytes), px);
        }
        return null;
    }

    static IImage Pdf(string path, int px)
    {
        if (pdftoppm == null) return null;
        var bytes = RunBytes(pdftoppm, new[] { "-png", "-singlefile", "-f", "1", "-l", "1", "-scale-to", px.ToString(), path }, 8000);
        return bytes is { Length: > 100 } ? Render.Fit(new MemoryStream(bytes), px) : null;
    }

    public static byte[] RunBytes(string exe, IEnumerable<string> args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardError.BaseStream.CopyToAsync(Stream.Null);
            using var ms = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return null; }
            copy.Wait(2000);
            return p.ExitCode == 0 ? ms.ToArray() : null;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------------
    // Type names ("PNG image", "Markdown document")
    // ---------------------------------------------------------------------
    public static string TypeName(string ext, bool dir, string name = null)
    {
        if (dir) return "Folder";
        var key = string.IsNullOrEmpty(ext) ? "name:" + (name ?? "") : ext;
        return typeNames.GetOrAdd(key, _ =>
        {
            try
            {
                var mime = Mime.ForName(name ?? ("file" + ext));
                if (mime != "application/octet-stream" && Mime.Comment(mime) is { Length: > 0 } c)
                    return char.ToUpper(c[0]) + c[1..];
            }
            catch { }
            return ext?.Length > 1 ? ext[1..].ToUpperInvariant() + " File" : "File";
        });
    }
}
