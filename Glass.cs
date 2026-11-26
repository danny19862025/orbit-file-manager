using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace Orbit;

/// <summary>
/// Gives Orbit's other windows (Settings, Properties, Connect, Convert, Lock, Disk space…) the same translucent glass and
/// transparency as the main window. Their backgrounds already use the theme's ContentBg brush, which carries the chosen
/// transparency, so this only has to open the window up to the Windows acrylic backdrop and keep it in step.
/// </summary>
public static class Glass
{
    /// <summary>Hooks every window as it loads. Called once at startup.</summary>
    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is Window w && w is not MainWindow) Attach(w);
        }));
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> attached = new();

    static void Attach(Window w)
    {
        if (attached.TryGetValue(w, out _)) return;
        attached.Add(w, null);
        Apply(w);
        Action changed = () => w.Dispatcher.BeginInvoke(() => Apply(w));
        Themes.Changed += changed;
        w.Closed += (_, _) => Themes.Changed -= changed;
    }

    public static void Apply(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            bool on = AppSettings.Current.Translucent;
            int dark = Themes.Current.IsLight ? 0 : 1;
            Native.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // dark title / border

            // keep each window's own caption settings, only switch the glass frame
            if (WindowChrome.GetWindowChrome(w) is { } old)
            {
                var glass = on ? new Thickness(-1) : new Thickness(0);
                if (old.GlassFrameThickness != glass)
                    WindowChrome.SetWindowChrome(w, new WindowChrome
                    {
                        CaptionHeight = old.CaptionHeight, ResizeBorderThickness = old.ResizeBorderThickness, CornerRadius = old.CornerRadius,
                        UseAeroCaptionButtons = old.UseAeroCaptionButtons, NonClientFrameEdges = old.NonClientFrameEdges, GlassFrameThickness = glass
                    });
            }

            var src = HwndSource.FromHwnd(hwnd);
            int backdrop = on ? 3 : 1; // acrylic / none
            Native.DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int));
            if (src?.CompositionTarget != null)
                src.CompositionTarget.BackgroundColor = on ? Colors.Transparent
                    : w.TryFindResource("ContentBg") is SolidColorBrush b ? Color.FromRgb(b.Color.R, b.Color.G, b.Color.B) : Colors.Black;
        }
        catch { }
    }
}
