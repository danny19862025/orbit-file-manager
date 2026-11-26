using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Orbit;

/// <summary>
/// The title-bar icon (the same artwork as Orbit.exe's icon, see tools\make-icon.ps1) and the short opening animation:
/// the window fades and settles into place while the moon travels once round the planet's ring — behind the planet on
/// the far side, in front on the near side — and comes to rest where it sits in the app icon.
/// Skipped when Windows' "Animation effects" are turned off.
/// </summary>
public partial class MainWindow
{
    static bool AnimationsOn => SystemParameters.ClientAreaAnimation;

    // icon geometry, in the 256-unit space the .ico is drawn in
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
        var c = (Color)ColorConverter.ConvertFromString(hex);
        return Color.FromArgb(a, c.R, c.G, c.B);
    }

    void BuildTitleIcon()
    {
        double k = TitleIcon.Width / 256;
        var art = new Canvas { Width = 256, Height = 256, RenderTransform = new ScaleTransform(k, k) };
        TitleIcon.Children.Clear();
        TitleIcon.Children.Add(art);

        // tile: rounded square, mauve → blue, with a soft highlight
        var tile = new Rectangle { Width = 240, Height = 240, RadiusX = 60, RadiusY = 60,
            Fill = new LinearGradientBrush(C("#CBA6F7"), C("#89B4FA"), new Point(0, 0), new Point(1, 1)) };
        Canvas.SetLeft(tile, 8); Canvas.SetTop(tile, 8);
        var shine = new Rectangle { Width = 240, Height = 120, RadiusX = 60, RadiusY = 60,
            Fill = new LinearGradientBrush(C("#FFFFFF", 60), C("#FFFFFF", 0), 90) };
        Canvas.SetLeft(shine, 8); Canvas.SetTop(shine, 8);
        art.Children.Add(tile);
        art.Children.Add(shine);

        Path Ring(string data, int z)
        {
            var p = new Path
            {
                Data = Geometry.Parse(data), Stroke = new SolidColorBrush(C("#FFFFFF", 235)), StrokeThickness = 18,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                RenderTransform = new RotateTransform(RingTilt, Cx, Cy)
            };
            Panel.SetZIndex(p, z);
            return p;
        }
        // far half of the ring (behind the planet) and near half (in front of it)
        art.Children.Add(Ring($"M {Cx - RingRx},{Cy} A {RingRx},{RingRy} 0 0 1 {Cx + RingRx},{Cy}", 1));
        var planet = new Ellipse { Width = 92, Height = 92, Fill = new RadialGradientBrush(C("#313244"), C("#11111B")) { GradientOrigin = new Point(0.35, 0.3) } };
        Canvas.SetLeft(planet, Cx - 46); Canvas.SetTop(planet, Cy - 46);
        Panel.SetZIndex(planet, 3);
        art.Children.Add(planet);
        art.Children.Add(Ring($"M {Cx + RingRx},{Cy} A {RingRx},{RingRy} 0 0 1 {Cx - RingRx},{Cy}", 4));

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
        Panel.SetZIndex(titleMoon, Math.Sin(t) < 0 ? 2 : 5); // far half: between the far ring and the planet
    }

    void PlayIntro()
    {
        if (!AnimationsOn) { Root.Opacity = 1; return; }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // window: fade in and settle from 95 %
        var scale = new ScaleTransform(0.95, 0.95);
        Root.RenderTransformOrigin = new Point(0.5, 0.5);
        Root.RenderTransform = scale;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease };
        fade.Completed += (_, _) => { Root.BeginAnimation(OpacityProperty, null); Root.Opacity = 1; };
        Root.BeginAnimation(OpacityProperty, fade);
        var grow = new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(620)) { EasingFunction = ease };
        grow.Completed += (_, _) => Root.RenderTransform = Transform.Identity; // nothing left running afterwards
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        // icon: a small pop-in
        var iconScale = new ScaleTransform(0.6, 0.6);
        TitleIcon.RenderTransform = iconScale;
        var pop = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(520)) { BeginTime = TimeSpan.FromMilliseconds(80), EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
        pop.Completed += (_, _) => TitleIcon.RenderTransform = Transform.Identity;
        iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

        // moon: exactly one lap round the ring, easing in and out, ending where it started
        const double delayMs = 200, lapMs = 2000;
        var clock = Stopwatch.StartNew();
        EventHandler tick = null;
        tick = (_, _) =>
        {
            double p = Math.Clamp((clock.Elapsed.TotalMilliseconds - delayMs) / lapMs, 0, 1);
            double eased = p < 0.5 ? 4 * p * p * p : 1 - Math.Pow(-2 * p + 2, 3) / 2;
            PlaceMoon(MoonRest + eased * 2 * Math.PI);
            if (p >= 1)
            {
                CompositionTarget.Rendering -= tick;
                PlaceMoon(MoonRest);
            }
        };
        CompositionTarget.Rendering += tick;
    }
}
