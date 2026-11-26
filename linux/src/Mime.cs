using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Orbit;

/// <summary>
/// The shared-mime-info database (/usr/share/mime): file name → MIME type, a readable type name
/// ("Markdown document"), and the icon names the desktop's icon theme uses for it.
/// </summary>
public static class Mime
{
    static readonly object gate = new();
    static bool loaded;
    static readonly Dictionary<string, (int weight, string mime)> byExt = new(StringComparer.Ordinal);
    static readonly Dictionary<string, (int weight, string mime)> byExtCi = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> byName = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> icons = new(StringComparer.Ordinal);
    static readonly Dictionary<string, string> generic = new(StringComparer.Ordinal);
    static readonly Dictionary<string, string> aliases = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<string, string> comments = new(StringComparer.Ordinal);

    static IEnumerable<string> MimeDirs() =>
        new[] { Path.Combine(Xdg.Data, "mime") }.Concat(Xdg.DataDirs.Select(d => Path.Combine(d, "mime"))).Where(Directory.Exists).Distinct();

    static void Load()
    {
        if (loaded) return;
        lock (gate)
        {
            if (loaded) return;
            foreach (var dir in MimeDirs().Reverse())
            {
                try
                {
                    var g2 = Path.Combine(dir, "globs2");
                    if (File.Exists(g2))
                        foreach (var line in File.ReadLines(g2))
                        {
                            if (line.StartsWith('#')) continue;
                            var p = line.Split(':');
                            if (p.Length < 3 || !int.TryParse(p[0], out var w)) continue;
                            var mime = p[1];
                            var glob = p[2];
                            bool cs = p.Length > 3 && p[3].Contains("cs");
                            if (glob.StartsWith("*.") && glob.IndexOfAny(new[] { '*', '?', '[' }, 2) < 0)
                            {
                                var ext = glob[1..]; // ".txt"
                                var dict = cs ? byExt : byExtCi;
                                if (!dict.TryGetValue(ext, out var cur) || w > cur.weight || (w == cur.weight && ext.Length > 0)) dict[ext] = (w, mime);
                            }
                            else if (glob.IndexOfAny(new[] { '*', '?', '[' }) < 0) byName.TryAdd(glob, mime);
                        }
                    Pairs(Path.Combine(dir, "icons"), icons);
                    Pairs(Path.Combine(dir, "generic-icons"), generic);
                    Pairs(Path.Combine(dir, "aliases"), aliases, ' ');
                }
                catch { }
            }
            loaded = true;
        }
    }

    static void Pairs(string file, Dictionary<string, string> into, char sep = ':')
    {
        if (!File.Exists(file)) return;
        foreach (var line in File.ReadLines(file))
        {
            int i = line.IndexOf(sep);
            if (i > 0) into[line[..i]] = line[(i + 1)..].Trim();
        }
    }

    /// <summary>MIME type for a file name (by its extension, or full name like "Makefile").</summary>
    public static string ForName(string name, bool dir = false)
    {
        if (dir) return "inode/directory";
        Load();
        if (string.IsNullOrEmpty(name)) return "application/octet-stream";
        if (byName.TryGetValue(name, out var n)) return n;
        // longest matching extension first (".tar.gz" before ".gz")
        for (int i = name.IndexOf('.'); i >= 0 && i < name.Length - 1; i = name.IndexOf('.', i + 1))
        {
            var ext = name[i..];
            if (byExt.TryGetValue(ext, out var e)) return e.mime;
            if (byExtCi.TryGetValue(ext, out var c)) return c.mime;
        }
        return "application/octet-stream";
    }

    /// <summary>Icon names to try for a MIME type, best first.</summary>
    public static IEnumerable<string> IconNames(string mime)
    {
        Load();
        if (aliases.TryGetValue(mime, out var canon)) mime = canon;
        if (icons.TryGetValue(mime, out var i)) yield return i;
        yield return mime.Replace('/', '-');
        if (generic.TryGetValue(mime, out var g)) yield return g;
        var media = mime.Split('/')[0];
        yield return media + "-x-generic";
        if (media == "text") yield return "text-plain";
        yield return "application-octet-stream";
        yield return "unknown";
    }

    /// <summary>English description of a MIME type ("PNG image"), read from its shared-mime-info XML.</summary>
    public static string Comment(string mime)
    {
        Load();
        if (aliases.TryGetValue(mime, out var canon)) mime = canon;
        return comments.GetOrAdd(mime, m =>
        {
            foreach (var dir in MimeDirs())
            {
                try
                {
                    var f = Path.Combine(dir, m + ".xml");
                    if (!File.Exists(f)) continue;
                    var text = File.ReadAllText(f);
                    var match = Regex.Match(text, @"<comment>([^<]*)</comment>");
                    if (match.Success) return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
                }
                catch { }
            }
            return null;
        });
    }
}
