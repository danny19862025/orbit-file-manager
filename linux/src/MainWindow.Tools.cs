using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace Orbit;

/// <summary>Entry points for the extra tools (disk space map, convert, locked folders) and the Trash.</summary>
public partial class MainWindow
{
    internal static string PendingSpacePath;

    internal void OpenSpace(string path)
    {
        if (string.IsNullOrEmpty(path) || IsTag(path) || IsRemote(path) || Archives.IsArc(path)) path = Home;
        if (!SafeDirExists(path)) { Toast("That folder isn't available"); return; }
        new SpaceWindow(this, path).Show();
    }

    /// <summary>Navigate to a path from another window: folders open, files are selected in their folder.</summary>
    internal void ShowInOrbit(string path)
    {
        if (Directory.Exists(path)) Navigate(path);
        else if (File.Exists(path)) Navigate(Path.GetDirectoryName(path), path);
        else { Toast("That item isn't there any more"); return; }
        // bring Orbit to the front so the result is actually visible
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        ActiveList?.Focus();
    }

    internal void ToastFromSpace(string msg) => Toast(msg);

    internal static string PendingConvertPath;
    internal static (bool Locking, string Path)? PendingLock;

    /// <summary>Tools section of the ⋯ menu.</summary>
    void AddToolItems(ContextMenu menu)
    {
        bool local = current != null && !IsTag(current.Path) && !IsRemote(current.Path) && !Archives.IsArc(current.Path);
        var sel = Selected().Where(f => !f.IsRemote && !f.InArchive).ToList();
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Analyze disk space…", "", () => OpenSpace(current?.Path), null, local));
        menu.Items.Add(MI("Convert selected…", "", () => OpenConvert(sel), null, ConvertibleGroup(sel).Count > 0));
    }

    /// <summary>The selected files that can be converted together (same kind as the first convertible one).</summary>
    static List<string> ConvertibleGroup(IEnumerable<FileItem> sel)
    {
        var files = sel.Where(f => !f.IsDir && !f.IsRemote && !f.InArchive).Select(f => f.FullPath).ToList();
        var kind = files.Select(Converter.KindOf).FirstOrDefault(k => k != null);
        return kind == null ? new List<string>() : files.Where(p => Converter.KindOf(p) == kind).ToList();
    }

    internal void OpenConvert(IEnumerable<FileItem> sel) => OpenConvertPaths(ConvertibleGroup(sel));

    internal void OpenConvertPaths(List<string> paths)
    {
        if (paths.Count == 0) { Toast("Select pictures, videos or audio files to convert"); return; }
        new ConvertWindow(this, paths).Show(this);
    }

    /// <summary>File tools for the right-click menu (local files).</summary>
    void AddFileToolItems(ContextMenu menu, List<FileItem> localFiles)
    {
        var group = ConvertibleGroup(localFiles);
        if (group.Count > 0)
            menu.Items.Add(MI(group.Count == 1 ? "Convert…" : $"Convert {group.Count} files…", "", () => OpenConvertPaths(group)));
        if (localFiles.Count == 1)
        {
            var f = localFiles[0];
            if (f.IsDir) menu.Items.Add(MI("Lock with password…", "", () => OpenLock(f.FullPath, true)));
            else if (FolderLock.IsLockFile(f.FullPath)) menu.Items.Add(MI("Unlock…", "", () => OpenLock(f.FullPath, false)));
        }
    }

    // ---------- locked folders ----------
    internal void OpenLock(string path, bool locking)
    {
        if (locking)
        {
            var full = Path.GetFullPath(path).TrimEnd('/');
            if (full.Length == 0) full = "/";
            var protectedRoots = new[] { "/", "/home", "/usr", "/etc", "/var", "/boot", "/opt", "/root", "/srv", "/bin", "/sbin", "/lib", "/lib64", "/tmp", Home };
            bool protectedDir = protectedRoots.Contains(full)
                || full.StartsWith("/usr/") || full.StartsWith("/etc/") || full.StartsWith("/boot/") || full.StartsWith("/proc/") || full.StartsWith("/sys/")
                || Drives.Any(d => Same(d.Path, full));
            if (protectedDir) { Toast("Orbit won't lock a drive, system folder or your whole home folder"); return; }
            if (!Directory.Exists(full)) { Toast("That folder isn't available"); return; }
        }
        else if (!File.Exists(path)) { Toast("That file isn't available"); return; }
        new LockWindow(this, path, locking).ShowDialog(this);
    }

    void AddDriveMenuItems(ContextMenu menu, SideItem s)
    {
        if (s.Kind != "drive") return;
        menu.Items.Add(MI("Analyze disk space", "", () => OpenSpace(s.Path)));
        if (s.Removable || s.Glyph == "" || s.Glyph == "")
            menu.Items.Add(MI("Eject", "", () => EjectDrive(s)));
    }

    async void EjectDrive(SideItem s)
    {
        if (current?.Path != null && (Same(current.Path, s.Path) || current.Path.StartsWith(s.Path.TrimEnd('/') + "/", StringComparison.Ordinal))) Navigate(Home);
        StopWatcher();
        var err = await Orbit.Drives.Eject(s.Device, s.Path);
        RefreshDrives();
        Toast(err == null ? $"Ejected {s.Name} — it's safe to remove" : $"Couldn't eject {s.Name}: {err}");
    }

    async void MountIso(string path)
    {
        Toast($"Mounting “{Path.GetFileName(path)}”…");
        var (mount, err) = await Orbit.Drives.MountImage(path);
        if (mount != null)
        {
            RefreshDrives();
            Navigate(mount);
            Toast("Mounted — right-click the drive in the sidebar to eject it");
        }
        else Toast("Couldn't mount: " + err);
    }

    // =====================================================================
    // Trash
    // =====================================================================
    const string RecycledHint = "Restore the item first (right-click › Restore) to open or change it";

    void ShowRecycleMenu(ListBox list, Control anchor, List<FileItem> sel)
    {
        var menu = new ContextMenu();
        if (sel.Count == 0)
        {
            var (count, bytes) = RecycleBin.Totals();
            menu.Items.Add(MI(count == 0 ? "The Trash is empty" : $"{count:N0} item{(count == 1 ? "" : "s")} · {Fmt.Size(bytes)}", "", null, null, false));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Restore all items", "", () => RestoreRecycled(view.ToList()), null, view.Count > 0));
            menu.Items.Add(MI("Empty Trash…", "", EmptyRecycleBin, null, count > 0));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Refresh", "", () => LoadCurrent(keepSelection: true), "F5"));
            menu.Items.Add(MI("Open in File Manager", "", () => Try(() => Shell.Open("trash:///"))));
        }
        else
        {
            menu.Items.Add(MI("Restore", "", () => RestoreRecycled(sel)));
            menu.Items.Add(MI("Preview", "", TogglePreview, "Space", sel.Count == 1 && !sel[0].IsDir));
            menu.Items.Add(new Separator());
            if (sel.Count == 1)
                menu.Items.Add(MI("Show original folder", "", () =>
                {
                    var dir = Path.GetDirectoryName(sel[0].OriginalPath);
                    if (SafeDirExists(dir)) Navigate(dir); else Toast("The original folder doesn't exist any more — Restore recreates it");
                }));
            menu.Items.Add(MI("Delete permanently…", "", () => DeleteRecycled(sel), "Delete"));
        }
        OpenMenu(menu, anchor ?? list, anchor != null);
    }

    void AddRecycleSideItems(ContextMenu menu, SideItem s)
    {
        menu.Items.Add(MI("Open", "", () => OpenSide(s, false)));
        menu.Items.Add(MI("Open in new tab", "", () => OpenSide(s, true)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Empty Trash…", "", EmptyRecycleBin, null, RecycleBin.Totals().Count > 0));
        menu.Items.Add(MI("Open in File Manager", "", () => Try(() => Shell.Open("trash:///"))));
    }

    async void RestoreRecycled(List<FileItem> sel)
    {
        if (sel.Count == 0) return;
        var (restored, errors) = await Task.Run(() => RecycleBin.Restore(sel));
        if (restored.Count > 0)
            Toast(restored.Count == 1 ? $"Restored “{Path.GetFileName(restored[0])}” to {Path.GetDirectoryName(restored[0])}" : $"Restored {restored.Count} items");
        if (errors.Count > 0)
            await Dialog.Info(this, "Restore", "Some items couldn't be restored:\n\n" + string.Join("\n", errors.Take(8)), warning: true);
        LoadCurrent(keepSelection: false);
    }

    async void DeleteRecycled(List<FileItem> sel)
    {
        sel = sel.Where(f => f.IsRecycled).ToList();
        if (sel.Count == 0) return;
        var msg = sel.Count == 1 ? $"Permanently delete “{sel[0].Name}”?" : $"Permanently delete these {sel.Count} items?";
        if (!await Dialog.Ask(this, "Delete permanently", msg + "\n\nThey'll be gone for good — this can't be undone.", "Delete", "Cancel", warning: true, yesIsDefault: false)) return;
        var errors = await Task.Run(() => RecycleBin.DeleteForever(sel));
        if (errors.Count > 0)
            await Dialog.Info(this, "Delete permanently", "Some items couldn't be deleted:\n\n" + string.Join("\n", errors.Take(8)), warning: true);
        else Toast(sel.Count == 1 ? $"Deleted “{sel[0].Name}” permanently" : $"Deleted {sel.Count} items permanently");
        LoadCurrent(keepSelection: false);
    }

    async void EmptyRecycleBin()
    {
        var (count, bytes) = RecycleBin.Totals();
        if (count == 0) return;
        if (!await Dialog.Ask(this, "Empty Trash", $"Permanently delete all {count:N0} item{(count == 1 ? "" : "s")} in the Trash ({Fmt.Size(bytes)})?\n\nThis can't be undone.",
                "Empty Trash", "Cancel", warning: true, yesIsDefault: false)) return;
        var errors = await Task.Run(RecycleBin.Empty);
        if (errors.Count > 0) Toast($"{errors.Count} item(s) couldn't be deleted: {errors[0]}");
        else Toast("The Trash is empty");
        if (RecycleBin.Is(current?.Path)) LoadCurrent(keepSelection: false);
    }

    /// <summary>Files dropped on the sidebar's Trash are moved to it.</summary>
    void RecycleDropped(IEnumerable<string> files)
    {
        var list = files.Where(p => !IsRemote(p) && !Archives.IsArc(p)).ToList();
        if (list.Count == 0) return;
        FileOps.Delete(list, true, this, () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Toast(list.Count == 1 ? $"Moved “{Path.GetFileName(list[0])}” to the Trash" : $"Moved {list.Count} items to the Trash");
            if (RecycleBin.Is(current?.Path)) LoadCurrent(keepSelection: true);
        }));
    }
}
