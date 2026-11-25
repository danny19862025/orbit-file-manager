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
/// In-memory index of every file and folder on the local drives (names + parent links, so it stays compact).
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
            if (sb.Length > 0 && sb[^1] != '\\') sb.Append('\\');
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
    static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", "$WinREAgent", "$SysReset", "$Windows.~BT", "$Windows.~WS",
        "Config.Msi", "Recovery", "node_modules", ".git", "WinSxS", "__pycache__", ".vs"
    };

    public static bool IsSkippedPath(string path)
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(win) && path.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var part in path.Split('\\'))
            if (SkipDirNames.Contains(part)) return true;
        return false;
    }

    public static IEnumerable<string> IndexedRoots() =>
        DriveInfo.GetDrives()
            .Where(d => { try { return d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable); } catch { return false; } })
            .Select(d => d.RootDirectory.FullName);

    /// <summary>Walks every local drive. Runs on a background thread; the index is searchable as it grows.</summary>
    public void Build(CancellationToken ct, Action<int> progress = null)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
            BufferSize = 64 * 1024
        };
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
                IEnumerable<(string name, bool isDir, FileAttributes attrs)> entries;
                try
                {
                    entries = new FileSystemEnumerable<(string, bool, FileAttributes)>(dir,
                        (ref FileSystemEntry e) => (e.FileName.ToString(), e.IsDirectory, e.Attributes), opts);
                }
                catch { continue; }
                try
                {
                    foreach (var (name, isDir, attrs) in entries)
                    {
                        byte flags = (byte)((isDir ? FlagDir : 0) | ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0 ? FlagHidden : 0));
                        if (isDir && SkipDirNames.Contains(name)) continue;
                        int child = Add(name, idx, flags);
                        if (!isDir) continue;
                        var full = dir.EndsWith('\\') ? dir + name : dir + "\\" + name;
                        if (!string.IsNullOrEmpty(windows) && string.Equals(full, windows, StringComparison.OrdinalIgnoreCase)) continue;
                        if ((attrs & FileAttributes.ReparsePoint) != 0)
                        {
                            // Follow cloud-storage placeholders (OneDrive etc.) but not junctions/symlinks, which loop or double count.
                            try { if (new DirectoryInfo(full).LinkTarget != null) continue; } catch { continue; }
                        }
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
    public static readonly ConcurrentDictionary<string, bool> Added = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Paths deleted or renamed away since the index was built.</summary>
    public static readonly ConcurrentDictionary<string, byte> Removed = new(StringComparer.OrdinalIgnoreCase);

    static readonly List<FileSystemWatcher> watchers = new();
    static CancellationTokenSource buildCts;
    static readonly object gate = new();

    static string CacheFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbit", "search-index.bin");

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

    static void StartWatchers()
    {
        foreach (var root in SearchIndex.IndexedRoots())
        {
            try
            {
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
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
