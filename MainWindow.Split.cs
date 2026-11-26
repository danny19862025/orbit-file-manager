using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using IOPath = System.IO.Path;

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

    bool SplitOpen => splitPath != null;

    // ---------- open / close ----------
    internal void OpenSplit(string path)
    {
        if (string.IsNullOrEmpty(path) || IsTag(path)) path = current?.Path ?? Home;
        if (!LocationExists(path)) { Toast("That folder isn't available"); return; }
        if (!SplitOpen) splitBack.Clear();
        else if (!Same(splitPath, path)) splitBack.Push(splitPath);
        splitPath = Norm(path);
        SplitHost.Width = Math.Clamp(S.SplitWidth, 280, Math.Max(320, ContentHost.ActualWidth * 0.75));
        SplitHost.Visibility = Visibility.Visible;
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
        SplitHost.Visibility = Visibility.Collapsed;
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
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => OpenSplit(S.SplitPath));
    }

    void SplitClose_Click(object sender, RoutedEventArgs e) => CloseSplit();
    void SplitRefresh_Click(object sender, RoutedEventArgs e) => LoadSplit();
    void SplitTitle_Click(object sender, MouseButtonEventArgs e) { if (SplitOpen) Navigate(splitPath); }

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
            FileSystemEventHandler h = (_, _) => Dispatcher.BeginInvoke(() =>
            {
                splitDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
                {
                    splitDebounce.Stop();
                    var keep = SplitList.SelectedItems.Cast<FileItem>().Select(f => f.FullPath).FirstOrDefault();
                    LoadSplit(keep);
                }, Dispatcher);
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

    List<FileItem> SplitSelected() => SplitList.SelectedItems.Cast<FileItem>().ToList();

    // ---------- opening items ----------
    void SplitOpenItem(FileItem f)
    {
        if (f.IsDir) { SplitNavigate(f.FullPath); return; }
        if (f.InArchive) { OpenArchivedFile(f); return; }
        if (!f.IsRemote && Archives.IsArchiveFile(f.FullPath)) { SplitNavigate(Archives.Make(f.FullPath, "")); return; }
        if (f.IsRemote) { OpenRemoteFile(f); return; }
        Try(() => Shell.Open(f.FullPath));
    }

    void SplitList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemFromSource(SplitList, e.OriginalSource) is FileItem f) { SplitOpenItem(f); e.Handled = true; }
    }

    void SplitList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && ItemFromSource(SplitList, e.OriginalSource) is FileItem f && f.IsDir)
        {
            AddTab(f.FullPath, false);
            Toast($"Opened “{f.Name}” in a new tab");
        }
    }

    // ---------- keyboard (when the split list has focus) ----------
    bool HandleSplitKey(Key key, ModifierKeys m)
    {
        bool ctrl = (m & ModifierKeys.Control) != 0, shift = (m & ModifierKeys.Shift) != 0, alt = (m & ModifierKeys.Alt) != 0;
        var sel = SplitSelected();
        if (key == Key.Enter && !alt) { foreach (var f in sel.Take(15)) SplitOpenItem(f); return true; }
        if (key == Key.Back) { SplitUp_Click(null, null); return true; }
        if (key == Key.F5) { LoadSplit(); return true; }
        if (key == Key.Delete) { SplitDelete(sel, shift); return true; }
        if (ctrl && key == Key.C && !shift) { SplitCopy(sel, false); return true; }
        if (ctrl && key == Key.X) { SplitCopy(sel, true); return true; }
        if (ctrl && key == Key.V) { Paste(splitPath); return true; }
        if (ctrl && shift && key == Key.C) { CopyPaths(sel.Select(f => f.FullPath).DefaultIfEmpty(splitPath)); return true; }
        if (alt && key == Key.Enter) { if (sel.Count > 0) new PropertiesWindow(this, sel).Show(); return true; }
        if (key == Key.Space && sel.Count == 1 && !sel[0].IsDir) { SplitOpenItem(sel[0]); return true; }
        return false;
    }

    void SplitDelete(List<FileItem> sel, bool permanent)
    {
        if (sel.Count == 0) return;
        if (sel.Any(f => f.InArchive)) { Toast(ReadOnlyArchive); return; }
        if (sel.Any(f => f.IsRemote)) { DeleteRemote(sel); return; }
        if (permanent && MessageBox.Show(this, sel.Count == 1 ? $"Permanently delete “{sel[0].Name}”?" : $"Permanently delete {sel.Count} items?",
                "Delete permanently", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        FileOps.Delete(sel.Select(f => f.FullPath), !permanent, Hwnd);
    }

    void SplitCopy(List<FileItem> sel, bool cut)
    {
        if (sel.Count == 0) return;
        if (sel.Any(f => f.InArchive)) { if (cut) Toast(ReadOnlyArchive); else CopyArchivedToClipboard(sel); return; }
        if (sel.Any(f => f.IsRemote)) { if (cut) Toast("Cut isn't available for server files."); else CopyRemoteToClipboard(sel); return; }
        var sc = new StringCollection();
        sc.AddRange(sel.Select(f => f.FullPath).ToArray());
        var data = new DataObject();
        data.SetFileDropList(sc);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
        if (TryClipboard(() => Clipboard.SetDataObject(data, true)))
            Toast($"{(cut ? "Cut" : "Copied")} {(sel.Count == 1 ? $"“{sel[0].Name}”" : sel.Count + " items")}");
    }

    // ---------- context menu ----------
    void SplitList_RightClick(object sender, MouseButtonEventArgs e)
    {
        var item = ItemFromSource(SplitList, e.OriginalSource);
        if (item == null) SplitList.UnselectAll();
        else if (!SplitList.SelectedItems.Contains(item)) SplitList.SelectedItem = item;
        e.Handled = true;
        var sel = SplitSelected();
        var menu = new ContextMenu { PlacementTarget = SplitList, Placement = PlacementMode.MousePoint };
        bool readOnly = Archives.IsArc(splitPath);
        if (sel.Count == 0)
        {
            menu.Items.Add(MI("Paste", "", () => Paste(splitPath), "Ctrl+V", !readOnly && CanPaste()));
            menu.Items.Add(MI("Refresh", "", () => LoadSplit(), "F5"));
            menu.Items.Add(MI("Open in the main pane", "", () => Navigate(splitPath)));
            menu.Items.Add(MI("Copy path", "", () => SetClipboardText(splitPath)));
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
            menu.Items.Add(MI("Properties", "", () => new PropertiesWindow(this, sel).Show(), "Alt+Enter"));
        }
        menu.IsOpen = true;
    }

    // ---------- drag out of the split pane ----------
    void SplitList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        splitDragItem = ItemFromSource(SplitList, e.OriginalSource);
        splitDragStart = splitDragItem == null ? null : e.GetPosition(SplitList);
        if (splitDragItem == null && FindAncestor<ScrollBar>(e.OriginalSource) == null) SplitList.UnselectAll();
    }

    void SplitList_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        splitDragStart = null;
        splitDragItem = null;
    }

    void SplitList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (splitDragStart == null || splitDragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(SplitList);
        if (Math.Abs(pos.X - splitDragStart.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - splitDragStart.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var sel = SplitList.SelectedItems.Contains(splitDragItem) ? SplitSelected() : new List<FileItem> { splitDragItem };
        splitDragStart = null;
        splitDragItem = null;
        if (sel.Any(f => f.InArchive)) { StartArchiveDrag(SplitList, sel); return; }
        if (sel.Any(f => f.IsRemote)) { Toast("Server files can't be dragged out yet. Right-click › Copy instead."); return; }
        StartFileDrag(SplitList, sel.Select(f => f.FullPath).ToArray());
    }

    // ---------- drop into the split pane ----------
    string SplitDropTarget(DragEventArgs e, string[] files, out FileItem over)
    {
        var hit = ItemFromSource(SplitList, e.OriginalSource);
        over = null;
        if (hit != null && hit.IsDir && !hit.InArchive && !files.Any(f => Same(f, hit.FullPath))) { over = hit; return hit.FullPath; }
        return Archives.IsArc(splitPath) ? null : splitPath;
    }

    void Split_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (!TryGetDropFiles(e.Data, out var files)) { SetDropTarget(null); return; }
        var target = SplitDropTarget(e, files, out var over);
        SetDropTarget(over);
        if (target != null) e.Effects = ChooseEffect(e, files, target);
    }

    void Split_DragLeave(object sender, DragEventArgs e) => SetDropTarget(null);

    void Split_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SetDropTarget(null);
        if (!TryGetDropFiles(e.Data, out var files)) return;
        var target = SplitDropTarget(e, files, out _);
        if (target == null) { Toast(ReadOnlyArchive); return; }
        var eff = ChooseEffect(e, files, target);
        e.Effects = eff;
        DoTransfer(files, target, eff);
    }

    // ---------- resizing ----------
    void SplitGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var w = Math.Clamp(SplitHost.Width - e.HorizontalChange, 260, Math.Max(300, ((FrameworkElement)SplitHost.Parent).ActualWidth - 320));
        SplitHost.Width = w;
        ContentHost.Margin = new Thickness(0, 0, w, 0);
    }

    void SplitGrip_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        S.SplitWidth = SplitHost.Width;
        SaveSoon();
    }

    // ---------- dragging a tab onto the content area ----------
    bool TabDragOverContent(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(TabFormat)) return false;
        e.Handled = true;
        bool rightHalf = e.GetPosition(ContentHost).X > ContentHost.ActualWidth / 2;
        var tab = e.Data.GetData(TabFormat) as TabState;
        if (rightHalf && tab != null && !IsTag(tab.Path))
        {
            e.Effects = DragDropEffects.Move;
            SplitDropZone.Width = Math.Max(240, ContentHost.ActualWidth / 2 - 16);
            SplitDropText.Text = $"Drop to split — open “{Display(tab.Path)}” on the right";
            SplitDropZone.Visibility = Visibility.Visible;
        }
        else
        {
            e.Effects = DragDropEffects.None;
            SplitDropZone.Visibility = Visibility.Collapsed;
        }
        return true;
    }

    bool TabDropOnContent(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(TabFormat)) return false;
        e.Handled = true;
        SplitDropZone.Visibility = Visibility.Collapsed;
        if (e.GetPosition(ContentHost).X > ContentHost.ActualWidth / 2 && e.Data.GetData(TabFormat) is TabState tab && !IsTag(tab.Path))
        {
            OpenSplit(tab.Path);
            if (Tabs.Count > 1 && !ReferenceEquals(tab, current)) CloseTab(tab); // the tab moves into the split pane
        }
        return true;
    }
}
