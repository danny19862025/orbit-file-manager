using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Orbit;

/// <summary>Drag &amp; drop: dragging out, drop feedback, spring-loaded folders, tab/breadcrumb/sidebar targets, tab reordering, marquee selection.</summary>
public partial class MainWindow
{
    const string TabFormat = "OrbitTab";

    readonly DispatcherTimer springTimer;
    FileItem springItem;
    TabState springTab;
    Point? tabDragStart;
    TabState tabDragging;
    bool dzDragging;

    ListBox marqueeList;
    Point marqueeStart;
    HashSet<object> marqueeBase;
    bool marqueeMoved;

    void WireDragDrop()
    {
        ContentHost.AddHandler(DragDrop.DragOverEvent, Content_DragOver);
        ContentHost.AddHandler(DragDrop.DragLeaveEvent, Content_DragLeave);
        ContentHost.AddHandler(DragDrop.DropEvent, Content_Drop);

        // sidebar items (Places, drives, bookmarks…) and the "drop here to add to Places" strip
        Sidebar.AddHandler(DragDrop.DragOverEvent, Sidebar_PreviewDragOver, RoutingStrategies.Tunnel);
        Sidebar.AddHandler(DragDrop.DragLeaveEvent, Sidebar_PreviewDragLeave, RoutingStrategies.Tunnel);
        Sidebar.AddHandler(DragDrop.DropEvent, (_, _) => Dispatcher.UIThread.Post(() => PlacesDropHint.IsVisible = false), RoutingStrategies.Tunnel);
        Sidebar.AddHandler(DragDrop.DragOverEvent, SideItem_DragOver);
        Sidebar.AddHandler(DragDrop.DropEvent, SideItem_Drop);

        TabStrip.AddHandler(DragDrop.DragEnterEvent, Tab_DragEnter);
        TabStrip.AddHandler(DragDrop.DragOverEvent, Tab_DragOver);
        TabStrip.AddHandler(DragDrop.DragLeaveEvent, Tab_DragLeave);
        TabStrip.AddHandler(DragDrop.DropEvent, Tab_Drop);
        TabStrip.AddHandler(PointerPressedEvent, Tab_PointerPressed, RoutingStrategies.Tunnel);
        TabStrip.AddHandler(PointerMovedEvent, Tab_PointerMoved, RoutingStrategies.Tunnel);
        TabStrip.AddHandler(PointerReleasedEvent, Tab_PointerReleased, RoutingStrategies.Tunnel);

        CrumbScroll.AddHandler(DragDrop.DragOverEvent, Crumb_DragOver);
        CrumbScroll.AddHandler(DragDrop.DropEvent, Crumb_Drop);
    }

    // =====================================================================
    // Data on the drag-and-drop clipboard
    // =====================================================================
    static string[] GetDropFiles(IDataObject data)
    {
        try
        {
            if (data.Contains(DataFormats.Files))
            {
                var files = data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToArray();
                if (files is { Length: > 0 }) return files;
            }
            foreach (var fmt in new[] { "text/uri-list", "x-special/gnome-copied-files" })
                if (data.Contains(fmt) && data.Get(fmt) is { } raw)
                {
                    var lines = AsText(raw).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                           .Where(l => !l.StartsWith('#') && l is not ("copy" or "cut")).Select(UriToPath).Where(p => p != null).ToArray();
                    if (lines.Length > 0) return lines;
                }
        }
        catch { }
        return null;
    }

    static bool TryGetDropFiles(IDataObject data, out string[] files)
    {
        files = GetDropFiles(data);
        return files != null && files.Length > 0;
    }

    async Task<DataObject> FileDragData(string[] paths)
    {
        var data = new DataObject();
        var items = new List<IStorageItem>();
        foreach (var p in paths)
        {
            try
            {
                IStorageItem it = Directory.Exists(p) ? await StorageProvider.TryGetFolderFromPathAsync(p) : await StorageProvider.TryGetFileFromPathAsync(p);
                if (it != null) items.Add(it);
            }
            catch { }
        }
        if (items.Count > 0) data.Set(DataFormats.Files, items);
        data.Set("text/uri-list", Encoding.UTF8.GetBytes(string.Join("\r\n", paths.Select(IconService.FileUri)) + "\r\n"));
        data.Set(DataFormats.Text, string.Join("\n", paths));
        return data;
    }

    // =====================================================================
    // Dragging files out of the list
    // =====================================================================
    async void StartFileDrag(Control source, string[] paths, PointerEventArgs trigger, bool copyOnly = false)
    {
        try
        {
            var data = await FileDragData(paths);
            await DragDrop.DoDragDrop(trigger, data, copyOnly ? DragDropEffects.Copy : DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch (Exception ex)
        {
            AppSettings.LogError(ex);
            Toast("Drag failed: " + ex.Message);
        }
        finally
        {
            SetDropTarget(null);
            ShowDropFeedback(null, DragDropEffects.None, null);
            springTimer.Stop();
        }
    }

    // =====================================================================
    // Choosing copy / move
    // =====================================================================
    DragDropEffects ChooseEffect(DragEventArgs e, string[] files, string target)
    {
        var allowed = allowedEffects ?? e.DragEffects;
        DragDropEffects want;
        if ((e.KeyModifiers & KeyModifiers.Control) != 0) want = DragDropEffects.Copy;
        else if ((e.KeyModifiers & KeyModifiers.Shift) != 0) want = DragDropEffects.Move;
        else if (dzDragging) want = DragDropEffects.Copy;
        else if (IsRemote(target)) want = DragDropEffects.Copy;
        else
        {
            var tm = RecycleBin.MountOf(target);
            bool sameVolume = files.All(f => RecycleBin.MountOf(f) == tm);
            want = sameVolume ? DragDropEffects.Move : DragDropEffects.Copy;
        }
        if ((allowed & want) == 0)
            want = (allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy
                 : (allowed & DragDropEffects.Move) != 0 ? DragDropEffects.Move
                 : DragDropEffects.None;

        // Moving things into the folder they're already in, or a folder into itself, does nothing.
        if (want == DragDropEffects.Move && files.All(f => Same(SafeParent(f), target))) return DragDropEffects.None;
        if (files.Any(f => Same(f, target) || target.StartsWith(f.TrimEnd('/') + "/", StringComparison.Ordinal)))
            return DragDropEffects.None;
        return want;
    }

    /// <summary>What the drag source allows, remembered from the first DragOver (Avalonia reuses DragEffects for the answer).</summary>
    DragDropEffects? allowedEffects;

    void RememberAllowed(DragEventArgs e) => allowedEffects ??= e.DragEffects == DragDropEffects.None ? DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link : e.DragEffects;

    void DoTransfer(string[] files, string target, DragDropEffects eff)
    {
        allowedEffects = null;
        if (IsRemote(target)) { if (eff != DragDropEffects.None) UploadTo(files, target); return; }
        if (eff == DragDropEffects.Move) FileOps.Move(files, target, this);
        else if (eff == DragDropEffects.Copy || eff == DragDropEffects.Link)
            FileOps.Copy(files, target, this, files.All(f => Same(SafeParent(f), target)));
    }

    // =====================================================================
    // Dropping into the content area
    // =====================================================================
    string DropTargetFor(DragEventArgs e, string[] files, out FileItem over)
    {
        var hit = ItemFromSource(ActiveList, e.Source);
        over = null;
        if (hit != null && hit.IsDir && !hit.InArchive && !files.Any(f => Same(f, hit.FullPath))) { over = hit; return hit.FullPath; }
        return IsTag(current?.Path) || Archives.IsArc(current?.Path) ? null : current?.Path;
    }

    void Content_DragOver(object sender, DragEventArgs e)
    {
        RememberAllowed(e);
        if (TabDragOverContent(e)) return;
        e.Handled = true;
        if (!TryGetDropFiles(e.Data, out var files)) { e.DragEffects = DragDropEffects.None; SetDropTarget(null); ShowDropFeedback(null, DragDropEffects.None, null); return; }
        var target = DropTargetFor(e, files, out var over);
        SetDropTarget(over);
        e.DragEffects = target != null ? ChooseEffect(e, files, target) : DragDropEffects.None;
        ShowDropFeedback(target, e.DragEffects, over, e.KeyModifiers);
        AutoScrollDuringDrag(e);
    }

    void Content_DragLeave(object sender, DragEventArgs e)
    {
        SplitDropZone.IsVisible = false;
        // DragLeave also fires when moving between child elements; only clear when the pointer really left.
        var p = e.GetPosition(ContentHost);
        if (p.X >= 0 && p.Y >= 0 && p.X < ContentHost.Bounds.Width && p.Y < ContentHost.Bounds.Height) return;
        SetDropTarget(null);
        ShowDropFeedback(null, DragDropEffects.None, null);
        allowedEffects = null;
    }

    void Content_Drop(object sender, DragEventArgs e)
    {
        if (TabDropOnContent(e)) return;
        e.Handled = true;
        SetDropTarget(null);
        ShowDropFeedback(null, DragDropEffects.None, null);
        if (!TryGetDropFiles(e.Data, out var files)) return;
        var target = DropTargetFor(e, files, out _);
        if (target == null) { if (Archives.IsArc(current?.Path)) Toast(ReadOnlyArchive); return; }
        var eff = ChooseEffect(e, files, target);
        e.DragEffects = eff;
        DoTransfer(files, target, eff);
    }

    void SetDropTarget(FileItem f)
    {
        if (ReferenceEquals(dropTarget, f)) return;
        if (dropTarget != null) { dropTarget.IsDropTarget = false; MarkDrop(dropTarget, false); }
        dropTarget = f;
        if (f != null) { f.IsDropTarget = true; MarkDrop(f, true); }
    }

    void MarkDrop(FileItem f, bool on)
    {
        foreach (var l in AllLists.Append(SplitList))
            if (l.ContainerFromItem(f) is Control c) c.Classes.Set("drop", on);
    }

    void ShowDropFeedback(string target, DragDropEffects eff, FileItem over, KeyModifiers keys = KeyModifiers.None)
    {
        if (target == null)
        {
            DropOverlay.IsVisible = false;
            DropHint.IsVisible = false;
            springItem = null;
            if (springTab == null) springTimer.Stop();
            return;
        }
        if (eff == DragDropEffects.None)
        {
            DropOverlay.IsVisible = false;
            DropHintText.Text = "Already in this folder — drop onto a folder, a tab or the path bar";
            DropHint.IsVisible = true;
            springItem = null;
            return;
        }
        var verb = eff == DragDropEffects.Move ? "Move" : "Copy";
        if (IsRemote(target)) verb = "Upload";
        var hold = (keys & (KeyModifiers.Control | KeyModifiers.Shift)) == 0
            ? (eff == DragDropEffects.Move ? "   ·   hold Ctrl to copy" : "   ·   hold Shift to move")
            : "";
        if (over != null)
        {
            DropOverlay.IsVisible = false;
            DropHintText.Text = $"{verb} into “{over.Name}”{hold}";
            if (!ReferenceEquals(springItem, over))
            {
                springItem = over;
                springTab = null;
                springTimer.Stop();
                springTimer.Start(); // hover a moment to open the folder
            }
        }
        else
        {
            DropOverlay.IsVisible = true;
            DropHintText.Text = $"{verb} to “{Display(target)}”{hold}";
            springItem = null;
            if (springTab == null) springTimer.Stop();
        }
        DropHint.IsVisible = true;
    }

    void AutoScrollDuringDrag(DragEventArgs e)
    {
        var sv = ScrollOf(ActiveList);
        if (sv == null) return;
        double y = e.GetPosition(sv).Y;
        if (y < 36) sv.Offset = new Vector(sv.Offset.X, Math.Max(0, sv.Offset.Y - 18));
        else if (y > sv.Bounds.Height - 36) sv.Offset = new Vector(sv.Offset.X, sv.Offset.Y + 18);
    }

    void SpringTimer_Tick(object sender, EventArgs e)
    {
        springTimer.Stop();
        if (springTab != null)
        {
            var t = springTab;
            springTab = null;
            if (!ReferenceEquals(t, current)) SwitchTo(t);
        }
        else if (springItem != null && springItem.IsDir)
        {
            var target = springItem.FullPath;
            springItem = null;
            SetDropTarget(null);
            Navigate(target);
        }
    }

    // =====================================================================
    // Sidebar: drop on a place / drive / the Trash, or pin a folder to Places
    // =====================================================================
    static SideItem SideItemFrom(object src) => (src as Visual)?.FindAncestorOfType<Button>(true)?.Tag as SideItem;

    void SideItem_DragOver(object sender, DragEventArgs e)
    {
        RememberAllowed(e);
        var s = SideItemFrom(e.Source);
        if (s == null || FindAncestor<Border>(e.Source) is Border b && ReferenceEquals(b, PlacesDropHint)) { PlacesHint_DragOver(e); return; }
        e.Handled = true;
        e.DragEffects = DragDropEffects.None;
        if (s.Kind == "recycle" && TryGetDropFiles(e.Data, out _))
            e.DragEffects = DragDropEffects.Move;
        else if (!IsTag(s.Path) && s.Kind is not ("recycle" or "connect" or "server") && TryGetDropFiles(e.Data, out var files))
            e.DragEffects = ChooseEffect(e, files, s.Path);
    }

    void SideItem_Drop(object sender, DragEventArgs e)
    {
        var s = SideItemFrom(e.Source);
        if (s == null) { PlacesHint_Drop(e); return; }
        e.Handled = true;
        if (s.Kind == "recycle" && TryGetDropFiles(e.Data, out var binned))
            RecycleDropped(binned);
        else if (!IsTag(s.Path) && s.Kind is not ("recycle" or "connect" or "server") && TryGetDropFiles(e.Data, out var files))
            DoTransfer(files, s.Path, ChooseEffect(e, files, s.Path));
    }

    static List<string> DroppedFolders(IDataObject data)
    {
        if (!TryGetDropFiles(data, out var files)) return new List<string>();
        return files.Where(f => { try { return Directory.Exists(f); } catch { return false; } }).ToList();
    }

    void Sidebar_PreviewDragOver(object sender, DragEventArgs e)
    {
        bool folders = DroppedFolders(e.Data).Count > 0;
        PlacesDropHint.IsVisible = folders && TglPlaces.IsChecked == true;
    }

    void Sidebar_PreviewDragLeave(object sender, DragEventArgs e)
    {
        var p = e.GetPosition(Sidebar);
        if (p.X < 0 || p.Y < 0 || p.X >= Sidebar.Bounds.Width || p.Y >= Sidebar.Bounds.Height)
            PlacesDropHint.IsVisible = false;
    }

    void PlacesHint_DragOver(DragEventArgs e)
    {
        if (FindAncestor<Border>(e.Source) is not Border b || !ReferenceEquals(b, PlacesDropHint)) { e.DragEffects = DragDropEffects.None; return; }
        e.DragEffects = DroppedFolders(e.Data).Count > 0 ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    void PlacesHint_Drop(DragEventArgs e)
    {
        if (FindAncestor<Border>(e.Source) is not Border b || !ReferenceEquals(b, PlacesDropHint)) return;
        e.Handled = true;
        e.DragEffects = DragDropEffects.Link; // only a shortcut is added; the source keeps its files
        foreach (var f in DroppedFolders(e.Data)) PinPlace(f);
        PlacesDropHint.IsVisible = false;
    }

    // =====================================================================
    // Tabs: drop files into a tab's folder, hover to switch, drag to reorder
    // =====================================================================
    static TabState TabFrom(object src) => (src as Visual)?.FindAncestorOfType<Border>(true) is { } b ? FindTabBorder(b)?.Tag as TabState : null;

    static Border FindTabBorder(Visual v)
    {
        for (; v != null; v = v.GetVisualParent())
            if (v is Border b && b.Classes.Contains("tab")) return b;
        return null;
    }

    void Tab_PointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (FindAncestor<Button>(e.Source) != null) return;
        if (FindTabBorder(e.Source as Visual)?.Tag is not TabState t) return;
        var pt = e.GetCurrentPoint(this);
        if (pt.Properties.IsLeftButtonPressed)
        {
            if (e.ClickCount == 2) return;
            tabDragStart = e.GetPosition(this);
            SwitchTo(t);
        }
    }

    void Tab_PointerReleased(object sender, PointerReleasedEventArgs e)
    {
        tabDragStart = null;
        if (e.InitialPressMouseButton == MouseButton.Middle && FindTabBorder(e.Source as Visual)?.Tag is TabState t)
        {
            CloseTab(t);
            e.Handled = true;
        }
    }

    async void Tab_PointerMoved(object sender, PointerEventArgs e)
    {
        if (tabDragStart == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { tabDragStart = null; return; }
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - tabDragStart.Value.X) < 10 && Math.Abs(p.Y - tabDragStart.Value.Y) < 10) return;
        tabDragStart = null;
        if (FindTabBorder(e.Source as Visual)?.Tag is not TabState t || Tabs.Count < 2) return;
        var data = new DataObject();
        data.Set(TabFormat, t);
        tabDragging = t;
        try { await DragDrop.DoDragDrop(e, data, DragDropEffects.Move); }
        catch { }
        tabDragging = null;
        SplitDropZone.IsVisible = false;
        SaveSoon();
    }

    void Tab_DragEnter(object sender, DragEventArgs e)
    {
        if (TabFrom(e.Source) is not TabState t || e.Data.Contains(TabFormat)) return;
        if (!ReferenceEquals(t, current))
        {
            springTab = t;
            springItem = null;
            springTimer.Stop();
            springTimer.Start();
        }
    }

    void Tab_DragOver(object sender, DragEventArgs e)
    {
        RememberAllowed(e);
        e.Handled = true;
        e.DragEffects = DragDropEffects.None;
        if (TabFrom(e.Source) is not TabState t) return;
        if (e.Data.Contains(TabFormat))
        {
            if (e.Data.Get(TabFormat) is TabState moving && !ReferenceEquals(moving, t))
            {
                int from = Tabs.IndexOf(moving), to = Tabs.IndexOf(t);
                if (from >= 0 && to >= 0) Tabs.Move(from, to);
            }
            e.DragEffects = DragDropEffects.Move;
            return;
        }
        if (IsTag(t.Path) || !TryGetDropFiles(e.Data, out var files)) return;
        e.DragEffects = ChooseEffect(e, files, t.Path);
    }

    void Tab_DragLeave(object sender, DragEventArgs e)
    {
        if (TabFrom(e.Source) is TabState t && ReferenceEquals(t, springTab))
        {
            springTab = null;
            springTimer.Stop();
        }
    }

    void Tab_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        springTimer.Stop();
        springTab = null;
        if (e.Data.Contains(TabFormat)) { SaveSoon(); return; }
        if (TabFrom(e.Source) is not TabState t || IsTag(t.Path) || !TryGetDropFiles(e.Data, out var files)) return;
        var eff = ChooseEffect(e, files, t.Path);
        e.DragEffects = eff;
        DoTransfer(files, t.Path, eff);
    }

    // =====================================================================
    // Breadcrumbs: drop onto any parent folder in the path bar
    // =====================================================================
    static Crumb CrumbFrom(object src) => (src as Visual)?.FindAncestorOfType<Button>(true)?.Tag as Crumb;

    void Crumb_DragOver(object sender, DragEventArgs e)
    {
        RememberAllowed(e);
        e.Handled = true;
        e.DragEffects = DragDropEffects.None;
        if (CrumbFrom(e.Source) is Crumb c && c.Path != null && !IsTag(c.Path) && !Archives.IsArc(c.Path) && TryGetDropFiles(e.Data, out var files))
            e.DragEffects = ChooseEffect(e, files, c.Path);
    }

    void Crumb_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (CrumbFrom(e.Source) is Crumb c && c.Path != null && !IsTag(c.Path) && !Archives.IsArc(c.Path) && TryGetDropFiles(e.Data, out var files))
        {
            var eff = ChooseEffect(e, files, c.Path);
            e.DragEffects = eff;
            DoTransfer(files, c.Path, eff);
        }
    }

    // =====================================================================
    // Dropzone: drop files on it, drag them out again later
    // =====================================================================
    Point? dzStart;
    FileItem dzItem;

    void WireDropzone()
    {
        DropzoneBorder.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.Handled = true;
            if (dzDragging || !TryGetDropFiles(e.Data, out var _none)) { e.DragEffects = DragDropEffects.None; return; }
            e.DragEffects = (e.DragEffects & DragDropEffects.Link) != 0 ? DragDropEffects.Link : DragDropEffects.Copy;
            DropzoneBorder.Bind(Border.BorderBrushProperty, DropzoneBorder.GetResourceObservable("AccentBrush"));
        });
        DropzoneBorder.AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropzoneBorder.Bind(Border.BorderBrushProperty, DropzoneBorder.GetResourceObservable("LineBrush")));
        DropzoneBorder.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            e.Handled = true;
            DropzoneBorder.Bind(Border.BorderBrushProperty, DropzoneBorder.GetResourceObservable("LineBrush"));
            if (dzDragging) return;
            if (TryGetDropFiles(e.Data, out var files))
            {
                e.DragEffects = DragDropEffects.Link;
                AddToDropzone(files);
            }
        });

        DzList.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (FindAncestor<Button>(e.Source) != null) return;
            dzItem = (e.Source as Visual)?.FindAncestorOfType<Border>(true) is { } b && FindDzBorder(b) is { } dz ? dz.Tag as FileItem : null;
            var pt = e.GetCurrentPoint(this);
            if (pt.Properties.IsRightButtonPressed && dzItem != null) { e.Handled = true; ShowDropzoneMenu(FindDzBorder(e.Source as Visual), dzItem); return; }
            if (!pt.Properties.IsLeftButtonPressed) return;
            dzStart = e.GetPosition(this);
            if (e.ClickCount == 2 && dzItem != null)
            {
                var f = dzItem;
                dzStart = null;
                e.Handled = true;
                if (f.IsDir) Navigate(f.FullPath);
                else if (File.Exists(f.FullPath)) Navigate(SafeParent(f.FullPath), f.FullPath);
                else PruneDropzone();
            }
        }, RoutingStrategies.Tunnel);
        DzList.AddHandler(PointerMovedEvent, (_, e) => DzTryDrag(e, dzItem == null ? null : new[] { dzItem.FullPath }), RoutingStrategies.Tunnel);
        DzList.AddHandler(PointerReleasedEvent, (_, _) => dzStart = null, RoutingStrategies.Tunnel);
        DzHeader.PointerPressed += (_, e) => { dzItem = null; dzStart = e.GetPosition(this); };
        DzHeader.PointerMoved += (_, e) => DzTryDrag(e, DropItems.Count == 0 ? null : DropItems.Select(f => f.FullPath).ToArray());
        DzHeader.PointerReleased += (_, _) => dzStart = null;
    }

    static Border FindDzBorder(Visual v)
    {
        for (; v != null; v = v.GetVisualParent())
            if (v is Border b && b.Classes.Contains("dz")) return b;
        return null;
    }

    async void DzTryDrag(PointerEventArgs e, string[] paths)
    {
        if (dzStart == null || paths == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - dzStart.Value.X) < 5 && Math.Abs(pos.Y - dzStart.Value.Y) < 5) return;
        dzStart = null;
        dzDragging = true;
        try
        {
            var data = await FileDragData(paths);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch { }
        finally { dzDragging = false; SetDropTarget(null); ShowDropFeedback(null, DragDropEffects.None, null); }
        await Task.Delay(1500);
        PruneDropzone();
    }

    // =====================================================================
    // Marquee (box) selection — drag on empty space
    // =====================================================================
    void BeginMarquee(ListBox list, PointerPressedEventArgs e)
    {
        var sv = ScrollOf(list);
        var p = e.GetPosition(list);
        marqueeList = list;
        marqueeStart = new Point(p.X + (sv?.Offset.X ?? 0), p.Y + (sv?.Offset.Y ?? 0));
        marqueeBase = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0
            ? new HashSet<object>(list.SelectedItems.Cast<object>())
            : new HashSet<object>();
        marqueeMoved = false;
        e.Pointer.Capture(list);
        e.Handled = true;
    }

    void UpdateMarquee(PointerEventArgs e)
    {
        var list = marqueeList;
        if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) { EndMarquee(e); return; }
        var sv = ScrollOf(list);
        var p = e.GetPosition(list);

        if (sv != null)
        {
            if (p.Y < 0) sv.Offset = new Vector(sv.Offset.X, Math.Max(0, sv.Offset.Y + p.Y / 2 - 4));
            else if (p.Y > list.Bounds.Height) sv.Offset = new Vector(sv.Offset.X, sv.Offset.Y + (p.Y - list.Bounds.Height) / 2 + 4);
        }
        double ox = sv?.Offset.X ?? 0, oy = sv?.Offset.Y ?? 0;
        var r = new Rect(marqueeStart, new Point(p.X + ox, p.Y + oy)).Normalize();
        if (!marqueeMoved && r.Width < 4 && r.Height < 4) return;
        marqueeMoved = true;

        // draw: content coords → list coords → overlay canvas coords (clipped to the visible area)
        var viewRect = new Rect(r.X - ox, r.Y - oy, r.Width, r.Height).Intersect(new Rect(0, 0, list.Bounds.Width, list.Bounds.Height));
        if (viewRect.Width <= 0 || viewRect.Height <= 0) Marquee.IsVisible = false;
        else if (list.TranslatePoint(viewRect.TopLeft, MarqueeCanvas) is { } tl)
        {
            Canvas.SetLeft(Marquee, tl.X);
            Canvas.SetTop(Marquee, tl.Y);
            Marquee.Width = viewRect.Width;
            Marquee.Height = viewRect.Height;
            Marquee.IsVisible = true;
        }

        // select realized containers that intersect (off-screen ones keep their state)
        foreach (var c in list.GetRealizedContainers().OfType<ListBoxItem>())
        {
            if (!c.IsVisible || c.TranslatePoint(new Point(0, 0), list) is not { } at) continue;
            var b = new Rect(at.X + ox, at.Y + oy, c.Bounds.Width, c.Bounds.Height);
            var item = c.DataContext;
            bool want = b.Intersects(r) || marqueeBase.Contains(item);
            bool has = list.SelectedItems.Contains(item);
            if (want && !has) list.SelectedItems.Add(item);
            else if (!want && has) list.SelectedItems.Remove(item);
        }
    }

    void EndMarquee(PointerEventArgs e)
    {
        marqueeList = null;
        Marquee.IsVisible = false;
        e.Pointer.Capture(null);
        UpdateSelText();
    }
}
