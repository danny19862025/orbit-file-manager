using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;

namespace Orbit;

public sealed class HighlightSeg
{
    public string Text { get; init; }
    public bool Match { get; init; }
}

public sealed class SearchHit
{
    public string Path { get; init; }
    public string Name { get; init; }
    public bool IsDir { get; init; }
    public bool Hidden { get; init; }
    public int Score { get; set; }
    public List<HighlightSeg> Segments { get; set; }
    public string Where { get; set; }
    FileItem iconItem;
    FileItem Item => iconItem ??= MakeItem();
    FileItem MakeItem()
    {
        var f = FileItem.Remote(Path, Name, IsDir, 0, default);
        f.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(FileItem.SmallIcon)) IconChanged?.Invoke(); };
        return f;
    }
    public IImage Icon => Item.SmallIcon;
    public event System.Action IconChanged;
}

/// <summary>Fuzzy filename search over the drive index: "invq4" finds "invoice_Q4_final.pdf".</summary>
public static class FuzzySearch
{
    const int Candidates = 400;

    public static (List<SearchHit> hits, int searched) Run(string query, string kind, int limit, CancellationToken ct)
    {
        var tokens = query.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                          .Select(t => t.Replace('\\', '/')).ToArray();
        var idx = SearchService.Current;
        if (tokens.Length == 0) return (new List<SearchHit>(), idx?.Count ?? 0);

        var pool = new List<(int score, int index, string path)>();
        int searched = 0;
        if (idx != null)
        {
            var (chunks, n) = idx.View();
            searched = n;
            int chunkCount = (n + 65535) >> 16;
            var partial = new PriorityQueue<int, int>[chunkCount];
            Parallel.For(0, chunkCount, new ParallelOptions { CancellationToken = ct }, c =>
            {
                var heap = new PriorityQueue<int, int>();
                int start = c << 16, end = Math.Min(n, start + 65536);
                for (int i = start; i < end; i++)
                {
                    if ((i & 4095) == 0 && ct.IsCancellationRequested) return;
                    byte flags = SearchIndex.FlagsAt(chunks, i);
                    bool dir = (flags & SearchIndex.FlagDir) != 0;
                    if (kind == "folders" && !dir) continue;
                    if (kind == "files" && dir) continue;
                    var name = SearchIndex.NameAt(chunks, i);
                    int s = ScoreEntry(name, tokens, chunks, i);
                    if (s <= 0) continue;
                    if ((flags & SearchIndex.FlagHidden) != 0) s -= 150;
                    if (heap.Count < Candidates) heap.Enqueue(i, s);
                    else if (s > PeekPriority(heap)) { heap.Dequeue(); heap.Enqueue(i, s); }
                }
                partial[c] = heap;
            });
            ct.ThrowIfCancellationRequested();
            var merged = new List<(int score, int index)>();
            foreach (var h in partial)
            {
                if (h == null) continue;
                while (h.TryDequeue(out var i, out var s)) merged.Add((s, i));
            }
            foreach (var (s, i) in merged.OrderByDescending(m => m.score).Take(Candidates))
                pool.Add((s, i, idx.PathOf(i)));
        }

        // things created since the index was built
        foreach (var kv in SearchService.Added)
        {
            if (kind == "folders" && !kv.Value) continue;
            if (kind == "files" && kv.Value) continue;
            var name = System.IO.Path.GetFileName(kv.Key);
            int s = 0;
            foreach (var t in tokens) { int ts = ScoreToken(name, t); if (ts <= 0) { s = 0; break; } s += ts; }
            if (s > 0) pool.Add((s, -1, kv.Key));
            searched++;
        }

        var home = Xdg.Home;
        var hits = new List<SearchHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (score, index, path) in pool)
        {
            if (SearchService.Removed.ContainsKey(path) || !seen.Add(path)) continue;
            int s = score + LocationBonus(path, home);
            var name = System.IO.Path.GetFileName(path.TrimEnd('/'));
            if (string.IsNullOrEmpty(name)) name = path;
            byte flags = index >= 0 ? idx.Flags(index) : (SearchService.Added.TryGetValue(path, out var d) && d ? SearchIndex.FlagDir : (byte)0);
            hits.Add(new SearchHit { Path = path, Name = name, IsDir = (flags & SearchIndex.FlagDir) != 0, Hidden = (flags & SearchIndex.FlagHidden) != 0, Score = s });
        }
        var top = hits.OrderByDescending(h => h.Score).ThenBy(h => h.Name.Length).Take(limit).ToList();
        foreach (var h in top)
        {
            h.Segments = Highlight(h.Name, tokens);
            var parent = System.IO.Path.GetDirectoryName(h.Path) ?? "";
            h.Where = parent == home || parent.StartsWith(home + "/", StringComparison.Ordinal) ? "~" + parent[home.Length..] : parent;
        }
        return (top, searched);
    }

    static string[] personal;

    /// <summary>Prefer things people work with (Desktop, Documents, Downloads, Pictures…) over tool caches and system files.</summary>
    static int LocationBonus(string path, string home)
    {
        personal ??= new[] { "DESKTOP", "DOCUMENTS", "PICTURES", "MUSIC", "VIDEOS", "DOWNLOAD" }
            .Select(k => Xdg.UserDir(k, k[0] + k[1..].ToLowerInvariant()))
            .Where(p => !string.IsNullOrEmpty(p)).Select(p => p.TrimEnd('/') + "/").ToArray();

        int s = 0;
        if (personal.Any(p => path.StartsWith(p, StringComparison.Ordinal))) s += 160;
        else if (path.StartsWith(home + "/", StringComparison.Ordinal)) s += 40;
        else s -= 80; // /opt, /srv, other drives' system folders

        if (path.Contains("/.local/share/", StringComparison.Ordinal) || path.Contains("/.config/", StringComparison.Ordinal)) s -= 160;
        foreach (var noisy in new[] { "/.vscode/extensions/", "/.nuget/", "/.cargo/", "/.rustup/", "/site-packages/", "/packages/",
                                     "/.cache/", "/cache/", "/share/doc/", "/obj/", "/bin/Debug/", "/bin/Release/", "/.steam/", "/Steam/", "/.wine/", "/.var/app/" })
            if (path.Contains(noisy, StringComparison.OrdinalIgnoreCase)) { s -= 120; break; }
        if (path.Contains("/.", StringComparison.Ordinal)) s -= 60; // inside a dot-folder like .vscode, .cache

        int depth = 0;
        foreach (var ch in path) if (ch == '/') depth++;
        return s - depth * 9;
    }

    static int PeekPriority(PriorityQueue<int, int> heap)
    {
        heap.TryPeek(out _, out var p);
        return p;
    }

    /// <summary>Every token must match the name or (more weakly) a parent folder; at least one must hit the name.</summary>
    static int ScoreEntry(string name, string[] tokens, SearchIndex.Chunk[] chunks, int i)
    {
        int total = 0;
        bool anyName = false;
        foreach (var t in tokens)
        {
            int s = t.Contains('/') ? 0 : ScoreToken(name, t);
            if (s > 0) { total += s; anyName = true; continue; }
            // try parent folders (e.g. "finance invoice")
            int p = SearchIndex.ParentAt(chunks, i), hops = 0, best = 0;
            var tt = t.Trim('/');
            while (p >= 0 && hops++ < 24)
            {
                var pn = SearchIndex.NameAt(chunks, p);
                int ps = pn.IndexOf(tt, StringComparison.OrdinalIgnoreCase) >= 0 ? 300 - hops * 10 : 0;
                if (ps > best) best = ps;
                p = SearchIndex.ParentAt(chunks, p);
            }
            if (best <= 0) return 0;
            total += best;
        }
        return anyName ? total : 0;
    }

    static bool IsBoundary(string s, int i)
    {
        if (i == 0) return true;
        char a = s[i - 1], b = s[i];
        if (a is '_' or '-' or ' ' or '.' or '(' or ')' or '[' or ']' or '+') return true;
        if (char.IsUpper(b) && char.IsLower(a)) return true;
        return char.IsDigit(a) != char.IsDigit(b);
    }

    public static int ScoreToken(string s, string t)
    {
        int at = s.IndexOf(t, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            int sc = 1000 + t.Length * 12 - s.Length;
            if (at == 0) sc += 400;
            else if (IsBoundary(s, at)) sc += 250;
            if (t.Length == s.Length) sc += 700;
            else
            {
                int dot = s.LastIndexOf('.');
                if (dot == t.Length && at == 0) sc += 500; // whole name without extension
            }
            return sc;
        }
        // subsequence
        int j = 0, score = 0, last = -2, run = 0, firstAt = -1;
        for (int i = 0; i < s.Length && j < t.Length; i++)
        {
            if (char.ToLowerInvariant(s[i]) != t[j]) continue;
            int b = 10;
            if (i == last + 1) { run++; b += 18 * run; } else run = 0;
            if (IsBoundary(s, i)) b += 35;
            if (firstAt < 0) { firstAt = i; if (i == 0) b += 40; }
            score += b;
            last = i;
            j++;
        }
        if (j < t.Length) return 0;
        int spread = last - firstAt + 1 - t.Length;
        int result = 200 + score - spread * 3 - s.Length / 2;
        return Math.Max(1, result);
    }

    static List<HighlightSeg> Highlight(string name, string[] tokens)
    {
        var mask = new bool[name.Length];
        foreach (var t in tokens)
        {
            int at = name.IndexOf(t, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) { for (int k = 0; k < t.Length; k++) mask[at + k] = true; continue; }
            var tmp = new List<int>();
            int j = 0;
            for (int i = 0; i < name.Length && j < t.Length; i++)
                if (char.ToLowerInvariant(name[i]) == t[j]) { tmp.Add(i); j++; }
            if (j == t.Length) foreach (var k in tmp) mask[k] = true;
        }
        var segs = new List<HighlightSeg>();
        int startSeg = 0;
        for (int i = 1; i <= name.Length; i++)
        {
            if (i == name.Length || mask[i] != mask[startSeg])
            {
                segs.Add(new HighlightSeg { Text = name[startSeg..i], Match = name.Length > 0 && mask[startSeg] });
                startSeg = i;
            }
        }
        return segs;
    }
}

/// <summary>A TextBlock that shows a name with the matched letters highlighted.</summary>
public sealed class HighlightTextBlock : Avalonia.Controls.TextBlock
{
    public static readonly Avalonia.StyledProperty<List<HighlightSeg>> SegmentsProperty =
        Avalonia.AvaloniaProperty.Register<HighlightTextBlock, List<HighlightSeg>>(nameof(Segments));

    public List<HighlightSeg> Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }

    protected override System.Type StyleKeyOverride => typeof(Avalonia.Controls.TextBlock);

    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SegmentsProperty) return;
        Inlines ??= new Avalonia.Controls.Documents.InlineCollection();
        Inlines.Clear();
        if (Segments == null) return;
        foreach (var s in Segments)
        {
            var run = new Avalonia.Controls.Documents.Run(s.Text);
            if (s.Match)
            {
                run.Foreground = Themes.Brush("AccentBrush");
                run.FontWeight = FontWeight.SemiBold;
            }
            Inlines.Add(run);
        }
    }
}
