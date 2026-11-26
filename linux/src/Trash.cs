using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Orbit;

/// <summary>
/// The Trash, read and written the freedesktop.org way so it always agrees with Dolphin, Files and the desktop:
/// ~/.local/share/Trash for your home drive and "&lt;drive&gt;/.Trash-&lt;uid&gt;" on other drives. Each item is a pair —
/// "files/&lt;name&gt;" (the item itself) and "info/&lt;name&gt;.trashinfo" (where it came from, and when).
/// (Called RecycleBin so the rest of the code reads the same as the Windows app.)
/// </summary>
public static class RecycleBin
{
    public const string Path = "recycle:";

    public static bool Is(string p) => p == Path;

    static string HomeTrash => System.IO.Path.Combine(Xdg.Data, "Trash");
    static readonly string Uid = GetUid();

    static string GetUid()
    {
        try
        {
            foreach (var l in File.ReadLines("/proc/self/status"))
                if (l.StartsWith("Uid:")) return l.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)[1];
        }
        catch { }
        return "1000";
    }

    // ------------------------------------------------------------------
    // Mount points
    // ------------------------------------------------------------------
    public static List<(string Mount, string Device, string Fs)> Mounts()
    {
        var list = new List<(string, string, string)>();
        try
        {
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                var p = line.Split(' ');
                int dash = Array.IndexOf(p, "-");
                if (p.Length < 10 || dash < 0 || dash + 2 >= p.Length) continue;
                list.Add((Unescape(p[4]), Unescape(p[dash + 2]), p[dash + 1]));
            }
        }
        catch { }
        return list;
    }

    static string Unescape(string s) => s.Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");

    /// <summary>The mount point that holds a path (longest matching mount).</summary>
    public static string MountOf(string path)
    {
        string best = "/";
        foreach (var (m, _, _) in Mounts())
            if ((path == m || path.StartsWith(m.TrimEnd('/') + "/", StringComparison.Ordinal)) && m.Length > best.Length) best = m;
        return best;
    }

    /// <summary>Every trash folder that belongs to you: the home trash and one per mounted drive.</summary>
    public static IEnumerable<(string Dir, string Top)> BinFolders()
    {
        yield return (HomeTrash, null);
        var seen = new HashSet<string>(StringComparer.Ordinal) { HomeTrash };
        foreach (var (m, _, fs) in Mounts())
        {
            if (fs is "proc" or "sysfs" or "tmpfs" or "devtmpfs" or "cgroup2" or "overlay" or "squashfs" or "autofs" or "fuse.portal" or "fuse.gvfsd-fuse") continue;
            foreach (var cand in new[] { System.IO.Path.Combine(m, ".Trash", Uid), System.IO.Path.Combine(m, ".Trash-" + Uid) })
            {
                bool ok;
                try { ok = Directory.Exists(System.IO.Path.Combine(cand, "files")); } catch { ok = false; }
                if (ok && seen.Add(cand)) yield return (cand, m);
            }
        }
    }

    // ------------------------------------------------------------------
    // Listing
    // ------------------------------------------------------------------
    public static List<FileItem> List()
    {
        var list = new List<FileItem>();
        foreach (var (dir, top) in BinFolders())
        {
            var infoDir = System.IO.Path.Combine(dir, "info");
            IEnumerable<string> infos;
            try { infos = Directory.EnumerateFiles(infoDir, "*.trashinfo").ToList(); } catch { continue; }
            foreach (var info in infos)
            {
                try
                {
                    var (original, deleted) = ReadInfo(info, top);
                    if (string.IsNullOrEmpty(original)) continue;
                    var data = System.IO.Path.Combine(dir, "files", System.IO.Path.GetFileNameWithoutExtension(info));
                    bool isDir = Directory.Exists(data);
                    long size = 0;
                    if (!isDir)
                    {
                        var fi = new FileInfo(data);
                        if (!fi.Exists && fi.LinkTarget == null) continue;
                        try { size = fi.Length; } catch { }
                    }
                    list.Add(FileItem.Recycled(data, info, original, deleted, size, isDir));
                }
                catch { }
            }
        }
        return list;
    }

    static (string Original, DateTime Deleted) ReadInfo(string file, string top)
    {
        string path = null;
        DateTime when = default;
        foreach (var line in File.ReadLines(file))
        {
            if (line.StartsWith("Path=")) path = Uri.UnescapeDataString(line[5..]);
            else if (line.StartsWith("DeletionDate=")) DateTime.TryParse(line[13..], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out when);
        }
        if (path != null && !path.StartsWith('/') && top != null) path = System.IO.Path.Combine(top, path);
        return (path, when);
    }

    /// <summary>Number of items and total size, for menus.</summary>
    public static (long Count, long Bytes) Totals()
    {
        long count = 0, bytes = 0;
        foreach (var (dir, _) in BinFolders())
        {
            try
            {
                var files = System.IO.Path.Combine(dir, "files");
                foreach (var e in new DirectoryInfo(files).EnumerateFileSystemInfos())
                {
                    count++;
                    if (e is FileInfo fi) bytes += fi.Length;
                    else
                        try
                        {
                            foreach (var f in ((DirectoryInfo)e).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                                bytes += f.Length;
                        }
                        catch { }
                }
            }
            catch { }
        }
        return (count, bytes);
    }

    // ------------------------------------------------------------------
    // Moving items to the Trash
    // ------------------------------------------------------------------
    /// <summary>Moves one file or folder to the right trash for its drive. Throws when that isn't possible.</summary>
    public static void Trash(string path)
    {
        path = System.IO.Path.GetFullPath(path).TrimEnd('/');
        var mount = MountOf(path);
        string dir, recorded;
        if (MountOf(HomeTrash) == mount || MountOf(Xdg.Home) == mount)
        {
            dir = HomeTrash;
            recorded = path;
        }
        else
        {
            dir = TopTrash(mount);
            recorded = dir == HomeTrash ? path : System.IO.Path.GetRelativePath(mount, path);
        }
        var filesDir = System.IO.Path.Combine(dir, "files");
        var infoDir = System.IO.Path.Combine(dir, "info");
        Directory.CreateDirectory(filesDir);
        Directory.CreateDirectory(infoDir);

        var name = System.IO.Path.GetFileName(path);
        var ext = File.Exists(path) ? System.IO.Path.GetExtension(name) : "";
        var stem = ext.Length > 0 ? name[..^ext.Length] : name;
        for (int i = 1; ; i++)
        {
            var candidate = i == 1 ? name : $"{stem}.{i}{ext}";
            var info = System.IO.Path.Combine(infoDir, candidate + ".trashinfo");
            FileStream fs;
            try { fs = new FileStream(info, FileMode.CreateNew, FileAccess.Write); }
            catch (IOException) when (File.Exists(info)) { continue; }
            using (fs)
            using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
                w.Write($"[Trash Info]\nPath={EncodePath(recorded)}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
            var target = System.IO.Path.Combine(filesDir, candidate);
            if (File.Exists(target) || Directory.Exists(target)) { File.Delete(info); continue; }
            try
            {
                if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget == null) Directory.Move(path, target);
                else File.Move(path, target);
            }
            catch
            {
                File.Delete(info);
                throw;
            }
            return;
        }
    }

    /// <summary>"&lt;drive&gt;/.Trash/&lt;uid&gt;" when the drive has a shared trash, otherwise "&lt;drive&gt;/.Trash-&lt;uid&gt;".</summary>
    static string TopTrash(string mount)
    {
        var shared = System.IO.Path.Combine(mount, ".Trash");
        try
        {
            if (Directory.Exists(shared) && new DirectoryInfo(shared).LinkTarget == null &&
                (File.GetUnixFileMode(shared) & UnixFileMode.StickyBit) != 0)
            {
                var mine = System.IO.Path.Combine(shared, Uid);
                Directory.CreateDirectory(mine);
                return mine;
            }
        }
        catch { }
        var own = System.IO.Path.Combine(mount, ".Trash-" + Uid);
        try
        {
            if (!Directory.Exists(own)) Directory.CreateDirectory(own, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return own;
        }
        catch
        {
            return HomeTrash; // read-only drive or no permission: the caller's move will fail and say why
        }
    }

    static string EncodePath(string p)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(p))
        {
            char c = (char)b;
            if (b < 128 && (char.IsAsciiLetterOrDigit(c) || "-._~/!$&'()*+,;=:@".IndexOf(c) >= 0)) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // Restore / delete / empty
    // ------------------------------------------------------------------
    /// <summary>Puts items back where they were deleted from. Returns the restored paths and any errors.</summary>
    public static (List<string> Restored, List<string> Errors) Restore(IEnumerable<FileItem> items)
    {
        var restored = new List<string>();
        var errors = new List<string>();
        foreach (var f in items.Where(i => i.IsRecycled))
        {
            try
            {
                var target = Unique(f.OriginalPath);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                if (f.IsDir) Directory.Move(f.FullPath, target);
                else File.Move(f.FullPath, target);
                TryDelete(f.TrashInfo);
                restored.Add(target);
            }
            catch (IOException) when (f.IsDir)
            {
                // a different drive: copy across, then remove the trashed copy
                try
                {
                    var target = Unique(f.OriginalPath);
                    FileOps.CopyTree(f.FullPath, target);
                    Directory.Delete(f.FullPath, true);
                    TryDelete(f.TrashInfo);
                    restored.Add(target);
                }
                catch (Exception ex) { errors.Add($"{f.Name}: {ex.Message}"); }
            }
            catch (Exception ex) { errors.Add($"{f.Name}: {ex.Message}"); }
        }
        return (restored, errors);
    }

    /// <summary>Deletes items from the Trash for good.</summary>
    public static List<string> DeleteForever(IEnumerable<FileItem> items)
    {
        var errors = new List<string>();
        foreach (var f in items.Where(i => i.IsRecycled))
        {
            try
            {
                FileOps.DeletePermanently(f.FullPath);
                TryDelete(f.TrashInfo);
            }
            catch (Exception ex) { errors.Add($"{f.Name}: {ex.Message}"); }
        }
        return errors;
    }

    /// <summary>Empties every trash folder. The caller asks for confirmation first.</summary>
    public static List<string> Empty()
    {
        var errors = new List<string>();
        foreach (var (dir, _) in BinFolders())
        {
            foreach (var sub in new[] { "files", "info" })
            {
                var d = System.IO.Path.Combine(dir, sub);
                try
                {
                    foreach (var e in Directory.EnumerateFileSystemEntries(d))
                    {
                        try { FileOps.DeletePermanently(e); }
                        catch (Exception ex) { errors.Add($"{System.IO.Path.GetFileName(e)}: {ex.Message}"); }
                    }
                }
                catch { }
            }
            TryDelete(System.IO.Path.Combine(dir, "directorysizes"));
        }
        return errors;
    }

    static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var ext = Directory.Exists(path) ? "" : System.IO.Path.GetExtension(path);
        var name = System.IO.Path.GetFileName(path)[..^ext.Length];
        for (int i = 2; ; i++)
        {
            var p = System.IO.Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    static void TryDelete(string file) { try { if (file != null) File.Delete(file); } catch { } }

    /// <summary>Last resort for items the spec-based move can't handle: let GIO trash it.</summary>
    public static bool GioTrash(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("gio") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            psi.ArgumentList.Add("trash");
            psi.ArgumentList.Add(path);
            using var p = Process.Start(psi)!;
            p.WaitForExit(30000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
