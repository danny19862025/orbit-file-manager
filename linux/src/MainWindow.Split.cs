using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Orbit;

/// <summary>Split view: a second folder side by side. Drag a tab onto the right half to open it there.</summary>
public partial class MainWindow
{
    string splitPath;
    readonly Stack<string> splitBack = new();
    FileSystemWatcher splitWatcher;
    DispatcherTimer splitDebounce;
    int splitVersion;
    Point? splitDragStart;
    FileItem splitDragItem;
    PointerPressedEventArgs splitPress;

    bool SplitOpen => splitPath != null;

    void WireSplit()
    {
        SplitList.DoubleTapped += (_, e) => { if (ItemFromSource(SplitList, e.Source) is FileItem f) { SplitOpenItem(f); e.Handled = true; } };
        SplitList.AddHandler(PointerPressedEvent, SplitList_PreviewMouseDown, RoutingStrategies.Tunnel, true);
        SplitList.AddHandler(PointerMovedEvent, SplitList_PreviewMouseMove, RoutingStrategies.Tunnel, true);
        SplitList.AddHandler(PointerReleasedEvent, SplitList_PreviewMouseUp, RoutingStrategies.Tunnel, true);
        SplitList.ContextRequested += SplitList_RightClick;
        SplitTitleArea.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left && SplitOpen) Navigate(splitPath); };
        SplitHost.AddHandler(DragDrop.DragOverEvent, Split_DragOver);
        SplitHost.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropTarget(null));
        SplitHost.AddHandler(DragDrop.DropEvent, Split_Drop);

        // resizing by dragging the pane's left edge
        double startX = 0, startW = 0;
        SplitGrip.PointerPressed += (_, e) => { startX = e.GetPosition(this).X; startW = SplitHost.Width; e.Pointer.Capture(SplitGrip); e.Handled = true; };
        SplitGrip.PointerMoved += (_, e) =>
        {
            if (!ReferenceEquals(e.Pointer.Captured, SplitGrip)) return;
            var w = Math.Clamp(startW - (e.GetPosition(this).X - startX), 260, Math.Max(300, MainArea.Bounds.Width - 320));
            SplitHost.Width = w;
            ContentHost.Margin = new Thickness(0, 0, w, 0);
        };
        SplitGrip.PointerReleased += (_, e) => { e.Pointer.Capture(null); S.SplitWidth = SplitHost.Width; SaveSoon(); };
    }

    // ---------- open / close ----------
    internal void OpenSplit(string path)
    {
        if (string.IsNullOrEmpty(path) || IsTag(path)) path = current?.Path ?? Home;
        if (IsTag(path)) path = Home;
        if (!LocationExists(path)) { Toast("That folder isn't available"); return; }
        if (!SplitOpen) splitBack.Clear();
        else if (!Same(splitPath, path)) splitBack.Push(splitPath);
        splitPath = Norm(path);
        SplitHost.Width = Math.Clamp(S.SplitWidth, 280, Math.Max(320, ContentHost.Bounds.Width * 0.75));
        SplitHost.IsVisible = true;
        ContentHost.Margin = new Thickness(0, 0, SplitHost.Width, 0);
        S.SplitPath = splitPath;
        SaveSoon();
        LoadSplit();
    }

    void CloseSplit()
    {
        splitPath = null;
        StopSplitWatcher();
        SplitList.ItemsSource = null;
        SplitHost.IsVisible = false;
        ContentHost.Margin = new Thickness(0);
        S.SplitPath = null;
        SaveSoon();
        FocusList();
    }

    internal void ToggleSplit()
    {
        if (SplitOpen) CloseSplit();
        else OpenSplit(current?.Path);
    }

    void RestoreSplit()
    {
        if (!string.IsNullOrEmpty(S.SplitPath) && (SafeDirExists(S.SplitPath) || Archives.IsArc(S.SplitPath)))
            Dispatcher.UIThread.Post(() => OpenSplit(S.SplitPath), DispatcherPriority.Loaded);
    }

    void SplitClose_Click(object sender, RoutedEventArgs e) => CloseSplit();
    void SplitRefresh_Click(object sender, RoutedEventArgs e) => LoadSplit();

    void SplitBack_Click(object sender, RoutedEventArgs e)
    {
        if (splitBack.Count == 0) return;
        splitPath = splitBack.Pop();
        LoadSplit();
    }

    void SplitUp_Click(object sender, RoutedEventArgs e)
    {
        var parent = ParentOf(splitPath);
        if (parent == null) return;
        var from = splitPath;
        splitBack.Push(splitPath);
        splitPath = parent;
        LoadSplit(Archives.IsArc(from) ? ArchiveSelectOnUp(from) : from);
    }

    void SplitSwap_Click(object sender, RoutedEventArgs e)
    {
        if (!SplitOpen || current == null || IsTag(current.Path)) return;
        var mainPath = current.Path;
        var other = splitPath;
        splitBack.Push(splitPath);
        splitPath = mainPath;
        LoadSplit();
        Navigate(other);
    }

    void SplitNavigate(string path)
    {
        if (!Same(path, splitPath)) splitBack.Push(splitPath);
        splitPath = Norm(path);
        LoadSplit();
    }

    // ---------- loading ----------
    async void LoadSplit(string select = null)
    {
        if (!SplitOpen) return;
        int ver = ++splitVersion;
        var path = splitPath;
        S.SplitPath = path;
        SplitTitle.Text = Display(path);
        SplitSub.Text = Archives.IsArc(path) ? Archives.Parse(path).archive : path;
        SplitBackBtn.IsEnabled = splitBack.Count > 0;
        SplitUpBtn.IsEnabled = ParentOf(path) != null;
        SplitEmpty.Text = "Loading…";
        List<FileItem> list;
        string error = null;
        try { list = await Task.Run(() => LoadChildrenSorted(path)); }
        catch (Exception ex) { list = new List<FileItem>(); error = ex.Message; }
        if (ver != splitVersion) return;
        foreach (var f in list) ApplyTags(f);
        SplitList.ItemsSource = list;
        SplitEmpty.Text = error ?? (list.Count == 0 ? "This folder is empty" : "");
        if (select != null)
        {
            var s = list.FirstOrDefault(f => Same(f.FullPath, select));
            if (s != null) { SplitList.SelectedItem = s; SplitList.ScrollIntoView(s); }
        }
        StartSplitWatcher(path);
    }

    void StartSplitWatcher(string path)
    {
        StopSplitWatcher();
        if (IsRemote(path) || Archives.IsArc(path) || !SafeDirExists(path)) return;
        try
        {
            var w = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite
            };
            FileSystemEventHandler h = (_, _) => Dispatcher.UIThread.Post(() =>
            {
                splitDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
                {
                    splitDebounce.Stop();
                    var keep = SplitList.SelectedItems.Cast<FileItem>().Select(f => f.FullPath).FirstOrDefault();
                    LoadSplit(keep);
                });
                splitDebounce.Stop();
                splitDebounce.Start();
            });
            w.Created += h; w.Deleted += h; w.Changed += h;
            w.Renamed += (s, e) => h(s, e);
            w.EnableRaisingEvents = true;
            splitWatcher = w;
        }
        catch { }
    }

    void StopSplitWatcher()
    {
        try { splitWatcher?.Dispose(); } catch { }
        splitWatcher = null;
    }

    List<FileItem> SplitSelected() => SplitList.SelectedItems?.Cast<FileItem>().ToList() ?? new List<FileItem>();

    // ---------- opening items ----------
    void SplitOpenItem(FileItem f)
    {
        if (f.IsDir) { SplitNavigate(f.FullPath); return; }
        if (f.InArchive) { OpenArchivedFile(f); return; }
        if (!f.IsRemote && Archives.IsArchiveFile(f.FullPath)) { SplitNavigate(Archives.Make(f.FullPath, "")); return; }
        if (f.IsRemote) { OpenRemoteFile(f); return; }
        Try(() => Shell.Open(f.FullPath));
    }

    // ---------- keyboard (when the split list has focus) ----------
    bool HandleSplitKey(Key key, KeyModifiers m)
    {
        bool ctrl = (m & KeyModifiers.Control) != 0, shift = (m & KeyModifiers.Shift) != 0, alt = (m & KeyModifiers.Alt) != 0;
        var sel = SplitSelected();
        if (key == Key.Enter && !alt) { foreach (var f in sel.Take(15)) SplitOpenItem(f); return true; }
        if (key == Key.Back) { SplitUp_Click(null, null); return true; }
        if (key == Key.F5) { LoadSplit(); return true; }
        if (key == Key.Delete) { SplitDelete(sel, shift); return true; }
        if (ctrl && key == Key.C && !shift) { SplitCopy(sel, false); return true; }
        if (ctrl && key == Key.X) { SplitCopy(sel, true); return true; }
        if (ctrl && key == Key.V) { Paste(splitPath); return true; }
        if (ctrl && shift && key == Key.C) { CopyPaths(sel.Select(f => f.FullPath).DefaultIfEmpty(splitPath)); return true; }
        if (alt && key == Key.Enter) { if (sel.Count > 0) new PropertiesWindow(this, sel).Show(this); return true; }
        if (key == Key.Space && sel.Count == 1 && !sel[0].IsDir) { SplitOpenItem(sel[0]); return true; }
        return false;
    }

    async void SplitDelete(List<FileItem> sel, bool permanent)
    {
        if (sel.Count == 0) return;
        if (sel.Any(f => f.InArchive)) { Toast(ReadOnlyArchive); return; }
        if (sel.Any(f => f.IsRemote)) { DeleteRemote(sel); return; }
        if (permanent && !await Dialog.Ask(this, "Delete permanently", sel.Count == 1 ? $"Permanently delete “{sel[0].Name}”?" : $"Permanently delete {sel.Count} items?",
                "Delete", "Cancel", warning: true, yesIsDefault: false)) return;
        FileOps.Delete(sel.Select(f => f.FullPath), !permanent, this);
    }

    async void SplitCopy(List<FileItem> sel, bool cut)
    {
        if (sel.Count == 0) return;
        if (sel.Any(f => f.InArchive)) { if (cut) Toast(ReadOnlyArchive); else CopyArchivedToClipboard(sel); return; }
        if (sel.Any(f => f.IsRemote)) { if (cut) Toast("Cut isn't available for server files."); else CopyRemoteToClipboard(sel); return; }
        if (await PutFilesOnClipboard(sel.Select(f => f.FullPath).ToList(), cut))
            Toast($"{(cut ? "Cut" : "Copied")} {(sel.Count == 1 ? $"“{sel[0].Name}”" : sel.Count + " items")}");
    }

    // ---------- context menu ----------
    async void SplitList_RightClick(object sender, ContextRequestedEventArgs e)
    {
        var item = ItemFromSource(SplitList, e.Source);
        if (item == null) SplitList.UnselectAll();
        else if (!SplitList.SelectedItems.Contains(item)) SplitList.SelectedItem = item;
        e.Handled = true;
        var sel = SplitSelected();
        var menu = new ContextMenu();
        bool readOnly = Archives.IsArc(splitPath);
        if (sel.Count == 0)
        {
            menu.Items.Add(MI("Paste", "", () => Paste(splitPath), "Ctrl+V", !readOnly && await CanPaste()));
            menu.Items.Add(MI("Refresh", "", () => LoadSplit(), "F5"));
            menu.Items.Add(MI("Open in the main pane", "", () => Navigate(splitPath)));
            menu.Items.Add(MI("Copy path", "", () => SetClipboardText(splitPath)));
        }
        else
        {
            var first = sel[0];
            menu.Items.Add(MI("Open", "", () => { foreach (var f in sel.Take(15)) SplitOpenItem(f); }, "Enter"));
            if (first.IsDir && sel.Count == 1)
            {
                menu.Items.Add(MI("Open in the main pane", "", () => Navigate(first.FullPath)));
                menu.Items.Add(MI("Open in new tab", "", () => AddTab(first.FullPath, true)));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Cut", "", () => SplitCopy(sel, true), "Ctrl+X", !readOnly));
            menu.Items.Add(MI("Copy", "", () => SplitCopy(sel, false), "Ctrl+C"));
            menu.Items.Add(MI("Copy path", "", () => CopyPaths(sel.Select(f => f.FullPath)), "Ctrl+Shift+C"));
            menu.Items.Add(MI("Delete", "", () => SplitDelete(sel, false), "Del", !readOnly));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Properties", "", () => new PropertiesWindow(this, sel).Show(this), "Alt+Enter"));
        }
        OpenMenu(menu, SplitList, null);
    }

    // ---------- drag out of the split pane ----------
    void SplitList_PreviewMouseDown(object sender, PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(SplitList);
        splitDragItem = ItemFromSource(SplitList, e.Source);
        if (pt.Properties.IsMiddleButtonPressed && splitDragItem is { IsDir: true } mid)
        {
            AddTab(mid.FullPath, false);
            Toast($"Opened “{mid.Name}” in a new tab");
            e.Handled = true;
            return;
        }
        if (!pt.Properties.IsLeftButtonPressed) return;
        splitDragStart = splitDragItem == null ? null : e.GetPosition(SplitList);
        splitPress = e;
        if (splitDragItem == null && FindAncestor<ScrollBar>(e.Source) == null) SplitList.UnselectAll();
    }

    void SplitList_PreviewMouseUp(object sender, PointerReleasedEventArgs e)
    {
        splitDragStart = null;
        splitDragItem = null;
    }

    void SplitList_PreviewMouseMove(object sender, PointerEventArgs e)
    {
        if (splitDragStart == null || splitDragItem == null || !e.GetCurrentPoint(SplitList).Properties.IsLeftButtonPressed) return;
        var pos = e.GetPosition(SplitList);
        if (Math.Abs(pos.X - splitDragStart.Value.X) < 5 && Math.Abs(pos.Y - splitDragStart.Value.Y) < 5) return;
        var sel = SplitList.SelectedItems.Contains(splitDragItem) ? SplitSelected() : new List<FileItem> { splitDragItem };
        splitDragStart = null;
        splitDragItem = null;
        if (sel.Any(f => f.InArchive)) { StartArchiveDrag(SplitList, sel, splitPress); return; }
        if (sel.Any(f => f.IsRemote)) { Toast("Server files can't be dragged out yet. Right-click › Copy instead."); return; }
        StartFileDrag(SplitList, sel.Select(f => f.FullPath).ToArray(), splitPress);
    }

    // ---------- drop into the split pane ----------
    string SplitDropTarget(DragEventArgs e, string[] files, out FileItem over)
    {
        var hit = ItemFromSource(SplitList, e.Source);
        over = null;
        if (hit != null && hit.IsDir && !hit.InArchive && !files.Any(f => Same(f, hit.FullPath))) { over = hit; return hit.FullPath; }
        return Archives.IsArc(splitPath) ? null : splitPath;
    }

    void Split_DragOver(object sender, DragEventArgs e)
    {
        RememberAllowed(e);
        e.Handled = true;
        if (!TryGetDropFiles(e.DataTransfer, out var files)) { e.DragEffects = DragDropEffects.None; SetDropTarget(null); return; }
        var target = SplitDropTarget(e, files, out var over);
        SetDropTarget(over);
        e.DragEffects = target != null ? ChooseEffect(e, files, target) : DragDropEffects.None;
    }

    void Split_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SetDropTarget(null);
        if (!TryGetDropFiles(e.DataTransfer, out var files)) return;
        var target = SplitDropTarget(e, files, out _);
        if (target == null) { Toast(ReadOnlyArchive); return; }
        var eff = ChooseEffect(e, files, target);
        e.DragEffects = eff;
        DoTransfer(files, target, eff);
    }

    // ---------- dragging a tab onto the content area ----------
    bool TabDragOverContent(DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(TabFormat)) return false;
        e.Handled = true;
        bool rightHalf = e.GetPosition(ContentHost).X > ContentHost.Bounds.Width / 2;
        var tab = e.DataTransfer.TryGetValue(TabFormat);
        if (rightHalf && tab != null && !IsTag(tab.Path))
        {
            e.DragEffects = DragDropEffects.Move;
            SplitDropZone.Width = Math.Max(240, ContentHost.Bounds.Width / 2 - 16);
            SplitDropText.Text = $"Drop to split — open “{Display(tab.Path)}” on the right";
            SplitDropZone.IsVisible = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
            SplitDropZone.IsVisible = false;
        }
        return true;
    }

    bool TabDropOnContent(DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(TabFormat)) return false;
        e.Handled = true;
        SplitDropZone.IsVisible = false;
        if (e.GetPosition(ContentHost).X > ContentHost.Bounds.Width / 2 && e.DataTransfer.TryGetValue(TabFormat) is TabState tab && !IsTag(tab.Path))
        {
            OpenSplit(tab.Path);
            if (Tabs.Count > 1 && !ReferenceEquals(tab, current)) CloseTab(tab); // the tab moves into the split pane
        }
        return true;
    }
}
