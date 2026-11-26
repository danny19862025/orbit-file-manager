using System;
using System.Linq;
using System.Windows.Forms;

namespace OrbitSetup;

static class Program
{
    /// <summary>
    /// OrbitSetup.exe                 — the installer window
    /// OrbitSetup.exe /quiet [/dir=…]  — install without a window (no desktop shortcut, doesn't launch Orbit)
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("/quiet", StringComparison.OrdinalIgnoreCase) || a.Equals("--quiet", StringComparison.OrdinalIgnoreCase)))
        {
            var dir = args.FirstOrDefault(a => a.StartsWith("/dir=", StringComparison.OrdinalIgnoreCase))?.Substring(5).Trim('"') ?? Installer.DefaultDir;
            try
            {
                Installer.Install(dir, desktopShortcut: args.Any(a => a.Equals("/desktop", StringComparison.OrdinalIgnoreCase)), progress: null, confirm: _ => true);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm());
        return 0;
    }
}
