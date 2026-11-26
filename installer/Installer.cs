using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace OrbitSetup;

/// <summary>
/// Installs Orbit for the current user: unpacks the self-contained build into the chosen folder, adds Start menu
/// (and optionally desktop) shortcuts, and registers it under Settings › Apps so it can be uninstalled
/// (Orbit.exe --uninstall). No administrator rights are needed.
/// </summary>
static class Installer
{
    public const string Marker = "orbit.installed";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Orbit";
    const string RepoUrl = "https://github.com/danny19862025/orbit-file-manager";

    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
    }

    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Orbit");

    /// <summary>Where Orbit is already installed, and which version, if it is.</summary>
    public static (string Dir, string Version) Existing()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
            var dir = k?.GetValue("InstallLocation") as string;
            if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "Orbit.exe")))
                return (dir, k.GetValue("DisplayVersion") as string);
        }
        catch { }
        return (null, null);
    }

    /// <summary>Running copies of Orbit started from <paramref name="dir"/>.</summary>
    public static Process[] RunningIn(string dir)
    {
        var full = Path.GetFullPath(dir).TrimEnd('\\');
        return Process.GetProcessesByName("Orbit").Where(p =>
        {
            try { return string.Equals(Path.GetDirectoryName(p.MainModule.FileName), full, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }).ToArray();
    }

    public static void CloseRunning(string dir)
    {
        foreach (var p in RunningIn(dir))
        {
            try
            {
                p.CloseMainWindow();
                if (!p.WaitForExit(6000)) { p.Kill(); p.WaitForExit(3000); }
            }
            catch { }
        }
    }

    /// <param name="progress">0–1 progress and a status line (may be null).</param>
    /// <param name="confirm">asked before anything risky; return false to stop.</param>
    public static void Install(string dir, bool desktopShortcut, Action<double, string> progress, Func<string, bool> confirm)
    {
        dir = Path.GetFullPath(dir).TrimEnd('\\');
        var root = Path.GetPathRoot(dir).TrimEnd('\\');
        if (string.Equals(root, dir, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Choose a folder, not a whole drive.");
        if (dir.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Orbit can't be installed inside the Windows folder.");

        progress?.Invoke(0, "Getting ready…");
        if (RunningIn(dir).Length > 0)
        {
            if (!confirm("Orbit is running from this folder. Close it and continue?")) throw new OperationCanceledException();
            CloseRunning(dir);
        }

        if (Directory.Exists(dir))
        {
            if (File.Exists(Path.Combine(dir, Marker)))
            {
                // an earlier install: replace it completely so no stale files are left behind
                progress?.Invoke(0.02, "Removing the previous version…");
                Directory.Delete(dir, true);
            }
            else if (Directory.EnumerateFileSystemEntries(dir).Any() &&
                     !confirm($"The folder isn't empty:\n{dir}\n\nInstall into it anyway? Files with the same names will be replaced."))
                throw new OperationCanceledException();
        }
        Directory.CreateDirectory(dir);

        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
        {
            if (s == null) throw new InvalidOperationException("This installer is incomplete — the Orbit files are missing.");
            using var zip = new ZipArchive(s, ZipArchiveMode.Read);
            long total = Math.Max(1, zip.Entries.Sum(e => e.Length)), done = 0;
            var rootWithSep = dir + "\\";
            foreach (var e in zip.Entries)
            {
                var dest = Path.GetFullPath(Path.Combine(dir, e.FullName));
                if (!dest.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The installer contains an unsafe path.");
                if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\")) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                e.ExtractToFile(dest, true);
                done += e.Length;
                progress?.Invoke(0.05 + 0.85 * done / total, "Copying " + e.Name);
            }
        }
        File.WriteAllText(Path.Combine(dir, Marker), $"Orbit {Version}\r\nInstalled {DateTime.Now:yyyy-MM-dd HH:mm}\r\n");

        progress?.Invoke(0.93, "Adding shortcuts…");
        var exe = Path.Combine(dir, "Orbit.exe");
        CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Orbit.lnk"), exe, dir);
        var desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Orbit.lnk");
        if (desktopShortcut) CreateShortcut(desktopLnk, exe, dir);

        progress?.Invoke(0.97, "Registering with Windows…");
        long kb = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024;
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "Orbit File Manager");
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", "Orbit");
            k.SetValue("DisplayIcon", exe + ",0");
            k.SetValue("InstallLocation", dir);
            k.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            k.SetValue("URLInfoAbout", RepoUrl);
            k.SetValue("HelpLink", RepoUrl + "/issues");
            k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, kb), RegistryValueKind.DWord);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        progress?.Invoke(1, "Orbit is installed.");
    }

    /// <summary>A .lnk through the Windows Script Host, which every Windows has.</summary>
    static void CreateShortcut(string lnkPath, string target, string workDir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath));
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        var shell = Activator.CreateInstance(shellType);
        try
        {
            var lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            var t = lnk.GetType();
            t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
            t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
            t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
            t.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Orbit — a fast, modern file manager" });
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(lnk);
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }
}
