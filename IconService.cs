using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;

namespace Orbit;

/// <summary>
/// Loads shell icons and thumbnails on dedicated STA worker threads, most-recently-requested first,
/// so whatever is on screen right now gets its icon first. Results are cached and shared.
/// </summary>
public static class IconService
{
    public static double Scale = 1.0;

    static Dispatcher ui;
    static readonly ConcurrentDictionary<string, ImageSource> cache = new();
    static readonly ConcurrentDictionary<string, string> typeNames = new(StringComparer.OrdinalIgnoreCase);
    static readonly object gate = new();
    static readonly Dictionary<string, List<Action<ImageSource>>> waiters = new();
    static readonly List<Job> stack = new();
    static readonly SemaphoreSlim signal = new(0);

    sealed record Job(string Key, string Path, int Px, int Mode);

    const int ModeIcon = 0, ModeThumb = 1, ModeThumbCached = 2, ModePack = 3;

    static readonly HashSet<string> ThumbExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".avif", ".tif", ".tiff", ".ico",
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp",
        ".pdf", ".psd", ".svg", ".3mf", ".stl", ".mp3", ".flac", ".m4a"
    };

    static readonly HashSet<string> UniqueExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".url", ".ico", ".cur", ".ani", ".appref-ms", ".msc", ".scr", ".website"
    };

    public static void Init(Dispatcher dispatcher)
    {
        ui = dispatcher;
        for (int i = 0; i < 3; i++)
        {
            var t = new Thread(Worker) { IsBackground = true, Name = "IconWorker" + i, Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
    }

    public static ImageSource Get(FileItem f, bool large, Action<ImageSource> ready)
    {
        int px = (int)Math.Round((large ? 64 : 16) * Scale);
        string key;
        int mode;
        if (UsePack(f, large))
        {
            var name = IconPack.Resolve(f.Name, f.IsDir);
            key = "pack|" + name;
            if (cache.TryGetValue(key, out var packed)) return packed;
            return Enqueue(key, name, px, ModePack, ready);
        }
        if (f.IsRemote)
        {
            // Remote items have no local file to ask the shell about, so use a stand-in with the same extension.
            var stub = Stub(f.IsDir ? "" : f.Ext, f.IsDir);
            var proxy = stub == null ? null : FileItem.TryFromPath(stub);
            if (proxy == null) return null;
            return Get(proxy, large, ready);
        }
        if (f.IsDir)
        {
            mode = ModeIcon;
            bool custom = (f.Attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0 || f.FullPath.Length <= 3;
            key = custom ? $"{px}|p|{f.FullPath}" : $"{px}|dir";
        }
        else if (large && ThumbExts.Contains(f.Ext))
        {
            // Cloud placeholders (OneDrive "online-only") only use the thumbnail cache so we never trigger a download.
            mode = f.IsCloud ? ModeThumbCached : ModeThumb;
            key = $"{px}|t|{f.FullPath}|{f.Modified.Ticks}";
        }
        else if (UniqueExts.Contains(f.Ext) && !f.IsCloud)
        {
            mode = ModeIcon;
            key = $"{px}|p|{f.FullPath}";
        }
        else
        {
            mode = ModeIcon;
            key = $"{px}|e|{f.Ext}";
        }

        if (cache.TryGetValue(key, out var img)) return img;
        return Enqueue(key, f.FullPath, px, mode, ready);
    }

    static ImageSource Enqueue(string key, string path, int px, int mode, Action<ImageSource> ready)
    {
        lock (gate)
        {
            if (waiters.TryGetValue(key, out var list)) { list.Add(ready); return null; }
            waiters[key] = new List<Action<ImageSource>> { ready };
            stack.Add(new Job(key, path, px, mode));
        }
        signal.Release();
        return null;
    }

    // With an icon pack on: keep real thumbnails for photos/videos (large view), app icons for programs and
    // shortcuts, and the system icon for drive roots. Everything else uses the pack.
    static readonly HashSet<string> KeepSystemExts = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".lnk", ".url", ".appref-ms", ".scr", ".msc" };
    static readonly HashSet<string> MediaExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".avif", ".tif", ".tiff",
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v"
    };

    static bool UsePack(FileItem f, bool large)
    {
        if (!IconPack.IsActive) return false;
        if (f.IsDir) return f.IsRemote || f.FullPath.Length > 3;
        if (!f.IsRemote && KeepSystemExts.Contains(f.Ext)) return false;
        if (large && !f.IsRemote && AppSettings.Current.PackThumbnails && MediaExts.Contains(f.Ext)) return false;
        return true;
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
        var cancelled = new List<Action<ImageSource>>();
        lock (gate)
        {
            foreach (var j in stack)
                if (waiters.Remove(j.Key, out var ws)) cancelled.AddRange(ws);
            stack.Clear();
        }
        if (cancelled.Count > 0)
            ui.BeginInvoke(DispatcherPriority.Background, () => { foreach (var a in cancelled) a(null); });
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

            ImageSource img = null;
            try
            {
                img = job.Mode switch
                {
                    ModeThumb => Native.GetShellImage(job.Path, job.Px, Native.SIIGBF_RESIZETOFIT)
                                 ?? Native.GetShellImage(job.Path, job.Px, Native.SIIGBF_ICONONLY),
                    ModeThumbCached => Native.GetShellImage(job.Path, job.Px, Native.SIIGBF_INCACHEONLY)
                                       ?? Native.GetShellImage(job.Path, job.Px, Native.SIIGBF_ICONONLY),
                    ModePack => IconPack.Render(job.Path),
                    _ => Native.GetShellImage(job.Path, job.Px, Native.SIIGBF_ICONONLY)
                };
            }
            catch { }

            if (img != null)
            {
                if (cache.Count > 6000) cache.Clear();
                cache[job.Key] = img;
            }

            List<Action<ImageSource>> ws;
            lock (gate) { waiters.Remove(job.Key, out ws); }
            if (img != null && ws != null)
                ui.BeginInvoke(DispatcherPriority.Background, () => { foreach (var a in ws) a(img); });
        }
    }

    static readonly ConcurrentDictionary<string, string> stubs = new(StringComparer.OrdinalIgnoreCase);

    static string Stub(string ext, bool dir)
    {
        return stubs.GetOrAdd(dir ? "<dir>" : ext, _ =>
        {
            try
            {
                var root = Path.Combine(Path.GetTempPath(), "Orbit", "stubs");
                Directory.CreateDirectory(root);
                if (dir)
                {
                    var d = Path.Combine(root, "folder");
                    Directory.CreateDirectory(d);
                    return d;
                }
                // ".exe"/".lnk" stubs would just show a broken icon, so use the generic file icon for those
                var safe = ext.Length > 1 && ext.Length < 16 && ext.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                           && !UniqueExts.Contains(ext) ? ext : "";
                var p = Path.Combine(root, "file" + safe);
                if (!File.Exists(p)) using (File.Create(p)) { }
                return p;
            }
            catch { return null; }
        });
    }

    public static string TypeName(string ext, bool dir)
    {
        if (dir) return "File folder";
        return typeNames.GetOrAdd(ext ?? "", e =>
        {
            try
            {
                var info = new Native.SHFILEINFO();
                Native.SHGetFileInfo("file" + e, 0x80, ref info, (uint)Marshal.SizeOf<Native.SHFILEINFO>(), 0x400 | 0x10);
                if (!string.IsNullOrEmpty(info.szTypeName)) return info.szTypeName;
            }
            catch { }
            return e.Length > 1 ? e[1..].ToUpperInvariant() + " File" : "File";
        });
    }
}
