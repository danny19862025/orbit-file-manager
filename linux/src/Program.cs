using System;
using Avalonia;

namespace Orbit;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        App.Args = args;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => { if (a.ExceptionObject is Exception ex) AppSettings.LogError(ex); };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, a) => { AppSettings.LogError(a.Exception); a.SetObserved(); };
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new X11PlatformOptions { WmClass = "orbit", EnableIme = true })
            .LogToTrace();
}
