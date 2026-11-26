using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Orbit;

/// <summary>
/// The Recycle Bin, read the same way Explorer reads it: every drive has "$Recycle.Bin\&lt;your SID&gt;", where each
/// deleted item is a pair — "$Rxxxxxx.ext" (the item itself) and "$Ixxxxxx.ext" (where it came from, when, how big).
/// Restoring moves $R back to its original place and removes $I, so Explorer stays in sync.
/// </summary>
public static class RecycleBin
{
    public const string Path = "recycle:";

    public static bool Is(string p) => p == Path;

    public static List<FileItem> List()
    {
        var list = new List<FileItem>();
        foreach (var dir in BinFolders())
        {
            IEnumerable<string> infos;
            try { infos = Directory.EnumerateFiles(dir, "$I*").ToList(); } catch { continue; }
            foreach (var info in infos)
            {
                try
                {
                    var (original, deleted, size) = ReadInfo(info);
                    if (string.IsNullOrEmpty(original)) continue;
                    var r = DataPathFor(info);
                    bool isDir = Directory.Exists(r);
                    if (!isDir && !File.Exists(r)) continue;
                    list.Add(FileItem.Recycled(r, original, deleted, size, isDir));
                }
                catch { }
            }
        }
        return list;
    }

    /// <summary>Number of items and total size, for the sidebar.</summary>
    public static (long Count, long Bytes) Totals()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64NumItems, info.i64Size) : (0, 0);
    }

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
                TryDelete(InfoPathFor(f.FullPath));
                restored.Add(target);
            }
            catch (Exception ex) { errors.Add($"{f.Name}: {ex.Message}"); }
        }
        return (restored, errors);
    }

    /// <summary>Deletes items from the Recycle Bin for good.</summary>
    public static List<string> DeleteForever(IEnumerable<FileItem> items)
    {
        var errors = new List<string>();
        foreach (var f in items.Where(i => i.IsRecycled))
        {
            try
            {
                if (f.IsDir)
                {
                    foreach (var file in Directory.EnumerateFiles(f.FullPath, "*", SearchOption.AllDirectories))
                        File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(f.FullPath, true);
                }
                else
                {
                    File.SetAttributes(f.FullPath, FileAttributes.Normal);
                    File.Delete(f.FullPath);
                }
                TryDelete(InfoPathFor(f.FullPath));
            }
            catch (Exception ex) { errors.Add($"{f.Name}: {ex.Message}"); }
        }
        return errors;
    }

    /// <summary>Empties the whole Recycle Bin. Windows asks for confirmation and shows progress.</summary>
    public static void Empty(IntPtr owner) => SHEmptyRecycleBin(owner, null, 0);

    /// <summary>The bin folders that belong to the current user, one per drive.</summary>
    public static IEnumerable<string> BinFolders()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid == null) yield break;
        foreach (var d in DriveInfo.GetDrives())
        {
            bool ok;
            try { ok = d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable; } catch { ok = false; }
            if (!ok) continue;
            var dir = System.IO.Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin", sid);
            bool exists;
            try { exists = Directory.Exists(dir); } catch { exists = false; }
            if (exists) yield return dir;
        }
    }

    static string DataPathFor(string infoPath) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(infoPath)!, "$R" + System.IO.Path.GetFileName(infoPath)[2..]);

    static string InfoPathFor(string dataPath) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dataPath)!, "$I" + System.IO.Path.GetFileName(dataPath)[2..]);

    /// <summary>$I format: version (1 = Vista–8, 2 = Windows 10+), size, deletion time, then the original path.</summary>
    static (string Original, DateTime Deleted, long Size) ReadInfo(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 24) return (null, default, 0);
        long version = BitConverter.ToInt64(b, 0);
        long size = BitConverter.ToInt64(b, 8);
        var deleted = DateTime.FromFileTime(BitConverter.ToInt64(b, 16));
        string original;
        if (version >= 2 && b.Length >= 28)
        {
            int chars = BitConverter.ToInt32(b, 24);
            original = Encoding.Unicode.GetString(b, 28, Math.Min(chars * 2, b.Length - 28));
        }
        else original = Encoding.Unicode.GetString(b, 24, Math.Min(520, b.Length - 24));
        return (original.TrimEnd('\0'), deleted, size);
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

    static void TryDelete(string file) { try { File.Delete(file); } catch { } }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);
}
