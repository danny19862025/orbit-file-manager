using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace Orbit;

/// <summary>
/// The title-bar icon (the same artwork as the app icon) and the short opening animation: the window fades and settles
/// into place while the moon travels once round the planet's ring — behind the planet on the far side, in front on the
/// near side — and comes to rest where it sits in the app icon. Skipped when the desktop's animations are turned off.
/// </summary>
public partial class MainWindow
{
    static bool? animationsOn;
    static bool AnimationsOn => animationsOn ??= DetectAnimations();

    static bool DetectAnimations()
    {
        try
        {
            var kde = System.IO.Path.Combine(Xdg.Config, "kdeglobals");
            if (File.Exists(kde))
                foreach (var line in File.ReadLines(kde))
                    if (line.StartsWith("AnimationDurationFactor=") && double.TryParse(line[24..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f))
                        return f > 0;
            if ((Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").Contains("GNOME", StringComparison.OrdinalIgnoreCase))
            {
                var psi = new ProcessStartInfo("gsettings", "get org.gnome.desktop.interface enable-animations") { RedirectStandardOutput = true, UseShellExecute = false };
                using var p = Process.Start(psi)!;
                var o = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(1000);
                if (o == "false") return false;
            }
        }
        catch { }
        return true;
    }

    // icon geometry, in the 256-unit space the icon is drawn in
    const double Cx = 128, Cy = 128, RingRx = 100, RingRy = 38, RingTilt = -24;
    const double MoonRest = Math.PI * 0.78; // where the moon sits when still
    const double MoonR = 22;
    Ellipse titleMoon;

    /// <summary>Called before the window first shows, so nothing flashes at full size first.</summary>
    void PrepareIntro()
    {
        BuildTitleIcon();
        if (AnimationsOn) Root.Opacity = 0;
    }

    static Color C(string hex, byte a = 255)
    {
        var c = Color.Parse(hex);
        return Color.FromArgb(a, c.R, c.G, c.B);
    }

    void BuildTitleIcon()
    {
        double k = TitleIcon.Width / 256;
        var art = new Canvas { Width = 256, Height = 256, RenderTransform = new ScaleTransform(k, k), RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative) };
        TitleIcon.Children.Clear();
        TitleIcon.Children.Add(art);

        // tile: rounded square, mauve → blue, with a soft highlight
        var tile = new Rectangle
        {
            Width = 240, Height = 240, RadiusX = 60, RadiusY = 60,
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(C("#CBA6F7"), 0), new GradientStop(C("#89B4FA"), 1) }
            }
        };
        Canvas.SetLeft(tile, 8); Canvas.SetTop(tile, 8);
        var shine = new Rectangle
        {
            Width = 240, Height = 120, RadiusX = 60, RadiusY = 60,
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(C("#FFFFFF", 60), 0), new GradientStop(C("#FFFFFF", 0), 1) }
            }
        };
        Canvas.SetLeft(shine, 8); Canvas.SetTop(shine, 8);
        art.Children.Add(tile);
        art.Children.Add(shine);

        Avalonia.Controls.Shapes.Path Ring(string data, int z)
        {
            var p = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(data), Stroke = new SolidColorBrush(C("#FFFFFF", 235)), StrokeThickness = 18,
                StrokeLineCap = PenLineCap.Round,
                RenderTransform = new RotateTransform(RingTilt, Cx, Cy),
                RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute)
            };
            p.ZIndex = z;
            return p;
        }
        string F(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // far half of the ring (behind the planet) and near half (in front of it)
        art.Children.Add(Ring($"M {F(Cx - RingRx)},{F(Cy)} A {F(RingRx)},{F(RingRy)} 0 0 1 {F(Cx + RingRx)},{F(Cy)}", 1));
        var planet = new Ellipse
        {
            Width = 92, Height = 92,
            Fill = new RadialGradientBrush
            {
                GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
                GradientStops = { new GradientStop(C("#313244"), 0), new GradientStop(C("#11111B"), 1) }
            },
            ZIndex = 3
        };
        Canvas.SetLeft(planet, Cx - 46); Canvas.SetTop(planet, Cy - 46);
        art.Children.Add(planet);
        art.Children.Add(Ring($"M {F(Cx + RingRx)},{F(Cy)} A {F(RingRx)},{F(RingRy)} 0 0 1 {F(Cx - RingRx)},{F(Cy)}", 4));

        titleMoon = new Ellipse { Width = MoonR * 2, Height = MoonR * 2, Fill = new SolidColorBrush(C("#FAB387")), Stroke = new SolidColorBrush(C("#1E1E2E")), StrokeThickness = 8 };
        art.Children.Add(titleMoon);
        PlaceMoon(MoonRest);
    }

    /// <summary>Puts the moon at angle <paramref name="t"/> on the tilted ring, behind the planet on the far half.</summary>
    void PlaceMoon(double t)
    {
        double dx = RingRx * Math.Cos(t), dy = RingRy * Math.Sin(t);
        double a = RingTilt * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        double x = Cx + dx * cos - dy * sin, y = Cy + dx * sin + dy * cos;
        Canvas.SetLeft(titleMoon, x - MoonR);
        Canvas.SetTop(titleMoon, y - MoonR);
        titleMoon.ZIndex = Math.Sin(t) < 0 ? 2 : 5; // far half: between the far ring and the planet
    }

    void PlayIntro()
    {
        if (!AnimationsOn) { Root.Opacity = 1; return; }
        var clock = Stopwatch.StartNew();
        var scale = new ScaleTransform(0.95, 0.95);
        Root.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        Root.RenderTransform = scale;
        var iconScale = new ScaleTransform(0.6, 0.6);
        TitleIcon.RenderTransform = iconScale;

        static double EaseOut(double p) => 1 - Math.Pow(1 - p, 3);
        static double BackOut(double p) { const double s = 0.5 * 1.70158; p -= 1; return p * p * ((s + 1) * p + s) + 1; }

        void Frame(TimeSpan _)
        {
            double ms = clock.Elapsed.TotalMilliseconds;
            // window: fade in and settle from 95 %
            Root.Opacity = EaseOut(Math.Clamp(ms / 420, 0, 1));
            double g = 0.95 + 0.05 * EaseOut(Math.Clamp(ms / 620, 0, 1));
            scale.ScaleX = scale.ScaleY = g;
            // icon: a small pop-in
            double ip = Math.Clamp((ms - 80) / 520, 0, 1);
            iconScale.ScaleX = iconScale.ScaleY = 0.6 + 0.4 * BackOut(ip);
            // moon: exactly one lap round the ring, easing in and out, ending where it started
            const double delayMs = 200, lapMs = 2000;
            double p = Math.Clamp((ms - delayMs) / lapMs, 0, 1);
            double eased = p < 0.5 ? 4 * p * p * p : 1 - Math.Pow(-2 * p + 2, 3) / 2;
            PlaceMoon(MoonRest + eased * 2 * Math.PI);
            if (p >= 1 && ms > 620)
            {
                Root.Opacity = 1;
                Root.RenderTransform = null;
                TitleIcon.RenderTransform = null;
                PlaceMoon(MoonRest);
                return;
            }
            RequestAnimationFrame(Frame);
        }
        RequestAnimationFrame(Frame);
    }
}
