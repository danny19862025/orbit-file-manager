using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace Orbit;

/// <summary>
/// "Orbit.exe --uninstall", run by Windows' Settings › Apps for copies installed with OrbitSetup.exe.
/// Removes the shortcuts and the Apps entry, then deletes the install folder once Orbit has exited.
/// It only ever deletes a folder that the installer created and marked, never a portable or development copy.
/// </summary>
public static class Uninstaller
{
    public const string Marker = "orbit.installed";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Orbit";

    public static void Run()
    {
        var dir = AppContext.BaseDirectory.TrimEnd('\\');
        var exe = Path.Combine(dir, "Orbit.exe");
        if (!File.Exists(Path.Combine(dir, Marker)))
        {
            MessageBox.Show($"This copy of Orbit wasn't installed with the Orbit installer, so there's nothing to uninstall.\n\nTo remove it, close Orbit and delete its folder:\n{dir}",
                "Uninstall Orbit", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show($"Remove Orbit from this PC?\n\nThis deletes the program folder:\n{dir}",
                "Uninstall Orbit", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        var settingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Orbit");
        bool removeSettings = Directory.Exists(settingsDir) &&
            MessageBox.Show("Also delete your Orbit settings (tabs, bookmarks, tags, saved servers)?\n\nChoose No to keep them for a future install.",
                "Uninstall Orbit", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

        // close any other Orbit windows running from this folder
        var me = Process.GetCurrentProcess().Id;
        foreach (var p in Process.GetProcessesByName("Orbit").Where(p => p.Id != me))
        {
            try
            {
                if (!string.Equals(Path.GetDirectoryName(p.MainModule?.FileName), dir, StringComparison.OrdinalIgnoreCase)) continue;
                p.CloseMainWindow();
                if (!p.WaitForExit(5000)) p.Kill();
            }
            catch { }
        }

        foreach (var lnk in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Orbit.lnk"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Orbit.lnk"),
                 })
            try { if (File.Exists(lnk)) File.Delete(lnk); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }

        // the folder can only go once this process has exited: a tiny hidden script waits, then removes it and itself
        var script = Path.Combine(Path.GetTempPath(), $"orbit-uninstall-{Guid.NewGuid():N}.cmd");
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine(":wait");
        sb.AppendLine($"tasklist /FI \"PID eq {me}\" 2>nul | find \"{me}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)");
        sb.AppendLine($"rd /s /q \"{dir}\"");
        if (removeSettings) sb.AppendLine($"rd /s /q \"{settingsDir}\"");
        sb.AppendLine("del \"%~f0\"");
        File.WriteAllText(script, sb.ToString(), Encoding.Default);
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        MessageBox.Show("Orbit has been removed." + (removeSettings ? "" : "\n\nYour settings were kept in " + settingsDir), "Uninstall Orbit", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
