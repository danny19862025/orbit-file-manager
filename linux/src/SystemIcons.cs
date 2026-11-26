using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Orbit;

/// <summary>
/// The desktop's own icon theme (Breeze, Adwaita, Papirus…), looked up the freedesktop.org way:
/// the theme named in KDE's kdeglobals or GNOME's settings, then the themes it inherits, then hicolor.
/// </summary>
public static class SystemIcons
{
    sealed class Theme
    {
        public string Name;
        public List<string> Roots = new();
        public List<string> Inherits = new();
        /// <summary>icon name → (file, nominal size, scalable)</summary>
        public Dictionary<string, List<(string File, int Size, bool Scalable)>> Icons;
    }

    static readonly object gate = new();
    static List<Theme> chain;
    static readonly Dictionary<string, string> found = new(StringComparer.Ordinal);

    public static string ThemeName { get; private set; }

    static IEnumerable<string> IconBases()
    {
        yield return Path.Combine(Xdg.Home, ".icons");
        yield return Path.Combine(Xdg.Data, "icons");
        foreach (var d in Xdg.DataDirs) yield return Path.Combine(d, "icons");
    }

    /// <summary>The configured icon theme name (KDE, then GNOME, then a sensible default).</summary>
    public static string ConfiguredTheme()
    {
        try
        {
            var kde = Path.Combine(Xdg.Config, "kdeglobals");
            if (File.Exists(kde))
            {
                string section = null;
                foreach (var raw in File.ReadLines(kde))
                {
                    var l = raw.Trim();
                    if (l.StartsWith('[')) { section = l; continue; }
                    if (section == "[Icons]" && l.StartsWith("Theme=")) return l[6..].Trim();
                }
                if ((Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Contains("KDE", StringComparison.OrdinalIgnoreCase)) return "breeze";
            }
        }
        catch { }
        try
        {
            var psi = new ProcessStartInfo("gsettings", "get org.gnome.desktop.interface icon-theme") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            var o = p!.StandardOutput.ReadToEnd().Trim().Trim('\'');
            p.WaitForExit(1500);
            if (p.ExitCode == 0 && o.Length > 0) return o;
        }
        catch { }
        foreach (var t in new[] { "breeze", "Adwaita", "Papirus", "hicolor" })
            if (FindRoots(t).Count > 0) return t;
        return "hicolor";
    }

    static List<string> FindRoots(string name) =>
        IconBases().Select(b => Path.Combine(b, name)).Where(d => File.Exists(Path.Combine(d, "index.theme")) || Directory.Exists(d)).Distinct().ToList();

    static List<Theme> Chain()
    {
        if (chain != null) return chain;
        lock (gate)
        {
            if (chain != null) return chain;
            var list = new List<Theme>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ThemeName = ConfiguredTheme();
            var queue = new Queue<string>();
            queue.Enqueue(ThemeName);
            while (queue.Count > 0 && list.Count < 8)
            {
                var n = queue.Dequeue();
                if (!seen.Add(n)) continue;
                var t = new Theme { Name = n, Roots = FindRoots(n) };
                if (t.Roots.Count == 0) continue;
                foreach (var r in t.Roots)
                {
                    var idx = Path.Combine(r, "index.theme");
                    if (!File.Exists(idx)) continue;
                    foreach (var line in File.ReadLines(idx))
                        if (line.StartsWith("Inherits=")) t.Inherits.AddRange(line[9..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
                list.Add(t);
                foreach (var i in t.Inherits) queue.Enqueue(i);
            }
            if (!seen.Contains("hicolor")) list.Add(new Theme { Name = "hicolor", Roots = FindRoots("hicolor") });
            chain = list;
            return chain;
        }
    }

    static void Index(Theme t)
    {
        if (t.Icons != null) return;
        var icons = new Dictionary<string, List<(string, int, bool)>>(StringComparer.Ordinal);
        foreach (var root in t.Roots)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 }).ToList(); }
            catch { continue; }
            foreach (var f in files)
            {
                var ext = Path.GetExtension(f);
                if (ext != ".svg" && ext != ".png" && ext != ".svgz") continue;
                var rel = f[(root.Length + 1)..];
                if (rel.Contains("symbolic")) continue;
                var name = Path.GetFileNameWithoutExtension(f);
                int size = 0;
                bool scalable = rel.StartsWith("scalable", StringComparison.Ordinal) || ext != ".png";
                foreach (var part in rel.Split('/'))
                {
                    var p = part.Split('@')[0];
                    var x = p.IndexOf('x');
                    if (int.TryParse(x > 0 ? p[..x] : p, out var s) && s > 0 && s <= 1024) { size = s; break; }
                }
                if (!icons.TryGetValue(name, out var l)) icons[name] = l = new List<(string, int, bool)>();
                l.Add((f, size, scalable));
            }
        }
        t.Icons = icons;
    }

    /// <summary>Best file for one of these icon names at about <paramref name="px"/> pixels, or null.</summary>
    public static string Lookup(IEnumerable<string> names, int px)
    {
        var list = names.Where(n => !string.IsNullOrEmpty(n)).ToList();
        var key = string.Join('|', list) + "@" + (px > 32 ? "L" : "S");
        lock (found) if (found.TryGetValue(key, out var hit)) return hit;
        string result = null;
        foreach (var n in list)
        {
            foreach (var t in Chain())
            {
                lock (t) Index(t);
                if (!t.Icons.TryGetValue(n, out var files)) continue;
                // SVGs scale cleanly; otherwise the smallest bitmap at least as big as wanted, else the biggest
                var pick = files.Where(f => f.Scalable && f.File.EndsWith(".svg")).OrderBy(f => Math.Abs(f.Size - px)).FirstOrDefault().File
                           ?? files.Where(f => f.Size >= px).OrderBy(f => f.Size).FirstOrDefault().File
                           ?? files.OrderByDescending(f => f.Size).FirstOrDefault().File;
                if (pick != null) { result = pick; break; }
            }
            if (result != null) break;
        }
        if (result == null)
            foreach (var n in list)
                foreach (var ext in new[] { ".svg", ".png" })
                {
                    var p = Path.Combine("/usr/share/pixmaps", n + ext);
                    if (File.Exists(p)) { result = p; break; }
                }
        lock (found) found[key] = result;
        return result;
    }

    public static void Reset()
    {
        lock (gate) chain = null;
        lock (found) found.Clear();
    }
}
