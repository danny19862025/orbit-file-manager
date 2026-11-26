using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using Microsoft.Win32;

namespace Orbit;

/// <summary>
/// "System / Wallpaper": a theme built from the desktop instead of a fixed palette.
/// 1. pywal (or winwal / wpgtk) — reads ~/.cache/wal/colors.json, the palette pywal makes from your wallpaper.
/// 2. Otherwise Windows — your accent colour (which Windows can pick from the wallpaper) and light/dark app mode.
/// <see cref="Watch"/> re-applies it as soon as either changes.
/// </summary>
public static class WallpaperTheme
{
    public static string PywalFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "wal", "colors.json");

    /// <summary>Where the current colours came from, for the Settings card.</summary>
    public static string Source { get; private set; } = "";

    public static ThemeDef Build()
    {
        try { if (File.Exists(PywalFile)) return FromPywal(PywalFile); } catch { }
        return FromWindows();
    }

    // ---------- pywal ----------
    static ThemeDef FromPywal(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var root = doc.RootElement;
        var special = root.GetProperty("special");
        var colors = root.GetProperty("colors");
        Color bg = Parse(special.GetProperty("background").GetString()), fg = Parse(special.GetProperty("foreground").GetString());
        var palette = Enumerable.Range(1, 6).Select(i => colors.TryGetProperty("color" + i, out var c) ? Parse(c.GetString()) : fg).ToList();
        // the two most colourful palette entries become the accents
        var ranked = palette.OrderByDescending(Saturation).ToList();
        Source = "pywal · " + file;
        return Make("Wallpaper colours (pywal)", bg, fg, ranked[0], ranked.Count > 1 ? ranked[1] : ranked[0]);
    }

    // ---------- Windows ----------
    static ThemeDef FromWindows()
    {
        bool light = false;
        Color accent = Color.FromRgb(0x00, 0x78, 0xD4);
        try
        {
            using var personalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = personalize?.GetValue("AppsUseLightTheme") is int v && v == 1;
            using var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (dwm?.GetValue("AccentColor") is int abgr)
                accent = Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
        }
        catch { }
        // a calm, slightly accent-tinted neutral base, like Windows 11's own Mica surfaces
        var bg = light ? Mix(Color.FromRgb(0xF7, 0xF7, 0xF8), accent, 0.04) : Mix(Color.FromRgb(0x1C, 0x1C, 0x20), accent, 0.06);
        var fg = light ? Color.FromRgb(0x1F, 0x1F, 0x24) : Color.FromRgb(0xE8, 0xE8, 0xEE);
        var second = Hue(accent, 40);
        Source = "Windows accent colour" + (light ? " · light mode" : " · dark mode");
        return Make("Your Windows accent", bg, fg, accent, second);
    }

    static ThemeDef Make(string description, Color bg, Color fg, Color accent, Color second)
    {
        bool light = Luminance(bg) > 0.5;
        accent = Readable(accent, bg, light);
        second = Readable(second, bg, light);
        var shade = light ? Colors.Black : Colors.Black;
        Color mantle = light ? Mix(bg, Colors.Black, 0.04) : Mix(bg, shade, 0.18);
        Color crust = light ? Mix(bg, Colors.Black, 0.08) : Mix(bg, shade, 0.32);
        return new ThemeDef(Themes.WallpaperName, description, light,
            Hex(bg), Hex(mantle), Hex(crust),
            Hex(Mix(bg, fg, 0.10)), Hex(Mix(bg, fg, 0.18)), Hex(Mix(bg, fg, 0.30)),
            Hex(fg), Hex(Mix(fg, bg, 0.32)), Hex(accent), Hex(second));
    }

    // ---------- live updates ----------
    static FileSystemWatcher watcher;
    static bool subscribed;

    /// <summary>Calls <paramref name="changed"/> (on a background thread) whenever pywal writes new colours or Windows' accent / mode changes.</summary>
    public static void Watch(Action changed)
    {
        if (!subscribed)
        {
            subscribed = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle) changed();
            };
        }
        try
        {
            var dir = Path.GetDirectoryName(PywalFile)!;
            if (watcher == null && Directory.Exists(dir))
            {
                watcher = new FileSystemWatcher(dir, "colors.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                DateTime last = default;
                void Fire(object s, FileSystemEventArgs e)
                {
                    if ((DateTime.UtcNow - last).TotalMilliseconds < 400) return; // pywal writes the file in a few steps
                    last = DateTime.UtcNow;
                    changed();
                }
                watcher.Changed += Fire;
                watcher.Created += Fire;
                watcher.Renamed += (s, e) => Fire(s, e);
                watcher.EnableRaisingEvents = true;
            }
        }
        catch { }
    }

    // ---------- colour helpers ----------
    static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

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

    /// <summary>Lightens (dark themes) or darkens (light themes) an accent until it stands out from the background.</summary>
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

    /// <summary>The same colour with its hue turned by <paramref name="degrees"/>.</summary>
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
