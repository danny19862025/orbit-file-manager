using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Orbit;

/// <summary>Opening things the Linux way: default apps (xdg-open), "Open with", terminals and the desktop's file manager.</summary>
public static class Shell
{
    public static void Open(string path)
    {
        // Prefer the desktop's opener; fall back to the registered default app.
        if (Try("xdg-open", path) || Try("gio", "open", path)) return;
        var app = DesktopApps.DefaultFor(Mime.ForName(Path.GetFileName(path), Directory.Exists(path)));
        if (app != null) { app.Launch(new[] { path }); return; }
        throw new InvalidOperationException("No app is set up to open this kind of file. Use Open with…");
    }

    static bool Try(string exe, params string[] args)
    {
        if (IconService.Which(exe) == null) return false;
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            var dir = SafeDir(args.Last());
            if (dir != null) psi.WorkingDirectory = dir;
            var p = Process.Start(psi);
            if (p == null) return false;
            p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEndAsync();
            return true;
        }
        catch { return false; }
    }

    static string SafeDir(string path)
    {
        try { return File.Exists(path) ? Path.GetDirectoryName(path) : Directory.Exists(path) ? path : null; } catch { return null; }
    }

    /// <summary>Show the desktop's file manager with the item selected (org.freedesktop.FileManager1).</summary>
    public static void ShowInFileManager(string path) => FileManager1("ShowItems", path, () => OpenInFileManager(Path.GetDirectoryName(path) ?? path));

    /// <summary>Open a folder in the desktop's file manager.</summary>
    public static void OpenInFileManager(string dir) => FileManager1("ShowFolders", dir, () =>
    {
        var app = DesktopApps.DefaultFor("inode/directory");
        if (app != null && !app.Id.StartsWith("orbit", StringComparison.OrdinalIgnoreCase)) app.Launch(new[] { dir });
        else Open(dir);
    });

    /// <summary>The file manager's own Properties dialog for an item, when it offers one.</summary>
    public static void Properties(string path) => FileManager1("ShowItemProperties", path, () => ShowInFileManager(path));

    static void FileManager1(string method, string path, Action fallback)
    {
        try
        {
            var psi = new ProcessStartInfo("dbus-send") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "--session", "--print-reply", "--dest=org.freedesktop.FileManager1", "/org/freedesktop/FileManager1",
                                      "org.freedesktop.FileManager1." + method, "array:string:" + IconService.FileUri(path), "string:" })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            if (p.WaitForExit(4000) && p.ExitCode == 0) return;
        }
        catch { }
        fallback();
    }

    // ------------------------------------------------------------------
    // Terminals
    // ------------------------------------------------------------------
    static readonly (string Exe, string[] DirArgs, string[] RunArgs)[] Terminals =
    {
        ("konsole", new[] { "--workdir", "{dir}" }, new[] { "-e" }),
        ("gnome-terminal", new[] { "--working-directory={dir}" }, new[] { "--" }),
        ("kgx", new[] { "--working-directory={dir}" }, new[] { "-e" }),
        ("ptyxis", new[] { "--working-directory={dir}" }, new[] { "-x" }),
        ("xfce4-terminal", new[] { "--working-directory={dir}" }, new[] { "-x" }),
        ("tilix", new[] { "--working-directory={dir}" }, new[] { "-e" }),
        ("ghostty", new[] { "--working-directory={dir}" }, new[] { "-e" }),
        ("kitty", new[] { "--directory", "{dir}" }, Array.Empty<string>()),
        ("alacritty", new[] { "--working-directory", "{dir}" }, new[] { "-e" }),
        ("wezterm", new[] { "start", "--cwd", "{dir}" }, new[] { "--" }),
        ("foot", new[] { "-D", "{dir}" }, Array.Empty<string>()),
        ("terminator", new[] { "--working-directory={dir}" }, new[] { "-x" }),
        ("xterm", Array.Empty<string>(), new[] { "-e" }),
    };

    /// <summary>The terminal to use: $TERMINAL, then the desktop's own, then any installed one.</summary>
    static (string Exe, string[] DirArgs, string[] RunArgs)? FindTerminal()
    {
        var env = Environment.GetEnvironmentVariable("TERMINAL");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var exe = Path.GetFileName(env.Split(' ')[0]);
            var known = Terminals.FirstOrDefault(t => t.Exe == exe);
            if (IconService.Which(exe) != null || File.Exists(env)) return known.Exe != null ? known with { Exe = env.Split(' ')[0] } : (env.Split(' ')[0], Array.Empty<string>(), new[] { "-e" });
        }
        var desktop = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").ToUpperInvariant();
        var order = Terminals.ToList();
        if (desktop.Contains("GNOME")) order = order.OrderBy(t => t.Exe is "kgx" or "ptyxis" or "gnome-terminal" ? 0 : 1).ToList();
        foreach (var t in order)
            if (IconService.Which(t.Exe) != null) return t;
        return null;
    }

    public static void OpenTerminal(string dir)
    {
        var t = FindTerminal() ?? throw new InvalidOperationException("No terminal app was found. Set $TERMINAL or install one (for example Konsole or GNOME Console).");
        var psi = new ProcessStartInfo(t.Exe) { UseShellExecute = false, WorkingDirectory = dir };
        foreach (var a in t.DirArgs) psi.ArgumentList.Add(a.Replace("{dir}", dir));
        Process.Start(psi);
    }

    /// <summary>Runs a command (e.g. ssh) in a new terminal window.</summary>
    public static void RunInTerminal(params string[] command)
    {
        var t = FindTerminal() ?? throw new InvalidOperationException("No terminal app was found. Set $TERMINAL or install one.");
        var psi = new ProcessStartInfo(t.Exe) { UseShellExecute = false, WorkingDirectory = Xdg.Home };
        foreach (var a in t.RunArgs) psi.ArgumentList.Add(a);
        foreach (var a in command) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }
}

/// <summary>Installed applications (.desktop files) and which file types they open.</summary>
public sealed class DesktopApps
{
    public string Id { get; init; }
    public string Name { get; init; }
    public string Exec { get; init; }
    public string IconName { get; init; }
    public bool Terminal { get; init; }
    public string[] MimeTypes { get; init; } = Array.Empty<string>();
    public string File { get; init; }

    static List<DesktopApps> all;
    static readonly object gate = new();

    static IEnumerable<string> AppDirs() =>
        new[] { Path.Combine(Xdg.Data, "applications") }
            .Concat(Xdg.DataDirs.Select(d => Path.Combine(d, "applications")))
            .Concat(new[] { "/var/lib/flatpak/exports/share/applications", Path.Combine(Xdg.Data, "flatpak/exports/share/applications") })
            .Where(Directory.Exists).Distinct();

    public static List<DesktopApps> All()
    {
        lock (gate)
        {
            if (all != null) return all;
            var byId = new Dictionary<string, DesktopApps>(StringComparer.Ordinal);
            foreach (var dir in AppDirs())
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir, "*.desktop", SearchOption.AllDirectories).ToList(); } catch { continue; }
                foreach (var f in files)
                {
                    var id = Path.GetRelativePath(dir, f).Replace('/', '-');
                    if (byId.ContainsKey(id)) continue; // earlier dirs win (user overrides system)
                    var app = Parse(f, id);
                    byId[id] = app;
                }
            }
            all = byId.Values.Where(a => a != null).ToList();
            return all;
        }
    }

    static DesktopApps Parse(string file, string id)
    {
        try
        {
            string name = null, exec = null, icon = null, tryExec = null, type = null, mimes = null;
            bool hidden = false, noDisplay = false, terminal = false, inEntry = false;
            foreach (var raw in System.IO.File.ReadLines(file))
            {
                var l = raw.Trim();
                if (l.StartsWith('[')) { inEntry = l == "[Desktop Entry]"; continue; }
                if (!inEntry) continue;
                int eq = l.IndexOf('=');
                if (eq <= 0) continue;
                var k = l[..eq].Trim();
                var v = l[(eq + 1)..].Trim();
                switch (k)
                {
                    case "Name": name ??= v; break;
                    case "Exec": exec = v; break;
                    case "Icon": icon = v; break;
                    case "TryExec": tryExec = v; break;
                    case "Type": type = v; break;
                    case "MimeType": mimes = v; break;
                    case "Hidden": hidden = v == "true"; break;
                    case "NoDisplay": noDisplay = v == "true"; break;
                    case "Terminal": terminal = v == "true"; break;
                }
            }
            if (type != "Application" || exec == null || hidden) return null;
            if (tryExec != null && !Path.IsPathRooted(tryExec) && IconService.Which(tryExec) == null) return null;
            if (tryExec != null && Path.IsPathRooted(tryExec) && !System.IO.File.Exists(tryExec)) return null;
            _ = noDisplay; // NoDisplay apps can still open files (many viewers are hidden from menus)
            return new DesktopApps
            {
                Id = id, Name = name ?? id, Exec = exec, IconName = icon, Terminal = terminal, File = file,
                MimeTypes = (mimes ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            };
        }
        catch { return null; }
    }

    /// <summary>Apps that say they open this type (plus the user's own associations), default first.</summary>
    public static List<DesktopApps> For(string mime)
    {
        var apps = All();
        var def = DefaultFor(mime);
        var added = Associations("Added Associations", mime);
        var removed = Associations("Removed Associations", mime);
        var media = mime.Split('/')[0] + "/*";
        var list = apps.Where(a => !removed.Contains(a.Id) && (a.MimeTypes.Contains(mime) || a.MimeTypes.Contains(media) || added.Contains(a.Id)))
                       .OrderByDescending(a => def != null && a.Id == def.Id).ThenByDescending(a => added.Contains(a.Id)).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                       .ToList();
        // text-like files: also offer text editors
        if (mime.StartsWith("text/") || mime is "application/json" or "application/xml" or "application/x-shellscript")
            foreach (var a in apps.Where(a => a.MimeTypes.Contains("text/plain")))
                if (!list.Contains(a)) list.Add(a);
        return list;
    }

    static IEnumerable<string> MimeAppsFiles()
    {
        var desk = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries);
        foreach (var d in desk) yield return Path.Combine(Xdg.Config, d.ToLowerInvariant() + "-mimeapps.list");
        yield return Path.Combine(Xdg.Config, "mimeapps.list");
        yield return Path.Combine(Xdg.Data, "applications", "mimeapps.list");
        foreach (var d in Xdg.DataDirs) yield return Path.Combine(d, "applications", "mimeapps.list");
    }

    static HashSet<string> Associations(string section, string mime)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in MimeAppsFiles())
            foreach (var v in Values(f, section, mime)) set.Add(v);
        return set;
    }

    static IEnumerable<string> Values(string file, string section, string mime)
    {
        if (!System.IO.File.Exists(file)) yield break;
        string cur = null;
        foreach (var raw in System.IO.File.ReadLines(file))
        {
            var l = raw.Trim();
            if (l.StartsWith('[')) { cur = l.Trim('[', ']'); continue; }
            if (cur != section || !l.StartsWith(mime + "=")) continue;
            foreach (var v in l[(mime.Length + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries)) yield return v.Trim();
        }
    }

    public static DesktopApps DefaultFor(string mime)
    {
        var apps = All();
        foreach (var f in MimeAppsFiles())
            foreach (var id in Values(f, "Default Applications", mime))
                if (apps.FirstOrDefault(a => a.Id == id) is { } a) return a;
        return null;
    }

    /// <summary>Makes this app the default for the type (same as "xdg-mime default").</summary>
    public void SetAsDefault(string mime)
    {
        try { Process.Start(new ProcessStartInfo("xdg-mime") { ArgumentList = { "default", Id, mime }, UseShellExecute = false })?.WaitForExit(5000); } catch { }
    }

    /// <summary>Starts the app with these files, expanding the Exec line's field codes.</summary>
    public void Launch(IReadOnlyList<string> files)
    {
        var tokens = Tokenize(Exec);
        var args = new List<string>();
        bool used = false;
        foreach (var t in tokens)
        {
            switch (t)
            {
                case "%f": if (files.Count > 0) args.Add(files[0]); used = true; break;
                case "%F": args.AddRange(files); used = true; break;
                case "%u": if (files.Count > 0) args.Add(IconService.FileUri(files[0])); used = true; break;
                case "%U": args.AddRange(files.Select(IconService.FileUri)); used = true; break;
                case "%i": if (IconName != null) { args.Add("--icon"); args.Add(IconName); } break;
                case "%c": args.Add(Name); break;
                case "%k": args.Add(File); break;
                case "%d" or "%D" or "%n" or "%N" or "%v" or "%m": break;
                default: args.Add(t.Replace("%%", "%")); break;
            }
        }
        if (!used) args.AddRange(files);
        if (args.Count == 0) return;
        if (Terminal) { Shell.RunInTerminal(args.ToArray()); return; }
        var psi = new ProcessStartInfo(args[0]) { UseShellExecute = false, WorkingDirectory = files.Count > 0 && System.IO.File.Exists(files[0]) ? Path.GetDirectoryName(files[0]) : Xdg.Home };
        foreach (var a in args.Skip(1)) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    static List<string> Tokenize(string exec)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < exec.Length; i++)
        {
            char c = exec[i];
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (c == '\\' && quoted && i + 1 < exec.Length) { sb.Append(exec[++i]); continue; }
            if (char.IsWhiteSpace(c) && !quoted) { if (sb.Length > 0 || any) list.Add(sb.ToString()); sb.Clear(); any = false; continue; }
            sb.Append(c);
        }
        if (sb.Length > 0 || any) list.Add(sb.ToString());
        return list;
    }
}
