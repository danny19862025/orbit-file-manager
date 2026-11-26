using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Orbit;

/// <summary>
/// A short opening animation (a little over a second): the window fades and settles into place while the
/// Orbit mark in the title bar spins in and a small moon sweeps once around it. Skipped when Windows'
/// "Animation effects" are turned off.
/// </summary>
public partial class MainWindow
{
    static bool AnimationsOn => SystemParameters.ClientAreaAnimation;

    /// <summary>Called before the window first shows, so nothing flashes at full size first.</summary>
    void PrepareIntro()
    {
        if (!AnimationsOn) return;
        Root.Opacity = 0;
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

        // title mark: spin in with a little overshoot
        var logoRotate = new RotateTransform(-180);
        var logoScale = new ScaleTransform(0.5, 0.5);
        TitleLogo.RenderTransform = new TransformGroup { Children = { logoScale, logoRotate } };
        var pop = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
        var spin = new DoubleAnimation(-180, 0, TimeSpan.FromMilliseconds(900)) { BeginTime = TimeSpan.FromMilliseconds(120), EasingFunction = pop };
        spin.Completed += (_, _) => TitleLogo.RenderTransform = Transform.Identity;
        logoRotate.BeginAnimation(RotateTransform.AngleProperty, spin);
        var popIn = new DoubleAnimation(0.5, 1, TimeSpan.FromMilliseconds(800)) { BeginTime = TimeSpan.FromMilliseconds(120), EasingFunction = pop };
        logoScale.BeginAnimation(ScaleTransform.ScaleXProperty, popIn);
        logoScale.BeginAnimation(ScaleTransform.ScaleYProperty, popIn);

        // moon: one quick lap, then fades away
        var moonRotate = new RotateTransform(0);
        TitleMoon.RenderTransform = moonRotate;
        var lap = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1050)) { BeginTime = TimeSpan.FromMilliseconds(150), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } };
        moonRotate.BeginAnimation(RotateTransform.AngleProperty, lap);
        var moonFade = new DoubleAnimationUsingKeyFrames();
        moonFade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        moonFade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300))));
        moonFade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1000))));
        moonFade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1250))));
        moonFade.Completed += (_, _) => { TitleMoon.BeginAnimation(OpacityProperty, null); TitleMoon.Opacity = 0; };
        TitleMoon.BeginAnimation(OpacityProperty, moonFade);
    }
}
