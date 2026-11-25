using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Orbit;

/// <summary>Sidebar extras: pin folders to Places by drag &amp; drop, and show/hide whole sections.</summary>
public partial class MainWindow
{
    public static readonly (string Id, string Label)[] SidebarSections =
    {
        ("Bookmarks", "Bookmarks"), ("Tags", "Tags"), ("Drives", "Drives"), ("Network", "Network"), ("Recent", "Recent"), ("Dropzone", "Dropzone"),
    };

    // ---------- section visibility ----------
    void ApplySidebarSections()
    {
        var map = new Dictionary<string, UIElement>
        {
            ["Bookmarks"] = SecBookmarks, ["Tags"] = SecTags, ["Drives"] = SecDrives,
            ["Network"] = SecNetwork, ["Recent"] = SecRecent, ["Dropzone"] = DropzoneBorder,
        };
        foreach (var kv in map)
            kv.Value.Visibility = S.HiddenSections.Contains(kv.Key) ? Visibility.Collapsed : Visibility.Visible;
    }

    internal bool IsSectionVisible(string id) => !S.HiddenSections.Contains(id);

    internal void SetSectionVisible(string id, bool visible)
    {
        S.HiddenSections.RemoveAll(x => x == id);
        if (!visible) S.HiddenSections.Add(id);
        ApplySidebarSections();
        SaveSoon();
    }

    // ---------- pinned places ----------
    bool IsPinned(string path) => S.CustomPlaces.Any(p => Same(p, path));

    void RebuildPlaces()
    {
        Places.Clear();
        BuildPlaces();
        MarkActive();
    }

    void TogglePin(string path)
    {
        if (IsPinned(path)) UnpinPlace(path);
        else PinPlace(path);
    }

    void PinPlace(string path)
    {
        if (!SafeDirExists(path) || IsRemote(path) || IsTag(path)) { Toast("Only folders on this PC can be added to Places"); return; }
        path = Norm(path);
        if (IsPinned(path) || Places.Any(p => Same(p.Path, path))) { Toast($"“{Display(path)}” is already in Places"); return; }
        S.CustomPlaces.Add(path);
        RebuildPlaces();
        SaveSoon();
        Toast($"Added “{Display(path)}” to Places");
    }

    void UnpinPlace(string path)
    {
        S.CustomPlaces.RemoveAll(p => Same(p, path));
        RebuildPlaces();
        SaveSoon();
    }

    void MovePinned(string path, int delta)
    {
        int i = S.CustomPlaces.FindIndex(p => Same(p, path));
        int j = i + delta;
        if (i < 0 || j < 0 || j >= S.CustomPlaces.Count) return;
        (S.CustomPlaces[i], S.CustomPlaces[j]) = (S.CustomPlaces[j], S.CustomPlaces[i]);
        RebuildPlaces();
        SaveSoon();
    }

    void BuildPinnedMenu(ContextMenu menu, SideItem s)
    {
        int i = S.CustomPlaces.FindIndex(p => Same(p, s.Path));
        menu.Items.Add(MI("Open", "", () => OpenSide(s, false)));
        menu.Items.Add(MI("Open in new tab", "", () => OpenSide(s, true)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Move up", "", () => MovePinned(s.Path, -1), null, i > 0));
        menu.Items.Add(MI("Move down", "", () => MovePinned(s.Path, 1), null, i >= 0 && i < S.CustomPlaces.Count - 1));
        menu.Items.Add(MI("Copy path", "", () => SetClipboardText(s.Path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Remove from Places", "", () => UnpinPlace(s.Path)));
    }

    // ---------- drag a folder into Places ----------
    static List<string> DroppedFolders(IDataObject data)
    {
        if (!TryGetDropFiles(data, out var files)) return new List<string>();
        return files.Where(f => { try { return Directory.Exists(f); } catch { return false; } }).ToList();
    }

    void Sidebar_PreviewDragOver(object sender, DragEventArgs e)
    {
        bool folders = DroppedFolders(e.Data).Count > 0;
        PlacesDropHint.Visibility = folders && TglPlaces.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    void Sidebar_PreviewDragLeave(object sender, DragEventArgs e)
    {
        var p = e.GetPosition(Sidebar);
        if (p.X < 0 || p.Y < 0 || p.X >= Sidebar.ActualWidth || p.Y >= Sidebar.ActualHeight)
            PlacesDropHint.Visibility = Visibility.Collapsed;
    }

    void Sidebar_PreviewDrop(object sender, DragEventArgs e)
    {
        // hide the strip after whichever target handles the drop
        Dispatcher.BeginInvoke(() => PlacesDropHint.Visibility = Visibility.Collapsed);
    }

    void PlacesHint_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedFolders(e.Data).Count > 0 ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    void PlacesHint_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.Link; // only a shortcut is added; the source keeps its files
        foreach (var f in DroppedFolders(e.Data)) PinPlace(f);
        PlacesDropHint.Visibility = Visibility.Collapsed;
    }
}
