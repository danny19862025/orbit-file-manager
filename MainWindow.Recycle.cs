using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace Orbit;

/// <summary>The Recycle Bin shown inside Orbit: restore, delete for good, empty.</summary>
public partial class MainWindow
{
    const string RecycledHint = "Restore the item first (right-click › Restore) to open or change it";

    void ShowRecycleMenu(ListBox list, UIElement anchor, List<FileItem> sel)
    {
        var menu = new ContextMenu();
        if (sel.Count == 0)
        {
            var (count, bytes) = RecycleBin.Totals();
            menu.Items.Add(MI(count == 0 ? "The Recycle Bin is empty" : $"{count:N0} item{(count == 1 ? "" : "s")} · {Fmt.Size(bytes)}", "", null, null, false));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Restore all items", "", () => RestoreRecycled(list.Items.Cast<FileItem>().ToList()), null, list.Items.Count > 0));
            menu.Items.Add(MI("Empty Recycle Bin…", "", EmptyRecycleBin, null, count > 0));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Refresh", "", () => LoadCurrent(keepSelection: true), "F5"));
            menu.Items.Add(MI("Open in File Explorer", "", () => Try(() => Shell.OpenInExplorer("shell:RecycleBinFolder"))));
        }
        else
        {
            var what = sel.Count == 1 ? $"“{sel[0].Name}”" : $"{sel.Count} items";
            menu.Items.Add(MI("Restore", "", () => RestoreRecycled(sel)));
            menu.Items.Add(MI("Preview", "", TogglePreview, "Space", sel.Count == 1 && !sel[0].IsDir));
            menu.Items.Add(new Separator());
            if (sel.Count == 1)
                menu.Items.Add(MI("Show original folder", "", () =>
                {
                    var dir = System.IO.Path.GetDirectoryName(sel[0].OriginalPath);
                    if (SafeDirExists(dir)) Navigate(dir); else Toast("The original folder doesn't exist any more — Restore recreates it");
                }));
            menu.Items.Add(MI("Delete permanently…", "", () => DeleteRecycled(sel), "Delete"));
        }
        if (anchor != null) { menu.PlacementTarget = anchor; menu.Placement = PlacementMode.Bottom; }
        else { menu.PlacementTarget = list; menu.Placement = PlacementMode.MousePoint; }
        menu.IsOpen = true;
    }

    void AddRecycleSideItems(ContextMenu menu, SideItem s)
    {
        menu.Items.Add(MI("Open", "", () => OpenSide(s, false)));
        menu.Items.Add(MI("Open in new tab", "", () => OpenSide(s, true)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Empty Recycle Bin…", "", EmptyRecycleBin, null, RecycleBin.Totals().Count > 0));
        menu.Items.Add(MI("Open in File Explorer", "", () => Try(() => Shell.OpenInExplorer("shell:RecycleBinFolder"))));
    }

    async void RestoreRecycled(List<FileItem> sel)
    {
        if (sel.Count == 0) return;
        var (restored, errors) = await Task.Run(() => RecycleBin.Restore(sel));
        if (restored.Count > 0)
            Toast(restored.Count == 1 ? $"Restored “{System.IO.Path.GetFileName(restored[0])}” to {System.IO.Path.GetDirectoryName(restored[0])}" : $"Restored {restored.Count} items");
        if (errors.Count > 0)
            MessageBox.Show(this, "Some items couldn't be restored:\n\n" + string.Join("\n", errors.Take(8)), "Restore", MessageBoxButton.OK, MessageBoxImage.Warning);
        LoadCurrent(keepSelection: false);
    }

    async void DeleteRecycled(List<FileItem> sel)
    {
        sel = sel.Where(f => f.IsRecycled).ToList();
        if (sel.Count == 0) return;
        var msg = sel.Count == 1 ? $"Permanently delete “{sel[0].Name}”?" : $"Permanently delete these {sel.Count} items?";
        if (MessageBox.Show(this, msg + "\n\nThey'll be gone for good — this can't be undone.", "Delete permanently",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var errors = await Task.Run(() => RecycleBin.DeleteForever(sel));
        if (errors.Count > 0)
            MessageBox.Show(this, "Some items couldn't be deleted:\n\n" + string.Join("\n", errors.Take(8)), "Delete permanently", MessageBoxButton.OK, MessageBoxImage.Warning);
        else Toast(sel.Count == 1 ? $"Deleted “{sel[0].Name}” permanently" : $"Deleted {sel.Count} items permanently");
        LoadCurrent(keepSelection: false);
    }

    void EmptyRecycleBin()
    {
        // Windows shows its own confirmation and progress, and refreshes Explorer
        RecycleBin.Empty(Hwnd);
        if (RecycleBin.Is(current?.Path)) LoadCurrent(keepSelection: false);
    }

    /// <summary>Files dropped on the sidebar's Recycle Bin are moved to it.</summary>
    void RecycleDropped(IEnumerable<string> files)
    {
        var list = files.Where(p => !IsRemote(p) && !Archives.IsArc(p)).ToList();
        if (list.Count == 0) return;
        FileOps.Delete(list, true, Hwnd, () => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Toast(list.Count == 1 ? $"Moved “{System.IO.Path.GetFileName(list[0])}” to the Recycle Bin" : $"Moved {list.Count} items to the Recycle Bin");
            if (RecycleBin.Is(current?.Path)) LoadCurrent(keepSelection: true);
        }));
    }
}
