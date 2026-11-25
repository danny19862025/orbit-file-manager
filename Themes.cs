using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Orbit;

/// <summary>
/// A theme is a small raw palette. Every UI brush (the "tokens") is derived from it and written into
/// Application.Resources; all XAML uses DynamicResource, so Apply() restyles the running app instantly.
/// </summary>
public sealed record ThemeDef(
    string Name, string Description, bool IsLight,
    string Base, string Mantle, string Crust,
    string Surface0, string Surface1, string Surface2,
    string Text, string Subtext, string Accent, string Lavender);

public static class Themes
{
    public static readonly ThemeDef[] All =
    {
        new("Catppuccin Mocha", "Warm dark (default)", false,
            "#1E1E2E", "#181825", "#11111B", "#313244", "#45475A", "#585B70", "#CDD6F4", "#A6ADC8", "#CBA6F7", "#B4BEFE"),
        new("Tokyo Night", "Vibrant deep blue", false,
            "#1A1B26", "#16161E", "#101014", "#292E42", "#3B4261", "#545C7E", "#C0CAF5", "#A9B1D6", "#7AA2F7", "#BB9AF7"),
        new("Rosé Pine", "Soft warm dark", false,
            "#191724", "#1F1D2E", "#121019", "#26233A", "#403D52", "#524F67", "#E0DEF4", "#908CAA", "#C4A7E7", "#EBBCBA"),
        new("Nord", "Cool arctic blue", false,
            "#2E3440", "#292E39", "#242933", "#3B4252", "#434C5E", "#4C566A", "#ECEFF4", "#A3ADBF", "#88C0D0", "#81A1C1"),
        new("Catppuccin Latte", "Clean light", true,
            "#EFF1F5", "#E6E9EF", "#DCE0E8", "#CCD0DA", "#BCC0CC", "#ACB0BE", "#4C4F69", "#6C6F85", "#8839EF", "#7287FD"),
    };

    public static ThemeDef Current { get; private set; } = All[0];
    public static event Action Changed;

    public static ThemeDef Find(string name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static void Apply(string name, bool translucent)
    {
        var t = Find(name);
        Current = t;
        Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
        Color baseC = C(t.Base), mantle = C(t.Mantle), crust = C(t.Crust), s0 = C(t.Surface0), s1 = C(t.Surface1),
              s2 = C(t.Surface2), text = C(t.Text), sub = C(t.Subtext), accent = C(t.Accent), lav = C(t.Lavender);

        // Translucent mode lets the Windows Mica backdrop show through the chrome (sidebar, title bar, content).
        byte sideA = translucent ? (byte)0xB8 : (byte)0xFF;
        byte baseA = translucent ? (byte)0xE0 : (byte)0xFF;

        var tokens = new Dictionary<string, Color>
        {
            ["SidebarBg"] = WithA(mantle, sideA),
            ["ContentBg"] = WithA(baseC, baseA),
            ["PanelBg"] = Mix(baseC, s0, 0.55),
            ["MenuBg"] = Mix(baseC, s0, t.IsLight ? 0.25 : 0.6),
            ["HoverBg"] = Mix(baseC, s0, t.IsLight ? 0.6 : 0.75),
            ["SelectedBg"] = Mix(baseC, accent, t.IsLight ? 0.16 : 0.17),
            ["LineBrush"] = s1,
            ["TextBrush"] = text,
            ["MutedBrush"] = sub,
            ["SectionBrush"] = Mix(sub, accent, 0.3),
            ["AccentBrush"] = accent,
            ["AccentSoftBrush"] = WithA(lav, 0x80),
            ["SelBorderBrush"] = WithA(lav, 0xD0),
            ["HoverBorderBrush"] = WithA(accent, 0x50),
            ["InputBg"] = t.IsLight ? C("#FFFFFF") : crust,
            ["TrackBrush"] = s0,
            ["ThumbBrush"] = s1,
            ["ThumbHoverBrush"] = s2,
            ["RootBorder"] = t.IsLight ? s1 : crust,
            ["SplitterBg"] = WithA(t.IsLight ? s0 : crust, sideA),
            ["DropzoneBg"] = Mix(mantle, baseC, 0.5),
            ["FaintBrush"] = s2,
            ["ToastBg"] = t.IsLight ? Mix(baseC, s0, 0.5) : s0,
        };

        var res = Application.Current.Resources;
        foreach (var kv in tokens)
        {
            var b = new SolidColorBrush(kv.Value);
            b.Freeze();
            res[kv.Key] = b;
        }
        Changed?.Invoke();
    }

    public static Brush Swatch(ThemeDef t)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(t.Base), 0.5));
        g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(t.Accent), 0.5));
        g.Freeze();
        return g;
    }

    static Color WithA(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));
}
