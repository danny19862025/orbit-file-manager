using System;
using System.Threading.Tasks;
using System.Windows;

namespace Orbit;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, a) =>
        {
            AppSettings.LogError(a.Exception);
            a.Handled = true;
            (Current?.MainWindow as Orbit.MainWindow)?.Toast("Something went wrong: " + a.Exception.Message);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => { if (a.ExceptionObject is Exception ex) AppSettings.LogError(ex); };
        TaskScheduler.UnobservedTaskException += (_, a) => { AppSettings.LogError(a.Exception); a.SetObserved(); };

        AppSettings.Load();
        Themes.Apply(AppSettings.Current.Theme, AppSettings.Current.Translucent ? AppSettings.Current.Transparency : 0);
        Glass.Register(); // Settings and dialogs share the main window's glass
        IconService.Init(Dispatcher);

        // Orbit.exe [folder or file]           — open there (a file is selected in its folder)
        // Orbit.exe --preview <file or folder>  — open there and show the quick preview
        string start = null, preview = null;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--preview" && i + 1 < e.Args.Length) preview = e.Args[++i];
            else if (e.Args[i] == "--space" && i + 1 < e.Args.Length) Orbit.MainWindow.PendingSpacePath = e.Args[++i];
            else if (e.Args[i] == "--convert" && i + 1 < e.Args.Length) Orbit.MainWindow.PendingConvertPath = e.Args[++i];
            else if ((e.Args[i] == "--lock" || e.Args[i] == "--unlock") && i + 1 < e.Args.Length) { Orbit.MainWindow.PendingLock = (e.Args[i] == "--lock", e.Args[i + 1]); i++; }
            else start ??= e.Args[i];
        }
        var w = new Orbit.MainWindow(preview ?? start, preview);
        MainWindow = w;
        w.Show();
    }
}
