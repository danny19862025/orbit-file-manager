using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Media;

namespace Orbit;

/// <summary>
/// "System / Wallpaper": a theme built from the desktop instead of a fixed palette.
/// 1. pywal (or wpgtk / wallust) — reads ~/.cache/wal/colors.json, the palette made from your wallpaper.
/// 2. KDE Plasma — your accent colour (which Plasma can take from the wallpaper) and light/dark colour scheme.
/// 3. GNOME — the accent colour and light/dark style from GNOME Settings.
/// <see cref="Watch"/> re-applies it as soon as any of them changes.
/// </summary>
public static class WallpaperTheme
{
    public static string PywalFile => Path.Combine(Xdg.Cache, "wal", "colors.json");
    static string KdeGlobals => Path.Combine(Xdg.Config, "kdeglobals");

    /// <summary>Where the current colours came from, for the Settings card.</summary>
    public static string Source { get; private set; } = "";

    public static ThemeDef Build()
    {
        try { if (File.Exists(PywalFile)) return FromPywal(PywalFile); } catch { }
        try { if (File.Exists(KdeGlobals) && FromKde() is { } kde) return kde; } catch { }
        try { if (FromGnome() is { } gnome) return gnome; } catch { }
        Source = "default accent colour";
        return Make("Your desktop's colours", Color.FromRgb(0x1C, 0x1C, 0x20), Color.FromRgb(0xE8, 0xE8, 0xEE),
            Color.FromRgb(0x3D, 0xAE, 0xE9), Hue(Color.FromRgb(0x3D, 0xAE, 0xE9), 40));
    }

    // ---------- pywal ----------
    static ThemeDef FromPywal(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var root = doc.RootElement;
        var special = root.GetProperty("special");
        var colors = root.GetProperty("colors");
        Color bg = Color.Parse(special.GetProperty("background").GetString()), fg = Color.Parse(special.GetProperty("foreground").GetString());
        var palette = Enumerable.Range(1, 6).Select(i => colors.TryGetProperty("color" + i, out var c) ? Color.Parse(c.GetString()) : fg).ToList();
        var ranked = palette.OrderByDescending(Saturation).ToList();
        Source = "pywal · " + file;
        return Make("Wallpaper colours (pywal)", bg, fg, ranked[0], ranked.Count > 1 ? ranked[1] : ranked[0]);
    }

    // ---------- KDE Plasma ----------
    static ThemeDef FromKde()
    {
        string section = null, accentText = null, windowBg = null, windowFg = null;
        foreach (var raw in File.ReadLines(KdeGlobals))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { section = line; continue; }
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq];
            var val = line[(eq + 1)..];
            if (section == "[General]" && key == "AccentColor") accentText = val;
            else if (section == "[Colors:Window]" && key == "BackgroundNormal") windowBg = val;
            else if (section == "[Colors:Window]" && key == "ForegroundNormal") windowFg = val;
            else if (section == "[Colors:Selection]" && key == "BackgroundNormal" && accentText == null) accentText = val;
        }
        if (accentText == null && windowBg == null) return null;
        var accent = ParseRgb(accentText) ?? Color.FromRgb(0x3D, 0xAE, 0xE9);
        var winBg = ParseRgb(windowBg);
        bool light = winBg is { } wb && Luminance(wb) > 0.4;
        var bg = winBg is { } w ? Mix(w, accent, 0.04) : (light ? Mix(Color.FromRgb(0xF7, 0xF7, 0xF8), accent, 0.04) : Mix(Color.FromRgb(0x1C, 0x1C, 0x20), accent, 0.06));
        var fg = ParseRgb(windowFg) ?? (light ? Color.FromRgb(0x1F, 0x1F, 0x24) : Color.FromRgb(0xE8, 0xE8, 0xEE));
        Source = "KDE Plasma accent colour" + (light ? " · light colours" : " · dark colours");
        return Make("Your Plasma accent", bg, fg, accent, Hue(accent, 40));
    }

    static Color? ParseRgb(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var p = s.Split(',');
        if (p.Length >= 3 && byte.TryParse(p[0], out var r) && byte.TryParse(p[1], out var g) && byte.TryParse(p[2], out var b)) return Color.FromRgb(r, g, b);
        return Color.TryParse(s, out var c) ? c : null;
    }

    // ---------- GNOME ----------
    static ThemeDef FromGnome()
    {
        var scheme = Gsettings("org.gnome.desktop.interface", "color-scheme");
        if (scheme == null) return null;
        bool light = !scheme.Contains("dark");
        var accentName = Gsettings("org.gnome.desktop.interface", "accent-color")?.Trim('\'') ?? "blue";
        var accent = accentName switch
        {
            "teal" => Color.Parse("#2190A4"), "green" => Color.Parse("#3A944A"), "yellow" => Color.Parse("#C88800"),
            "orange" => Color.Parse("#ED5B00"), "red" => Color.Parse("#E62D42"), "pink" => Color.Parse("#D56199"),
            "purple" => Color.Parse("#9141AC"), "slate" => Color.Parse("#6F8396"), _ => Color.Parse("#3584E4"),
        };
        var bg = light ? Mix(Color.FromRgb(0xFA, 0xFA, 0xFB), accent, 0.03) : Mix(Color.FromRgb(0x24, 0x24, 0x24), accent, 0.05);
        var fg = light ? Color.FromRgb(0x1E, 0x1E, 0x22) : Color.FromRgb(0xF0, 0xF0, 0xF2);
        Source = "GNOME accent colour" + (light ? " · light style" : " · dark style");
        return Make("Your GNOME accent", bg, fg, accent, Hue(accent, 40));
    }

    static string Gsettings(string schema, string key)
    {
        try
        {
            var psi = new ProcessStartInfo("gsettings", $"get {schema} {key}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            var o = p!.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(1500);
            return p.ExitCode == 0 && o.Length > 0 ? o : null;
        }
        catch { return null; }
    }

    static ThemeDef Make(string description, Color bg, Color fg, Color accent, Color second)
    {
        bool light = Luminance(bg) > 0.5;
        accent = Readable(accent, bg, light);
        second = Readable(second, bg, light);
        Color mantle = light ? Mix(bg, Colors.Black, 0.04) : Mix(bg, Colors.Black, 0.18);
        Color crust = light ? Mix(bg, Colors.Black, 0.08) : Mix(bg, Colors.Black, 0.32);
        return new ThemeDef(Themes.WallpaperName, description, light,
            Hex(bg), Hex(mantle), Hex(crust),
            Hex(Mix(bg, fg, 0.10)), Hex(Mix(bg, fg, 0.18)), Hex(Mix(bg, fg, 0.30)),
            Hex(fg), Hex(Mix(fg, bg, 0.32)), Hex(accent), Hex(second));
    }

    // ---------- live updates ----------
    static FileSystemWatcher pywalWatcher, kdeWatcher;
    static Process gnomeMonitor;

    /// <summary>Calls <paramref name="changed"/> (on a background thread) whenever pywal, Plasma or GNOME colours change.</summary>
    public static void Watch(Action changed)
    {
        DateTime last = default;
        void Fire(object s, FileSystemEventArgs e)
        {
            if ((DateTime.UtcNow - last).TotalMilliseconds < 400) return; // files are written in a few steps
            last = DateTime.UtcNow;
            changed();
        }
        FileSystemWatcher Make(string dir, string filter)
        {
            if (!Directory.Exists(dir)) return null;
            var w = new FileSystemWatcher(dir, filter) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            w.Changed += Fire;
            w.Created += Fire;
            w.Renamed += (s, e) => Fire(s, e);
            w.EnableRaisingEvents = true;
            return w;
        }
        try { pywalWatcher ??= Make(Path.GetDirectoryName(PywalFile)!, "colors.json"); } catch { }
        try { kdeWatcher ??= Make(Xdg.Config, "kdeglobals"); } catch { }
        try
        {
            if (gnomeMonitor == null && (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Contains("GNOME", StringComparison.OrdinalIgnoreCase))
            {
                var psi = new ProcessStartInfo("gsettings", "monitor org.gnome.desktop.interface") { RedirectStandardOutput = true, UseShellExecute = false };
                gnomeMonitor = Process.Start(psi);
                if (gnomeMonitor != null)
                {
                    gnomeMonitor.OutputDataReceived += (_, e) => { if (e.Data != null && (e.Data.Contains("color-scheme") || e.Data.Contains("accent-color"))) changed(); };
                    gnomeMonitor.BeginOutputReadLine();
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { gnomeMonitor.Kill(); } catch { } };
                }
            }
        }
        catch { }
    }

    // ---------- colour helpers ----------
    static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    static Color Mix(Color a, Color b, double t) => Themes.Mix(a, b, t);

    static double Luminance(Color c)
    {
        static double L(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
    }

    static double Contrast(Color a, Color b)
    {
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    static Color Readable(Color c, Color bg, bool light)
    {
        for (int i = 0; i < 12 && Contrast(c, bg) < 4.0; i++)
            c = Mix(c, light ? Colors.Black : Colors.White, 0.12);
        return c;
    }

    static double Saturation(Color c)
    {
        double max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0, min = Math.Min(c.R, Math.Min(c.G, c.B)) / 255.0;
        double l = (max + min) / 2;
        return max == min ? 0 : (max - min) / (1 - Math.Abs(2 * l - 1)) * (l > 0.12 && l < 0.92 ? 1 : 0.3);
    }

    static Color Hue(Color c, double degrees)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        double l = (max + min) / 2, s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        h = ((h + degrees) % 360 + 360) % 360;
        double C = (1 - Math.Abs(2 * l - 1)) * s, X = C * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - C / 2;
        var (r1, g1, b1) = h < 60 ? (C, X, 0.0) : h < 120 ? (X, C, 0.0) : h < 180 ? (0.0, C, X) : h < 240 ? (0.0, X, C) : h < 300 ? (X, 0.0, C) : (C, 0.0, X);
        return Color.FromRgb((byte)Math.Round((r1 + m) * 255), (byte)Math.Round((g1 + m) * 255), (byte)Math.Round((b1 + m) * 255));
    }
}
