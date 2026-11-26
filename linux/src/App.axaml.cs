using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Orbit;

public partial class App : Application
{
    public static string[] Args = Array.Empty<string>();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.UnhandledException += (_, a) =>
            {
                AppSettings.LogError(a.Exception);
                a.Handled = true;
                (desktop.MainWindow as MainWindow)?.Toast("Something went wrong: " + a.Exception.Message);
            };

            AppSettings.Load();
            Themes.Apply(AppSettings.Current.Theme, AppSettings.Current.Translucent ? AppSettings.Current.Transparency : 0);
            IconService.Init();

            // orbit [folder or file]            — open there (a file is selected in its folder)
            // orbit --preview <file or folder>  — open there and show the quick preview
            string start = null, preview = null;
            var args = Args;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--preview" && i + 1 < args.Length) preview = args[++i];
                else if (args[i] == "--space" && i + 1 < args.Length) MainWindow.PendingSpacePath = args[++i];
                else if (args[i] == "--convert" && i + 1 < args.Length) MainWindow.PendingConvertPath = args[++i];
                else if ((args[i] == "--lock" || args[i] == "--unlock") && i + 1 < args.Length) { MainWindow.PendingLock = (args[i] == "--lock", args[i + 1]); i++; }
                else if (args[i].StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(args[i], UriKind.Absolute, out var u)) start ??= u.LocalPath;
                else start ??= args[i];
            }
            var w = new MainWindow(preview ?? start, preview);
            desktop.MainWindow = w;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
