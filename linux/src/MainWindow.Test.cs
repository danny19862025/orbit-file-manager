using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Orbit;

/// <summary>
/// Scripted runs for automated checks (tools/nested-test.sh): only active when ORBIT_TEST_SCRIPT names a file of commands.
/// Each line is one command, e.g. "nav ~/Pictures", "view details", "preview", "shot grid". Screenshots go to ORBIT_TEST_OUT.
/// </summary>
public partial class MainWindow
{
    static string TestOut => Environment.GetEnvironmentVariable("ORBIT_TEST_OUT") ?? "/tmp";

    internal async void RunTestScript()
    {
        var file = Environment.GetEnvironmentVariable("ORBIT_TEST_SCRIPT");
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) return;
        var log = Path.Combine(TestOut, "orbit-test.log");
        void Log(string s) { try { File.AppendAllText(log, $"[{DateTime.Now:HH:mm:ss.fff}] {s}\n"); } catch { } }
        Log("script start");
        await Task.Delay(2500);
        Log("after delay");
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var sp = line.IndexOf(' ');
            var cmd = sp < 0 ? line : line[..sp];
            var arg = sp < 0 ? "" : line[(sp + 1)..].Trim().Replace("~", Home);
            Log(line);
            try
            {
                switch (cmd)
                {
                    case "wait": await Task.Delay(int.Parse(arg)); continue;
                    case "nav": Navigate(arg); break;
                    case "view": SetView(arg); break;
                    case "select":
                        var f = view.FirstOrDefault(x => x.Name == arg) ?? treeRows.FirstOrDefault(x => x.Name == arg);
                        if (f != null) { ActiveList.SelectedItem = f; ActiveList.ScrollIntoView(f); } else Log("  not found");
                        break;
                    case "selectall": ActiveList.SelectAll(); break;
                    case "preview": OpenPreview(); break;
                    case "closepreview": ClosePreview(); break;
                    case "find": FindBox.Text = arg; OpenFind(); break;
                    case "closefind": CloseFind(); break;
                    case "settings": OpenSettings(arg.Length == 0 ? null : arg); break;
                    case "props": ShowProperties(); break;
                    case "theme": ApplyTheme(arg, S.Translucent); break;
                    case "glass": ApplyTheme(S.Theme, arg == "on"); break;
                    case "pack": SetIconPack(arg); break;
                    case "split": OpenSplit(arg); break;
                    case "unsplit": CloseSplit(); break;
                    case "space": OpenSpace(arg); break;
                    case "convert": OpenConvertPaths(new List<string> { arg }); break;
                    case "lock": OpenLock(arg, true); break;
                    case "unlockfile": OpenLock(arg, false); break;
                    case "connect": ShowConnectDialog(null); break;
                    case "openwith": OpenWith(arg); break;
                    case "newtab": AddTab(arg.Length == 0 ? Home : arg, true); break;
                    case "newfolder": NewFolder(); break;
                    case "toggle-tree":
                        var tf = treeRows.FirstOrDefault(x => x.Name == arg);
                        if (tf != null) ToggleTreeRow(tf);
                        break;
                    case "key":
                        var parts = arg.Split('+');
                        var key = Enum.Parse<Key>(parts[^1] == "Backspace" ? "Back" : parts[^1], true);
                        KeyModifiers m = 0;
                        foreach (var p in parts[..^1]) m |= p switch { "Ctrl" => KeyModifiers.Control, "Shift" => KeyModifiers.Shift, "Alt" => KeyModifiers.Alt, _ => 0 };
                        if (!RunShortcut(key, m, false, true)) Log("  no shortcut");
                        break;
                    case "menu":
                        switch (arg)
                        {
                            case "new": CmdNew_Click(CmdNew, null); break;
                            case "view": CmdView_Click(CmdView, null); break;
                            case "filter": CmdFilter_Click(CmdFilter, null); break;
                            case "more": CmdMore_Click(CmdMore, null); break;
                            case "sort": CmdSort_Click(CmdSort, null); break;
                            default: ShowItemMenu(ActiveList, ActiveList.ContainerFromItem(ActiveList.SelectedItem ?? new object()) as Control ?? ActiveList); break;
                        }
                        break;
                    case "escape":
                        foreach (var w in OwnedOrOpenWindows()) w.Close();
                        break;
                    case "closewin":
                        foreach (var w in OwnedOrOpenWindows()) w.Close();
                        break;
                    case "toast": Toast(arg); break;
                    case "renameto":
                        BeginRenameSelected();
                        if (Selected().FirstOrDefault() is { } rf) { rf.EditName = arg; CommitRename(rf); }
                        break;
                    case "exists": Log(File.Exists(arg) || Directory.Exists(arg) ? "  PASS exists " + arg : "  FAIL missing " + arg); break;
                    case "missing": Log(!File.Exists(arg) && !Directory.Exists(arg) ? "  PASS gone " + arg : "  FAIL still there " + arg); break;
                    case "restoreall": RestoreRecycled(view.ToList()); break;
                    case "compress": CompressSelection(Selected()); break;
                    case "extract": ExtractArchiveFiles(Selected(), true); break;
                    case "locktest":
                        var lf = await Task.Run(() => FolderLock.Lock(arg, "correct horse battery", null, default));
                        try { FolderLock.Unlock(lf, "wrong password!", null, default); Log("  FAIL wrong password accepted"); }
                        catch (FolderLock.WrongPasswordException) { Log("  PASS wrong password refused"); }
                        var un = await Task.Run(() => FolderLock.Unlock(lf, "correct horse battery", null, default));
                        var a1 = Directory.GetFiles(arg, "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(arg, x) + ":" + new FileInfo(x).Length).OrderBy(x => x);
                        var a2 = Directory.GetFiles(un, "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(un, x) + ":" + new FileInfo(x).Length).OrderBy(x => x);
                        Log(a1.SequenceEqual(a2) && File.ReadAllBytes(Directory.GetFiles(arg, "*", SearchOption.AllDirectories)[0]).SequenceEqual(File.ReadAllBytes(Path.Combine(un, Path.GetRelativePath(arg, Directory.GetFiles(arg, "*", SearchOption.AllDirectories)[0]))))
                            ? $"  PASS lock round trip ({lf} → {un})" : "  FAIL contents differ");
                        break;
                    case "shot": Snapshot(this, arg); break;
                    case "shotwin":
                        var top = OwnedOrOpenWindows().LastOrDefault();
                        if (top != null) Snapshot(top, arg); else Log("  no window");
                        break;
                    case "kwinshot":
                        var py = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "tools", "screenshot.py");
                        var script = Environment.GetEnvironmentVariable("ORBIT_SCREENSHOT_PY") ?? py;
                        var psi = new ProcessStartInfo("python3") { UseShellExecute = false };
                        psi.ArgumentList.Add(script);
                        psi.ArgumentList.Add(Path.Combine(TestOut, arg + ".png"));
                        Process.Start(psi)?.WaitForExit(10000);
                        break;
                    case "quit":
                        Close();
                        return;
                    default: Log("  unknown command"); break;
                }
            }
            catch (Exception ex) { Log("  error: " + ex); }
            await Task.Delay(900);
        }
    }

    static IEnumerable<Window> OwnedOrOpenWindows() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.Where(w => w is not MainWindow).ToList() ?? new List<Window>();

    static void Snapshot(Window w, string name)
    {
        var size = new PixelSize((int)(w.Bounds.Width * w.RenderScaling), (int)(w.Bounds.Height * w.RenderScaling));
        using var rtb = new RenderTargetBitmap(size, new Vector(96 * w.RenderScaling, 96 * w.RenderScaling));
        rtb.Render(w);
        rtb.Save(Path.Combine(TestOut, name + ".png"));
    }
}
