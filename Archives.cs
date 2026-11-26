using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace Orbit;

/// <summary>
/// Browse archives like folders. Locations look like  arc:C:\path\file.zip::folder/sub
/// Reading uses SharpCompress (ZIP, RAR, 7z, TAR, GZip/BZip2/XZ-compressed TAR).
/// </summary>
public static class Archives
{
    public const string Prefix = "arc:";
    const string Sep = "::";

    static readonly HashSet<string> Exts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".tgz", ".tbz2", ".txz", ".jar", ".nupkg", ".cbz", ".cbr", ".cb7", ".apk", ".vsix", ".whl", ".xpi"
    };

    public static bool IsArc(string p) => p != null && p.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>True for files Orbit can open as a folder.</summary>
    public static bool IsArchiveFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var lower = path.ToLowerInvariant();
        if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tar.bz2") || lower.EndsWith(".tar.xz")) return true;
        return Exts.Contains(Path.GetExtension(path));
    }

    public static bool IsIso(string path) =>
        path != null && (path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase));

    static bool IsCompressedTar(string archive)
    {
        var l = archive.ToLowerInvariant();
        return l.EndsWith(".tar.gz") || l.EndsWith(".tgz") || l.EndsWith(".tar.bz2") || l.EndsWith(".tbz2") || l.EndsWith(".tar.xz") || l.EndsWith(".txz");
    }

    public static string Make(string archive, string inner) => Prefix + archive + Sep + (inner ?? "").Replace('\\', '/').Trim('/');

    public static (string archive, string inner) Parse(string url)
    {
        var body = url.Substring(Prefix.Length);
        int i = body.IndexOf(Sep, StringComparison.Ordinal);
        return i < 0 ? (body, "") : (body[..i], body[(i + Sep.Length)..].Trim('/'));
    }

    public static string Combine(string url, string name)
    {
        var (a, inner) = Parse(url);
        return Make(a, inner.Length == 0 ? name : inner + "/" + name);
    }

    /// <summary>Parent location: the folder above inside the archive, or the folder holding the archive.</summary>
    public static string ParentOf(string url)
    {
        var (a, inner) = Parse(url);
        if (inner.Length == 0) return Path.GetDirectoryName(a);
        int i = inner.LastIndexOf('/');
        return Make(a, i < 0 ? "" : inner[..i]);
    }

    public static string NameOf(string url)
    {
        var (a, inner) = Parse(url);
        return inner.Length == 0 ? Path.GetFileName(a) : inner[(inner.LastIndexOf('/') + 1)..];
    }

    // ---------------------------------------------------------------------
    // Reading the table of contents (cached per archive, refreshed when the file changes)
    // ---------------------------------------------------------------------
    public sealed record Entry(string Path, bool IsDir, long Size, long Packed, DateTime Modified, bool Encrypted);

    static readonly Dictionary<string, (DateTime stamp, List<Entry> entries)> cache = new(StringComparer.OrdinalIgnoreCase);

    static string Clean(string key)
    {
        key = (key ?? "").Replace('\\', '/');
        while (key.StartsWith("./", StringComparison.Ordinal)) key = key[2..]; // tar often stores "./dir/file"
        key = key.Trim('/');
        return key == "." ? "" : key;
    }

    public static List<Entry> Entries(string archive)
    {
        var stamp = File.GetLastWriteTimeUtc(archive);
        lock (cache)
            if (cache.TryGetValue(archive, out var c) && c.stamp == stamp) return c.entries;

        var list = new List<Entry>();
        if (IsCompressedTar(archive))
        {
            using var reader = ReaderFactory.OpenReader(archive, new ReaderOptions());
            while (reader.MoveToNextEntry())
            {
                var e = reader.Entry;
                list.Add(new Entry(Clean(e.Key), e.IsDirectory, e.Size, e.CompressedSize, e.LastModifiedTime ?? default, e.IsEncrypted));
            }
        }
        else
        {
            using var arc = ArchiveFactory.OpenArchive(archive, new ReaderOptions());
            foreach (var e in arc.Entries)
                list.Add(new Entry(Clean(e.Key), e.IsDirectory, e.Size, e.CompressedSize, e.LastModifiedTime ?? default, e.IsEncrypted));
        }

        // Many archives only store files; add the folders implied by their paths.
        var known = new HashSet<string>(list.Where(x => x.IsDir).Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var e in list.ToList())
        {
            var p = e.Path;
            for (int i = p.LastIndexOf('/'); i > 0; i = p.LastIndexOf('/', i - 1))
            {
                var dir = p[..i];
                if (known.Add(dir)) list.Add(new Entry(dir, true, 0, 0, e.Modified, false));
            }
        }
        list.RemoveAll(x => x.Path.Length == 0);
        lock (cache) cache[archive] = (stamp, list);
        return list;
    }

    static string ParentPath(string p) { int i = p.LastIndexOf('/'); return i < 0 ? "" : p[..i]; }

    /// <summary>Items directly inside a folder of the archive.</summary>
    public static List<FileItem> List(string url)
    {
        var (a, inner) = Parse(url);
        var entries = Entries(a);
        var result = new List<FileItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (!string.Equals(ParentPath(e.Path), inner, StringComparison.OrdinalIgnoreCase) || !seen.Add(e.Path)) continue;
            var name = e.Path[(e.Path.LastIndexOf('/') + 1)..];
            long size = e.IsDir ? -1 : e.Size;
            result.Add(FileItem.Archived(Make(a, e.Path), name, e.IsDir, size, e.Packed, e.Modified, e.Encrypted));
        }
        return result;
    }

    public static (int files, long size, long packed) Totals(string url)
    {
        var (a, inner) = Parse(url);
        var prefix = inner.Length == 0 ? "" : inner + "/";
        int files = 0; long size = 0, packed = 0;
        foreach (var e in Entries(a))
            if (!e.IsDir && e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { files++; size += e.Size; packed += e.Packed; }
        return (files, size, packed);
    }

    // ---------------------------------------------------------------------
    // Extracting
    // ---------------------------------------------------------------------
    /// <summary>
    /// Extracts the given items (files or whole folders inside the archive) into <paramref name="destDir"/>.
    /// Folders keep their own name; files land directly in destDir. Returns the top-level paths created.
    /// </summary>
    public static List<string> Extract(string archive, IEnumerable<string> innerPaths, string destDir)
    {
        var wanted = innerPaths.Select(Clean).ToList();
        var dirs = new HashSet<string>(Entries(archive).Where(e => e.IsDir).Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        var destRoot = Path.GetFullPath(destDir);
        var created = new List<string>();
        foreach (var w in wanted)
            created.Add(Path.Combine(destRoot, SafeName(w.Length == 0 ? Path.GetFileNameWithoutExtension(archive) : w[(w.LastIndexOf('/') + 1)..])));

        string TargetFor(string key)
        {
            foreach (var w in wanted)
            {
                if (w.Length == 0) return key; // whole archive
                if (string.Equals(key, w, StringComparison.OrdinalIgnoreCase))
                    return w[(w.LastIndexOf('/') + 1)..];
                if (dirs.Contains(w) && key.StartsWith(w + "/", StringComparison.OrdinalIgnoreCase))
                    return key.Substring(ParentPath(w).Length).TrimStart('/');
            }
            return null;
        }

        void Write(string key, bool isDir, bool encrypted, Func<Stream> open)
        {
            var rel = TargetFor(key);
            if (rel == null) return;
            if (encrypted) throw new InvalidOperationException("This archive is password-protected, which Orbit can't open yet.");
            var target = Path.GetFullPath(Path.Combine(destRoot, string.Join(Path.DirectorySeparatorChar, rel.Split('/').Select(SafeName))));
            // never write outside the destination ("zip slip")
            if (!target.StartsWith(destRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && !string.Equals(target, destRoot, StringComparison.OrdinalIgnoreCase)) return;
            if (isDir) { Directory.CreateDirectory(target); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var src = open();
            using var dst = File.Create(target);
            src.CopyTo(dst);
        }

        if (IsCompressedTar(archive))
        {
            using var reader = ReaderFactory.OpenReader(archive, new ReaderOptions());
            while (reader.MoveToNextEntry())
            {
                var e = reader.Entry;
                Write(Clean(e.Key), e.IsDirectory, e.IsEncrypted, () => reader.OpenEntryStream());
            }
        }
        else
        {
            using var arc = ArchiveFactory.OpenArchive(archive, new ReaderOptions());
            foreach (var e in arc.Entries)
                Write(Clean(e.Key), e.IsDirectory, e.IsEncrypted, () => e.OpenEntryStream());
        }
        // folders implied only by file paths
        foreach (var w in wanted.Where(dirs.Contains)) Directory.CreateDirectory(Path.Combine(destRoot, SafeName(w[(w.LastIndexOf('/') + 1)..])));
        return created.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
    }

    /// <summary>Extract items to a fresh temp folder (for opening, dragging out, copying, previewing).</summary>
    public static List<string> ExtractToTemp(IEnumerable<string> urls)
    {
        var list = urls.ToList();
        var (a, _) = Parse(list[0]);
        var dir = Path.Combine(Path.GetTempPath(), "Orbit", "extract", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return Extract(a, list.Select(u => Parse(u).inner), dir);
    }

    static string SafeName(string name)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return name.Trim().Length == 0 ? "_" : name;
    }

    // ---------------------------------------------------------------------
    // Creating ZIPs
    // ---------------------------------------------------------------------
    public static string CompressToZip(IReadOnlyList<string> paths, string zipPath)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                var root = Path.GetFileName(p.TrimEnd('\\'));
                var baseDir = Path.GetDirectoryName(p.TrimEnd('\\'))!;
                bool any = false;
                foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                {
                    zip.CreateEntryFromFile(f, Path.GetRelativePath(baseDir, f).Replace('\\', '/'), CompressionLevel.Optimal);
                    any = true;
                }
                if (!any) zip.CreateEntry(root + "/");
            }
            else if (File.Exists(p))
                zip.CreateEntryFromFile(p, Path.GetFileName(p), CompressionLevel.Optimal);
        }
        return zipPath;
    }
}
