using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Orbit;

/// <summary>
/// In-memory index of every file and folder on the local drives (the system's own trees such as /usr and /proc are skipped) (names + parent links, so it stays compact).
/// Append-only chunked storage lets searches run while indexing is still in progress.
/// </summary>
public sealed class SearchIndex
{
    const int Shift = 16, ChunkSize = 1 << Shift, Mask = ChunkSize - 1;
    public const byte FlagDir = 1, FlagHidden = 2;

    internal sealed class Chunk
    {
        public readonly string[] Names = new string[ChunkSize];
        public readonly int[] Parents = new int[ChunkSize];
        public readonly byte[] Flags = new byte[ChunkSize];
    }

    readonly List<Chunk> chunks = new();
    Chunk[] snapshot = Array.Empty<Chunk>();
    int count;

    public int Count => Volatile.Read(ref count);
    public DateTime BuiltUtc { get; set; }

    public int Add(string name, int parent, byte flags)
    {
        int i = count;
        int c = i >> Shift;
        if (c == chunks.Count)
        {
            lock (chunks)
            {
                chunks.Add(new Chunk());
                snapshot = chunks.ToArray();
            }
        }
        var ch = chunks[c];
        int o = i & Mask;
        ch.Names[o] = name;
        ch.Parents[o] = parent;
        ch.Flags[o] = flags;
        Volatile.Write(ref count, i + 1);
        return i;
    }

    /// <summary>Reader view: chunks + count, stable for the duration of one search.</summary>
    internal (Chunk[] chunks, int count) View()
    {
        int n = Count;
        Chunk[] snap;
        lock (chunks) snap = snapshot;
        return (snap, n);
    }

    public string Name(int i) { var s = View().chunks; return s[i >> Shift].Names[i & Mask]; }
    public int Parent(int i) { var s = View().chunks; return s[i >> Shift].Parents[i & Mask]; }
    public byte Flags(int i) { var s = View().chunks; return s[i >> Shift].Flags[i & Mask]; }

    public string PathOf(int i)
    {
        var parts = new Stack<string>();
        var s = View().chunks;
        while (i >= 0)
        {
            parts.Push(s[i >> Shift].Names[i & Mask]);
            i = s[i >> Shift].Parents[i & Mask];
        }
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (sb.Length > 0 && sb[^1] != '/') sb.Append('/');
            sb.Append(p);
        }
        return sb.ToString();
    }

    // Matching needs raw access to the arrays for speed.
    internal static string NameAt(Chunk[] s, int i) => s[i >> Shift].Names[i & Mask];
    internal static int ParentAt(Chunk[] s, int i) => s[i >> Shift].Parents[i & Mask];
    internal static byte FlagsAt(Chunk[] s, int i) => s[i >> Shift].Flags[i & Mask];

    // ---------------------------------------------------------------------
    // Building
    // ---------------------------------------------------------------------
    static readonly HashSet<string> SkipDirNames = new(StringComparer.Ordinal)
    {
        "node_modules", ".git", "__pycache__", ".vs", ".cache", ".npm", ".cargo", ".rustup", ".nuget", ".gradle", ".m2",
        ".Trash", "lost+found", ".snapshots", ".var", ".steam", ".wine", ".mozilla", ".thunderbird", ".pki", ".dotnet"
    };

    /// <summary>System trees at the top of / that aren't worth searching (like Windows' own folder on Windows).</summary>
    static readonly HashSet<string> SkipRoots = new(StringComparer.Ordinal)
    {
        "/proc", "/sys", "/dev", "/run", "/tmp", "/var", "/usr", "/boot", "/efi", "/etc", "/lib", "/lib32", "/lib64", "/libx32",
        "/bin", "/sbin", "/snap", "/nix", "/lost+found", "/swap", "/.snapshots"
    };

    static readonly string TrashDir = Path.Combine(Xdg.Data, "Trash");

    public static bool IsSkippedPath(string path)
    {
        foreach (var r in SkipRoots)
            if (path == r || path.StartsWith(r + "/", StringComparison.Ordinal))
                return !path.StartsWith("/run/media/", StringComparison.Ordinal);
        if (path.StartsWith(TrashDir, StringComparison.Ordinal)) return true;
        foreach (var part in path.Split('/'))
            if (SkipDirNames.Contains(part) || part.StartsWith(".Trash-", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>"/" plus removable and other drives under /run/media, which the walk of / doesn't reach.</summary>
    public static IEnumerable<string> IndexedRoots()
    {
        yield return "/";
        foreach (var d in Drives.List())
            if (!d.Network && d.Mount.StartsWith("/run/media/", StringComparison.Ordinal)) yield return d.Mount;
    }

    /// <summary>Mount points the walk must not enter: network shares and pseudo file systems.</summary>
    static HashSet<string> NoEnter()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (m, _, fs) in RecycleBin.Mounts())
            if (fs is "nfs" or "nfs4" or "cifs" or "smb3" or "fuse.sshfs" or "fuse.rclone" or "fuse.gvfsd-fuse" or "autofs" or "fuse.portal" or "proc" or "sysfs" or "overlay" or "squashfs")
                set.Add(m);
        return set;
    }

    /// <summary>Walks every local drive. Runs on a background thread; the index is searchable as it grows.</summary>
    public void Build(CancellationToken ct, Action<int> progress = null)
    {
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
            BufferSize = 64 * 1024
        };
        var noEnter = NoEnter();
        var sw = Stopwatch.StartNew();
        foreach (var root in IndexedRoots())
        {
            int rootIdx = Add(root, -1, FlagDir);
            var stack = new Stack<(string path, int idx)>();
            stack.Push((root, rootIdx));
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (dir, idx) = stack.Pop();
                IEnumerable<(string name, bool isDir, bool link)> entries;
                try
                {
                    entries = new FileSystemEnumerable<(string, bool, bool)>(dir,
                        (ref FileSystemEntry e) => (e.FileName.ToString(), e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0), opts);
                }
                catch { continue; }
                try
                {
                    foreach (var (name, isDir, link) in entries)
                    {
                        byte flags = (byte)((isDir ? FlagDir : 0) | (name.StartsWith('.') ? FlagHidden : 0));
                        if (isDir && (SkipDirNames.Contains(name) || name.StartsWith(".Trash-", StringComparison.Ordinal))) continue;
                        var full = dir == "/" ? "/" + name : dir + "/" + name;
                        if (isDir && (SkipRoots.Contains(full) || noEnter.Contains(full) || full == TrashDir)) continue;
                        int child = Add(name, idx, flags);
                        if (!isDir || link) continue; // symlinks loop or double count
                        stack.Push((full, child));
                    }
                }
                catch { }
                if (sw.ElapsedMilliseconds > 500) { progress?.Invoke(Count); sw.Restart(); }
            }
        }
        BuiltUtc = DateTime.UtcNow;
        progress?.Invoke(Count);
    }

    // ---------------------------------------------------------------------
    // Cache on disk
    // ---------------------------------------------------------------------
    const int Magic = 0x4F524258; // "ORBX"
    const int FormatVersion = 1;

    public void Save(string file)
    {
        var (s, n) = View();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        using (var w = new BinaryWriter(fs, Encoding.UTF8))
        {
            w.Write(Magic);
            w.Write(FormatVersion);
            w.Write(BuiltUtc.Ticks);
            w.Write(n);
            for (int i = 0; i < n; i++)
            {
                w.Write(NameAt(s, i));
                w.Write(ParentAt(s, i));
                w.Write(FlagsAt(s, i));
            }
        }
        File.Move(tmp, file, true);
    }

    public static SearchIndex Load(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var r = new BinaryReader(fs, Encoding.UTF8);
        if (r.ReadInt32() != Magic || r.ReadInt32() != FormatVersion) return null;
        var idx = new SearchIndex { BuiltUtc = new DateTime(r.ReadInt64(), DateTimeKind.Utc) };
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++) idx.Add(r.ReadString(), r.ReadInt32(), r.ReadByte());
        return idx;
    }
}

/// <summary>Owns the current index: loads the cached copy, rebuilds in the background, follows live changes.</summary>
public static class SearchService
{
    public static SearchIndex Current { get; private set; }
    public static bool Indexing { get; private set; }
    public static int IndexingCount { get; private set; }
    public static event Action StatusChanged;

    /// <summary>Files/folders created or renamed since the index was built (path → isDir).</summary>
    public static readonly ConcurrentDictionary<string, bool> Added = new(StringComparer.Ordinal);
    /// <summary>Paths deleted or renamed away since the index was built.</summary>
    public static readonly ConcurrentDictionary<string, byte> Removed = new(StringComparer.Ordinal);

    static readonly List<FileSystemWatcher> watchers = new();
    static CancellationTokenSource buildCts;
    static readonly object gate = new();

    static string CacheFile => Path.Combine(Xdg.Cache, "Orbit", "search-index.bin");

    public static void Start()
    {
        Task.Run(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            try
            {
                if (File.Exists(CacheFile))
                {
                    var loaded = SearchIndex.Load(CacheFile);
                    if (loaded != null) { Current = loaded; Raise(); }
                }
            }
            catch (Exception ex) { AppSettings.LogError(ex); }
            StartWatchers();
            bool stale = Current == null || DateTime.UtcNow - Current.BuiltUtc > TimeSpan.FromHours(6);
            if (stale) Rebuild();
        });
    }

    public static void Rebuild()
    {
        lock (gate)
        {
            if (Indexing) return;
            Indexing = true;
            buildCts = new CancellationTokenSource();
        }
        var ct = buildCts.Token;
        var thread = new Thread(() =>
        {
            var fresh = new SearchIndex();
            // With no cached index, search the one being built so results appear right away.
            if (Current == null) Current = fresh;
            var startedUtc = DateTime.UtcNow;
            try
            {
                fresh.Build(ct, n => { IndexingCount = n; Raise(); });
                Current = fresh;
                // Changes seen before the walk started are now part of the index.
                foreach (var k in Added.Keys.ToList()) Added.TryRemove(k, out _);
                foreach (var k in Removed.Keys.ToList()) Removed.TryRemove(k, out _);
                try { fresh.Save(CacheFile); } catch (Exception ex) { AppSettings.LogError(ex); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppSettings.LogError(ex); }
            finally
            {
                Indexing = false;
                Raise();
                GC.Collect(2, GCCollectionMode.Optimized, false); // drop the previous index promptly
            }
            _ = startedUtc;
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "SearchIndexer" };
        thread.Start();
    }

    public static void Stop()
    {
        buildCts?.Cancel();
        lock (watchers) { foreach (var w in watchers) { try { w.Dispose(); } catch { } } watchers.Clear(); }
    }

    static void Raise() { try { StatusChanged?.Invoke(); } catch { } }

    /// <summary>
    /// Live changes in your own folders. (Watching all of / with inotify would cost a watch per folder on the system,
    /// so everything else is caught up by the periodic rebuild.)
    /// </summary>
    static void StartWatchers()
    {
        var dirs = new[] { "DESKTOP", "DOCUMENTS", "DOWNLOAD", "PICTURES", "MUSIC", "VIDEOS" }
            .Select(k => Xdg.UserDir(k, k[0] + k[1..].ToLowerInvariant())).Where(Directory.Exists).Distinct().ToList();
        foreach (var d in dirs.Prepend(Xdg.Home))
        {
            try
            {
                var w = new FileSystemWatcher(d)
                {
                    IncludeSubdirectories = d != Xdg.Home,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                };
                w.Created += (_, e) => OnAdded(e.FullPath);
                w.Deleted += (_, e) => OnRemoved(e.FullPath);
                w.Renamed += (_, e) => { OnRemoved(e.OldFullPath); OnAdded(e.FullPath); };
                w.Error += (_, _) => { }; // overflow: the periodic rebuild will catch up
                w.EnableRaisingEvents = true;
                lock (watchers) watchers.Add(w);
            }
            catch { }
        }
    }

    static void OnAdded(string path)
    {
        if (SearchIndex.IsSkippedPath(path)) return;
        Removed.TryRemove(path, out _);
        bool dir;
        try { dir = Directory.Exists(path); } catch { dir = false; }
        Added[path] = dir;
        if (Added.Count > 200_000) Rebuild();
    }

    static void OnRemoved(string path)
    {
        if (SearchIndex.IsSkippedPath(path)) return;
        Added.TryRemove(path, out _);
        Removed[path] = 0;
    }
}
