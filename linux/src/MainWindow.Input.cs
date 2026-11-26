using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IOPath = System.IO.Path;

namespace Orbit;

/// <summary>A menu entry laid out like the Windows app: icon · label · shortcut · tick.</summary>
public sealed class OrbitMenuItem : MenuItem
{
    readonly TextBlock tick;
    protected override Type StyleKeyOverride => typeof(MenuItem);

    public OrbitMenuItem(string header, string gesture)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,20"), MinWidth = 180 };
        g.Children.Add(new TextBlock { Text = header, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 460 });
        if (!string.IsNullOrEmpty(gesture))
        {
            var t = new TextBlock { Text = gesture, Margin = new Thickness(24, 0, 0, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("MutedBrush"));
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
        }
        tick = new TextBlock { Text = "", Classes = { "icon" }, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        tick.Bind(TextBlock.ForegroundProperty, tick.GetResourceObservable("AccentBrush"));
        Grid.SetColumn(tick, 2);
        g.Children.Add(tick);
        Header = g;
    }

    public bool IsTicked { get => tick.IsVisible; set => tick.IsVisible = value; }
    public object TipText { set => ToolTip.SetTip(this, value); }
}

public partial class MainWindow
{
    // =====================================================================
    // Event wiring (Avalonia has no Preview* events: tunnelling handlers do the same job)
    // =====================================================================
    void WireEvents()
    {
        WireChrome();
        AddHandler(KeyDownEvent, Window_PreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) => mods = e.KeyModifiers, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, Window_PreviewPointerPressed, RoutingStrategies.Tunnel, true);
        AddHandler(PointerMovedEvent, (_, e) => mods = e.KeyModifiers, RoutingStrategies.Tunnel, true);

        foreach (var list in AllLists)
        {
            list.AddHandler(PointerPressedEvent, List_PreviewPointerPressed, RoutingStrategies.Tunnel, true);
            list.AddHandler(PointerMovedEvent, List_PreviewPointerMoved, RoutingStrategies.Tunnel, true);
            list.AddHandler(PointerReleasedEvent, List_PreviewPointerReleased, RoutingStrategies.Tunnel, true);
            list.DoubleTapped += Item_DoubleTapped;
            list.ContextRequested += List_ContextRequested;
            list.SelectionChanged += List_SelectionChanged;
            list.AddHandler(KeyDownEvent, RenameBox_KeyDown, RoutingStrategies.Tunnel);
            list.AddHandler(LostFocusEvent, RenameBox_LostFocus, RoutingStrategies.Bubble);
            list.ItemsSource = view;
        }
        GridList.AddHandler(KeyDownEvent, GridList_PreviewKeyDown, RoutingStrategies.Tunnel);
        TreeList.AddHandler(KeyDownEvent, TreeList_PreviewKeyDown, RoutingStrategies.Tunnel);
        ColumnsList.AddHandler(KeyDownEvent, ColumnsList_PreviewKeyDown, RoutingStrategies.Tunnel);

        AddressArea.PointerPressed += AddressArea_MouseDown;
        AddressBox.KeyDown += AddressBox_KeyDown;
        AddressBox.LostFocus += (_, _) => EndEditAddress();
        SearchBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) SearchBox_TextChanged(); };
        SearchBox.KeyDown += SearchBox_KeyDown;
        SearchBox.LostFocus += (_, _) => { if (string.IsNullOrEmpty(SearchBox.Text)) SearchBox.IsVisible = false; };

        FindBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) FindBox_TextChanged(); };
        FindBox.AddHandler(KeyDownEvent, FindBox_PreviewKeyDown, RoutingStrategies.Tunnel);
        FindResults.DoubleTapped += FindResults_DoubleClick;
        SearchOverlay.PointerPressed += (_, e) => { if (ReferenceEquals(e.Source, SearchOverlay)) { CloseFind(); e.Handled = true; } };
        PreviewOverlay.PointerPressed += (_, e) => { if (ReferenceEquals(e.Source, PreviewOverlay)) { ClosePreview(); e.Handled = true; } };

        WireDragDrop();
        WireSplit();
        WireDropzone();
        WirePreview();
        Splitter.DragCompleted += (_, _) => { if (SidebarCol.ActualWidth >= 180) S.SidebarWidth = SidebarCol.ActualWidth; SaveSoon(); };
    }

    // =====================================================================
    // List mouse handling: click-to-clear, drag out, multi-select drag
    // =====================================================================
    void Item_DoubleTapped(object sender, TappedEventArgs e)
    {
        if (FindAncestor<TextBox>(e.Source) != null) return;
        var list = (ListBox)sender;
        if (ItemFromSource(list, e.Source) is not FileItem f || f.IsRenaming) return;
        e.Handled = true;
        if (ReferenceEquals(list, TreeList) && f.IsDir && e.KeyModifiers == KeyModifiers.None) { ToggleTreeRow(f); return; }
        var sel = Selected();
        OpenItems(sel.Contains(f) ? sel : new List<FileItem> { f });
    }

    void List_PreviewPointerPressed(object sender, PointerPressedEventArgs e)
    {
        var list = (ListBox)sender;
        var pt = e.GetCurrentPoint(list);
        mods = e.KeyModifiers;
        dragStart = null;
        dragItem = null;
        pendingSingle = null;
        if (FindAncestor<ScrollBar>(e.Source) != null || FindAncestor<TextBox>(e.Source) != null) return;

        var item = ItemFromSource(list, e.Source);
        if (pt.Properties.IsMiddleButtonPressed)
        {
            if (item != null)
            {
                e.Handled = true;
                if (item.IsDir) { AddTab(item.FullPath, false); Toast($"Opened “{item.Name}” in a new tab"); }
                else Try(() => Shell.Open(item.FullPath));
            }
            return;
        }
        if (pt.Properties.IsRightButtonPressed)
        {
            // right-click selects what's under the pointer (the menu opens on release, via ContextRequested)
            CommitAnyRename();
            if (item == null) list.UnselectAll();
            else if (!list.SelectedItems.Contains(item)) list.SelectedItem = item;
            e.Handled = true;
            return;
        }
        if (!pt.Properties.IsLeftButtonPressed) return;
        if (item == null)
        {
            if (e.ClickCount == 1)
            {
                CommitAnyRename();
                if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) == 0) list.UnselectAll();
                list.Focus();
                BeginMarquee(list, e);
            }
            return;
        }
        dragStart = e.GetPosition(list);
        dragItem = item;
        dragPress = e;
        if (e.ClickCount == 1 && e.KeyModifiers == KeyModifiers.None && list.SelectedItems.Count > 1 && list.SelectedItems.Contains(item))
        {
            pendingSingle = item; // keep the multi-selection so it can be dragged; collapse on release
            e.Handled = true;
            list.Focus();
        }
    }

    void List_PreviewPointerMoved(object sender, PointerEventArgs e)
    {
        if (marqueeList != null) { UpdateMarquee(e); return; }
        var list = (ListBox)sender;
        if (dragStart == null || dragItem == null || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
        var pos = e.GetPosition(list);
        if (Math.Abs(pos.X - dragStart.Value.X) < 5 && Math.Abs(pos.Y - dragStart.Value.Y) < 5) return;
        if (dragItem.IsRenaming) return;
        if (dragItem.IsRecycled) { dragStart = null; dragItem = null; return; }
        var press = dragPress;
        if (dragItem.InArchive)
        {
            var arcSel = list.SelectedItems.Contains(dragItem) ? Selected() : new List<FileItem> { dragItem };
            dragStart = null; dragItem = null; pendingSingle = null;
            StartArchiveDrag(list, arcSel, e);
            return;
        }
        if (dragItem.IsRemote)
        {
            dragStart = null;
            Toast("Server files can't be dragged out yet. Right-click › Download, or Copy and paste them.");
            return;
        }

        var paths = list.SelectedItems.Contains(dragItem)
            ? Selected().Select(f => f.FullPath).ToArray()
            : new[] { dragItem.FullPath };
        dragStart = null;
        dragItem = null;
        pendingSingle = null;
        StartFileDrag(list, paths, e);
    }

    void List_PreviewPointerReleased(object sender, PointerReleasedEventArgs e)
    {
        if (marqueeList != null) { EndMarquee(e); e.Handled = true; return; }
        if (pendingSingle != null)
        {
            var list = (ListBox)sender;
            list.SelectedItem = pendingSingle;
        }
        pendingSingle = null;
        dragStart = null;
        dragItem = null;
    }

    void List_ContextRequested(object sender, ContextRequestedEventArgs e)
    {
        var list = (ListBox)sender;
        if (FindAncestor<TextBox>(e.Source) != null) return;
        e.Handled = true;
        CommitAnyRename();
        // keyboard (Menu key / Shift+F10): anchor to the focused item
        bool keyboard = !e.TryGetPosition(list, out _);
        Control anchor = keyboard ? (list.ContainerFromItem(list.SelectedItem ?? new object()) as Control ?? list) : null;
        ShowItemMenu(list, anchor);
    }

    void Window_PreviewPointerPressed(object sender, PointerPressedEventArgs e)
    {
        mods = e.KeyModifiers;
        var p = e.GetCurrentPoint(this).Properties;
        if (p.IsXButton1Pressed) { GoBack(); e.Handled = true; }
        else if (p.IsXButton2Pressed) { GoForward(); e.Handled = true; }
    }

    void GridList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return;
        if ((e.KeyModifiers & KeyModifiers.Alt) != 0) return;
        var list = GridList;
        int count = view.Count;
        if (count == 0) return;
        var panel = list.GetVisualDescendants().OfType<VirtualizingWrapPanel>().FirstOrDefault();
        int cols = panel?.Columns ?? 1;
        int rows = panel?.VisibleRows ?? 4;

        int cur = -1;
        if (FocusManager?.GetFocusedElement() is ListBoxItem lbi) cur = list.IndexFromContainer(lbi);
        if (cur < 0) cur = list.SelectedIndex;

        int next;
        switch (e.Key)
        {
            case Key.Left: next = cur < 0 ? 0 : cur - 1; break;
            case Key.Right: next = cur < 0 ? 0 : cur + 1; break;
            case Key.Up: next = cur < 0 ? 0 : (cur - cols >= 0 ? cur - cols : cur); break;
            case Key.Down: next = cur < 0 ? 0 : (cur + cols < count ? cur + cols : (cur / cols < (count - 1) / cols ? count - 1 : cur)); break;
            case Key.Home: next = 0; break;
            case Key.End: next = count - 1; break;
            case Key.PageUp: next = Math.Max(0, (cur < 0 ? 0 : cur) - cols * rows); break;
            case Key.PageDown: next = Math.Min(count - 1, (cur < 0 ? 0 : cur) + cols * rows); break;
            default: return;
        }
        next = Math.Clamp(next, 0, count - 1);
        e.Handled = true;

        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        if (shift)
        {
            if (anchorIndex < 0 || anchorIndex >= count) anchorIndex = Math.Max(cur, 0);
            int a = Math.Min(anchorIndex, next), b = Math.Max(anchorIndex, next);
            list.Selection.BeginBatchUpdate();
            list.Selection.Clear();
            list.Selection.SelectRange(a, b);
            list.Selection.EndBatchUpdate();
        }
        else if (!ctrl)
        {
            list.SelectedIndex = next;
            anchorIndex = next;
        }
        FocusIndex(list, next);
    }

    static void FocusIndex(ListBox list, int i)
    {
        if (i < 0 || list.ItemCount <= i) return;
        list.ScrollIntoView(i);
        Dispatcher.UIThread.Post(() => (list.ContainerFromIndex(i) as ListBoxItem)?.Focus(), DispatcherPriority.Loaded);
    }

    void FocusList()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (AddressBox.IsKeyboardFocusWithin || SearchBox.IsKeyboardFocusWithin || FindBox.IsKeyboardFocusWithin) return;
            if (FocusManager?.GetFocusedElement() is TextBox) return;
            var l = ActiveList;
            var sel = l.SelectedItem;
            if (sel != null)
            {
                l.ScrollIntoView(sel);
                if (l.ContainerFromItem(sel) is ListBoxItem c) { c.Focus(); return; }
            }
            l.Focus();
        }, DispatcherPriority.Input);
    }

    // =====================================================================
    // Keyboard
    // =====================================================================
    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key;
        var m = e.KeyModifiers;
        mods = m;
        bool ctrl = (m & KeyModifiers.Control) != 0, shift = (m & KeyModifiers.Shift) != 0, alt = (m & KeyModifiers.Alt) != 0;
        var focused = FocusManager?.GetFocusedElement();
        bool inText = focused is TextBox;
        bool inList = ActiveList.IsKeyboardFocusWithin && !inText;

        // Find anything palette: while open, keys belong to it.
        if (findOpen)
        {
            if (key == Key.Escape) { CloseFind(); e.Handled = true; }
            return;
        }

        // Quick preview: Space / Esc close it; arrow keys keep working in the list behind it.
        if (previewOpen && (key == Key.Escape || (key == Key.Space && m == KeyModifiers.None && !inText)))
        {
            ClosePreview();
            e.Handled = true;
            return;
        }
        if (previewOpen && !inList && !inText && key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
            FocusList();

        // Keys for the split pane when it has focus
        if (SplitList.IsKeyboardFocusWithin && !inText && HandleSplitKey(key, m))
        {
            e.Handled = true;
            return;
        }

        // Reassignable shortcuts (Settings › Shortcuts)
        if (RunShortcut(key, m, inText, inList))
        {
            e.Handled = true;
            return;
        }

        // Fixed keys
        bool handled = true;
        if (alt && key == Key.D) BeginEditAddress();
        else if (ctrl && shift && key == Key.F) OpenFind();
        else if (key == Key.F3) OpenSearch();
        else if (!inText)
        {
            if (key == Key.Back) GoBack();
            else if (key == Key.Enter && inList) OpenItems(Selected());
            else if (key == Key.Apps || (shift && key == Key.F10)) ShowItemMenu(ActiveList, focused as Control);
            else if (ctrl && key == Key.A && !inList) { ActiveList.SelectAll(); ActiveList.Focus(); }
            else if (ctrl && key == Key.A && inList) { ActiveList.SelectAll(); }
            else if (key == Key.Escape)
            {
                if (searchText.Length > 0) ClearSearch(true);
                else ActiveList.UnselectAll();
            }
            else handled = false;
        }
        else handled = false;

        if (handled) e.Handled = true;
    }

    // =====================================================================
    // Address bar & breadcrumbs
    // =====================================================================
    void AddressArea_MouseDown(object sender, PointerPressedEventArgs e)
    {
        if (FindAncestor<Button>(e.Source) != null || AddressBox.IsVisible) return;
        BeginEditAddress();
        e.Handled = true;
    }

    void BeginEditAddress()
    {
        if (current == null) return;
        AddressBox.Text = IsTag(current.Path) ? "" : current.Path;
        CrumbScroll.IsVisible = false;
        AddressBox.IsVisible = true;
        Dispatcher.UIThread.Post(() => { AddressBox.Focus(); AddressBox.SelectAll(); }, DispatcherPriority.Input);
    }

    void EndEditAddress()
    {
        AddressBox.IsVisible = false;
        CrumbScroll.IsVisible = true;
    }

    void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var text = AddressBox.Text;
            EndEditAddress();
            GoToAddress(text);
            FocusList();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndEditAddress();
            FocusList();
            e.Handled = true;
        }
    }

    void GoToAddress(string text)
    {
        var t = Environment.ExpandEnvironmentVariables((text ?? "").Trim().Trim('"'));
        if (t.Length == 0) return;
        if (t.StartsWith("$HOME", StringComparison.Ordinal)) t = Home + t[5..];
        if (Sftp.IsSftp(t) || t.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) { Navigate(t); return; }
        if (t.StartsWith(@"\\")) t = "smb://" + t[2..].Replace('\\', '/');
        if (NetMount.IsNetUri(t)) { Navigate(t); return; }
        if (t.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(t, UriKind.Absolute, out var fu)) t = fu.LocalPath;
        else if (t is "trash:" or "trash:/" or "trash:///") { Navigate(RecycleBin.Path); return; }
        else if (t.Contains("://"))
        {
            Try(() => Shell.Open(t));
            return;
        }
        if (t == "~") t = Home;
        else if (t.StartsWith("~/")) t = IOPath.Combine(Home, t[2..]);
        try
        {
            if (!IOPath.IsPathRooted(t) && current != null && !IsTag(current.Path) && !IsRemote(current.Path) && !Archives.IsArc(current.Path)) t = IOPath.Combine(current.Path, t);
        }
        catch { Toast($"“{text}” isn't a valid path"); return; }
        Navigate(t);
    }

    void Crumb_Click(object sender, RoutedEventArgs e)
    {
        if (((Control)sender).Tag is Crumb c && c.Path != null && !Same(c.Path, current?.Path))
        {
            var child = current?.Path;
            Navigate(c.Path, child != null && !IsTag(child) ? ChildToward(c.Path, child) : null);
        }
    }

    static string ChildToward(string ancestor, string descendant)
    {
        var a = ancestor.TrimEnd('/');
        if (!descendant.StartsWith(a + "/", StringComparison.Ordinal)) return null;
        var rest = descendant[(a.Length + 1)..];
        var first = rest.Split('/')[0];
        return first.Length == 0 ? null : (a.Length == 0 ? "/" : a + "/") + first;
    }

    void CrumbChevron_Click(object sender, RoutedEventArgs e)
    {
        if (((Control)sender).Tag is not Crumb c || c.Path == null || IsTag(c.Path)) return;
        var anchor = (Control)sender;
        if (IsRemote(c.Path)) { ShowRemoteSubfolders(anchor, c.Path); return; }
        if (Archives.IsArc(c.Path)) { ShowArchiveSubfolders(anchor, c.Path); return; }
        var menu = new ContextMenu { MaxHeight = 520 };
        List<string> dirs;
        try
        {
            var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
            dirs = new DirectoryInfo(c.Path).EnumerateDirectories("*", opts)
                .Where(d => S.ShowHidden || !d.Name.StartsWith('.'))
                .Select(d => d.FullName)
                .ToList();
            dirs.Sort((a, b) => Natural.Compare(IOPath.GetFileName(a), IOPath.GetFileName(b)));
        }
        catch (Exception ex) { Toast(ex.Message); return; }
        if (dirs.Count == 0) menu.Items.Add(MI("No subfolders", null, null, enabled: false));
        foreach (var d in dirs.Take(400))
        {
            var target = d;
            menu.Items.Add(MI(IOPath.GetFileName(d), "", () => Navigate(target)));
        }
        OpenMenu(menu, anchor, true);
    }

    // =====================================================================
    // Search / filter
    // =====================================================================
    void SearchBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SearchBox.IsVisible && searchText.Length == 0) { SearchBox.IsVisible = false; return; }
        OpenSearch();
    }

    void OpenSearch()
    {
        SearchBox.IsVisible = true;
        Dispatcher.UIThread.Post(() => { SearchBox.Focus(); SearchBox.SelectAll(); }, DispatcherPriority.Input);
    }

    void SearchBox_TextChanged()
    {
        if (suppressSearch) return;
        searchText = (SearchBox.Text ?? "").Trim();
        RefreshView();
        UpdateStatus();
    }

    void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { ClearSearch(true); e.Handled = true; }
        else if (e.Key == Key.Enter || e.Key == Key.Down)
        {
            var l = ActiveList;
            if (l.ItemCount > 0) { l.SelectedIndex = 0; FocusIndex(l, 0); }
            e.Handled = true;
        }
    }

    void ClearSearch(bool focusList)
    {
        ResetFilters();
        bool had = searchText.Length > 0;
        suppressSearch = true;
        SearchBox.Text = "";
        suppressSearch = false;
        searchText = "";
        if (!SearchBox.IsKeyboardFocusWithin) SearchBox.IsVisible = false;
        if (had) { RefreshView(); UpdateStatus(); }
        if (focusList) { SearchBox.IsVisible = false; FocusList(); }
    }

    // =====================================================================
    // View / sort / hidden
    // =====================================================================
    void HiddenToggle_Click(object sender, RoutedEventArgs e)
    {
        S.ShowHidden = HiddenToggle.IsChecked == true;
        RefreshView();
        UpdateStatus();
        SaveSoon();
    }

    void ToggleHidden()
    {
        HiddenToggle.IsChecked = !(HiddenToggle.IsChecked == true);
        HiddenToggle_Click(null, null);
        Toast(S.ShowHidden ? "Showing hidden items" : "Hiding hidden items");
    }

    void Sort_Click(object sender, RoutedEventArgs e) => OpenMenu(SortMenu(), (Control)sender, true, top: true);

    ContextMenu SortMenu()
    {
        var menu = new ContextMenu();
        foreach (var (key, label) in new[] { ("name", "Name"), ("date", "Date modified"), ("size", "Size"), ("type", "Type") })
        {
            var k = key;
            var mi = MI(label, null, () => { S.SortBy = k; ApplySort(); });
            mi.IsTicked = S.SortBy == k;
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var asc = MI("Ascending", "", () => { S.SortDesc = false; ApplySort(); });
        asc.IsTicked = !S.SortDesc;
        var desc = MI("Descending", "", () => { S.SortDesc = true; ApplySort(); });
        desc.IsTicked = S.SortDesc;
        menu.Items.Add(asc);
        menu.Items.Add(desc);
        return menu;
    }

    void ApplySort()
    {
        RefreshView();
        BuildDetailsHeader();
        SaveSoon();
    }

    // =====================================================================
    // Clipboard (works with Dolphin, Files, Thunar…: text/uri-list + the GNOME and KDE cut markers)
    // =====================================================================
    const string UriList = "text/uri-list", GnomeFiles = "x-special/gnome-copied-files", KdeCut = "application/x-kde-cutselection";

    async void SetClipboardText(string text)
    {
        try { await Clipboard!.SetTextAsync(text); Toast("Path copied"); } catch { Toast("Couldn't access the clipboard"); }
    }

    async void CopyPaths(IEnumerable<string> paths)
    {
        var list = paths.Where(p => p != null && !IsTag(p)).ToList();
        if (list.Count == 0) return;
        try
        {
            await Clipboard!.SetTextAsync(string.Join("\n", list));
            Toast(list.Count == 1 ? "Path copied" : $"{list.Count} paths copied");
        }
        catch { }
    }

    async Task<bool> PutFilesOnClipboard(IReadOnlyList<string> paths, bool cut)
    {
        try
        {
            var data = new DataObject();
            var uris = string.Join("\r\n", paths.Select(IconService.FileUri)) + "\r\n";
            data.Set(UriList, Encoding.UTF8.GetBytes(uris));
            data.Set(GnomeFiles, Encoding.UTF8.GetBytes((cut ? "cut\n" : "copy\n") + string.Join("\n", paths.Select(IconService.FileUri))));
            data.Set(KdeCut, Encoding.UTF8.GetBytes(cut ? "1" : "0"));
            data.Set(DataFormats.Text, string.Join("\n", paths));
            var items = new List<IStorageItem>();
            foreach (var p in paths)
            {
                IStorageItem it = Directory.Exists(p) ? await StorageProvider.TryGetFolderFromPathAsync(p) : await StorageProvider.TryGetFileFromPathAsync(p);
                if (it != null) items.Add(it);
            }
            if (items.Count > 0) data.Set(DataFormats.Files, items);
            await Clipboard!.SetDataObjectAsync(data);
            clipboardHasFiles = true;
            UpdateCommandBar();
            return true;
        }
        catch (Exception ex) { AppSettings.LogError(ex); return false; }
    }

    async void CopyToClipboard(bool cut)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        if (sel.Any(f => f.IsRecycled)) { Toast(RecycledHint); return; }
        if (sel.Any(f => f.InArchive)) { if (cut) Toast(ReadOnlyArchive); else CopyArchivedToClipboard(sel); return; }
        if (sel.Any(f => f.IsRemote))
        {
            if (cut) Toast("Cut isn't available for server files. Use Copy, then Delete.");
            else CopyRemoteToClipboard(sel);
            return;
        }
        if (!await PutFilesOnClipboard(sel.Select(f => f.FullPath).ToList(), cut)) { Toast("Couldn't access the clipboard"); return; }

        foreach (var p in cutPaths) if (index.TryGetValue(p, out var old)) old.IsCut = false;
        cutPaths.Clear();
        if (cut)
            foreach (var f in sel) { cutPaths.Add(f.FullPath); f.IsCut = true; }
        Toast($"{(cut ? "Cut" : "Copied")} {(sel.Count == 1 ? $"“{sel[0].Name}”" : sel.Count + " items")}");
    }

    /// <summary>Files on the clipboard (from Orbit or another file manager) and whether they were cut.</summary>
    async Task<(string[] files, bool move)> ClipboardFiles()
    {
        try
        {
            var cb = Clipboard!;
            var formats = await cb.GetFormatsAsync();
            string[] files = null;
            bool move = false;
            if (formats.Contains(GnomeFiles) && await cb.GetDataAsync(GnomeFiles) is { } g)
            {
                var lines = AsText(g).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (lines.Length > 1) { move = lines[0] == "cut"; files = lines.Skip(1).Select(UriToPath).Where(p => p != null).ToArray(); }
            }
            if ((files == null || files.Length == 0) && formats.Contains(UriList) && await cb.GetDataAsync(UriList) is { } u)
                files = AsText(u).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                 .Where(l => !l.StartsWith('#')).Select(UriToPath).Where(p => p != null).ToArray();
            if ((files == null || files.Length == 0) && formats.Contains(DataFormats.Files) && await cb.GetDataAsync(DataFormats.Files) is IEnumerable<IStorageItem> items)
                files = items.Select(i => i.TryGetLocalPath()).Where(p => p != null).ToArray();
            if (formats.Contains(KdeCut) && await cb.GetDataAsync(KdeCut) is { } k) move |= AsText(k).Trim() == "1";
            return (files ?? Array.Empty<string>(), move);
        }
        catch { return (Array.Empty<string>(), false); }
    }

    static string AsText(object o) => o switch { byte[] b => Encoding.UTF8.GetString(b), string s => s, _ => o?.ToString() ?? "" };

    static string UriToPath(string u)
    {
        u = u.Trim().TrimEnd('\0');
        if (u.StartsWith('/')) return u;
        return Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null;
    }

    async Task<bool> CanPaste() => (await ClipboardFiles()).files.Length > 0;

    async void Paste(string targetDir)
    {
        if (targetDir == null || IsTag(targetDir)) return;
        if (Archives.IsArc(targetDir)) { Toast(ReadOnlyArchive); return; }
        var (files, move) = await ClipboardFiles();
        if (files.Length == 0) { Toast("Nothing to paste"); return; }
        if (IsRemote(targetDir)) { UploadTo(files, targetDir); return; }

        if (move)
        {
            if (files.All(f => Same(SafeParent(f), targetDir))) { ClearCut(); return; }
            FileOps.Move(files, targetDir, this, () => Dispatcher.UIThread.Post(async () =>
            {
                try { await Clipboard!.ClearAsync(); } catch { }
                clipboardHasFiles = false;
                UpdateCommandBar();
                ClearCut();
            }));
        }
        else
        {
            FileOps.Copy(files, targetDir, this, files.All(f => Same(SafeParent(f), targetDir)));
        }
    }

    void ClearCut()
    {
        foreach (var p in cutPaths) if (index.TryGetValue(p, out var f)) f.IsCut = false;
        cutPaths.Clear();
    }

    // =====================================================================
    // Create / rename / delete
    // =====================================================================
    void NewFolder() => CreateNew(true);
    void NewTextFile() => CreateNew(false);

    void CreateNew(bool folder, string baseName = null, string ext = ".txt")
    {
        if (current == null || IsTag(current.Path)) return;
        if (Archives.IsArc(current.Path)) { Toast(ReadOnlyArchive); return; }
        if (IsRemote(current.Path)) { CreateNewRemote(folder, baseName, ext); return; }
        string name = folder ? UniqueName(current.Path, baseName ?? "New Folder", "") : UniqueName(current.Path, baseName ?? "New Text Document", ext);
        var p = IOPath.Combine(current.Path, name);
        try
        {
            if (folder) Directory.CreateDirectory(p);
            else using (File.Create(p)) { }
        }
        catch (Exception ex) { Toast(ex.Message); return; }

        var f = FileItem.TryFromPath(p);
        if (f == null) return;
        if (!index.ContainsKey(p)) { items.Add(f); index[p] = f; InsertSorted(f); AfterViewChanged(); }
        else f = index[p];
        ClearSearch(false);
        ActiveList.SelectedItem = f;
        BeginRename(f);
        UpdateStatus();
    }

    void BeginRenameSelected()
    {
        var sel = Selected();
        if (sel.Count == 1) BeginRename(sel[0]);
    }

    void BeginRename(FileItem f)
    {
        CommitAnyRename();
        var l = ActiveList;
        l.ScrollIntoView(f);
        f.EditName = f.Name;
        f.IsRenaming = true;
        // focus the name box once it's on screen, with the name (not the extension) selected
        void Focus(int tries)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!f.IsRenaming) return;
                var box = (l.ContainerFromItem(f) as Control)?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Classes.Contains("rename"));
                if (box == null || !box.IsEffectivelyVisible) { if (tries > 0) Focus(tries - 1); return; }
                box.Focus();
                int dot = f.IsDir ? -1 : (box.Text ?? "").LastIndexOf('.');
                box.SelectionStart = 0;
                box.SelectionEnd = dot > 0 ? dot : (box.Text ?? "").Length;
            }, DispatcherPriority.Loaded);
        }
        Focus(5);
    }

    void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Source is not TextBox tb || !tb.Classes.Contains("rename") || tb.DataContext is not FileItem f) return;
        if (e.Key == Key.Enter) { CommitRename(f); FocusList(); e.Handled = true; }
        else if (e.Key == Key.Escape) { f.IsRenaming = false; FocusList(); e.Handled = true; }
        else if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown) e.Handled = true; // never let keystrokes move the list while typing a name
    }

    void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox tb && tb.Classes.Contains("rename") && tb.DataContext is FileItem f && f.IsRenaming && IsActive)
            CommitRename(f);
    }

    void CommitAnyRename()
    {
        foreach (var f in items.Where(i => i.IsRenaming).ToList()) CommitRename(f);
        foreach (var f in treeRows.Where(i => i.IsRenaming).ToList()) CommitRename(f);
    }

    void CommitRename(FileItem f)
    {
        if (!f.IsRenaming) return;
        f.IsRenaming = false;
        var newName = (f.EditName ?? "").Trim();
        if (newName.Length == 0 || newName == f.Name) return;
        if (newName.Contains('/') || newName.Contains('\0') || newName is "." or "..")
        {
            Toast("A name can't contain “/” or be “.” or “..”");
            return;
        }
        if (f.IsRecycled) { Toast(RecycledHint); return; }
        if (f.InArchive) { Toast(ReadOnlyArchive); return; }
        if (f.IsRemote) { CommitRemoteRename(f, newName); return; }
        var dir = SafeParent(f.FullPath);
        if (dir == null) return;
        var src = f.FullPath;
        var dest = IOPath.Combine(dir, newName);
        if (File.Exists(dest) || Directory.Exists(dest))
        {
            Toast($"“{newName}” already exists here");
            return;
        }
        try
        {
            if (f.IsDir && !f.IsLink) Directory.Move(src, dest);
            else File.Move(src, dest);
        }
        catch (Exception ex)
        {
            Toast("Couldn't rename: " + ex.Message);
            return;
        }

        index.Remove(src);
        f.Rebase(newName, dest);
        index[dest] = f;
        MovePathReferences(src, dest);
        if (view.Remove(f)) InsertSorted(f);
        ActiveList.SelectedItem = f;
        ActiveList.ScrollIntoView(f);
    }

    void MovePathReferences(string oldPath, string newPath)
    {
        string Map(string p) =>
            Same(p, oldPath) ? newPath :
            p.StartsWith(oldPath + "/", StringComparison.Ordinal) ? newPath + p[oldPath.Length..] : null;

        bool changed = false;
        foreach (var k in S.Tags.Keys.ToList())
        {
            var m = Map(k);
            if (m != null) { var v = S.Tags[k]; S.Tags.Remove(k); S.Tags[m] = v; changed = true; }
        }
        for (int i = 0; i < S.Bookmarks.Count; i++) { var m = Map(S.Bookmarks[i]); if (m != null) { S.Bookmarks[i] = m; changed = true; } }
        for (int i = 0; i < S.Recent.Count; i++) { var m = Map(S.Recent[i]); if (m != null) { S.Recent[i] = m; changed = true; } }
        if (changed) { RebuildBookmarks(); RebuildRecent(); SaveSoon(); }
    }

    async void DeleteSelected(bool permanent)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        if (sel.Any(f => f.IsRecycled)) { DeleteRecycled(sel); return; }
        if (sel.Any(f => f.InArchive)) { Toast(ReadOnlyArchive); return; }
        if (sel.Any(f => f.IsRemote)) { DeleteRemote(sel); return; }
        if (permanent)
        {
            var msg = sel.Count == 1 ? $"Permanently delete “{sel[0].Name}”?\n\nThis can't be undone." : $"Permanently delete {sel.Count} items?\n\nThis can't be undone.";
            if (!await Dialog.Ask(this, "Delete permanently", msg, "Delete", "Cancel", warning: true, yesIsDefault: false)) return;
        }
        // pre-select the neighbour so focus lands somewhere sensible after the item vanishes
        var l = ActiveList;
        int idx = view.IndexOf(sel[0]);
        FileOps.Delete(sel.Select(f => f.FullPath), !permanent, this, () => Dispatcher.UIThread.Post(() =>
        {
            if (l.SelectedItems.Count == 0 && l.ItemCount > 0)
            {
                int i = Math.Clamp(idx, 0, l.ItemCount - 1);
                l.SelectedIndex = i;
                FocusIndex(l, i);
            }
        }, DispatcherPriority.Background));
    }

    /// <summary>Orbit's own Properties window for the selection (or the current folder when nothing is selected).</summary>
    void ShowProperties()
    {
        var sel = Selected();
        if (sel.Count == 0)
        {
            if (current == null || IsTag(current.Path)) return;
            var here = IsRemote(current.Path)
                ? FileItem.Remote(current.Path, Display(current.Path), true, -1, default)
                : FileItem.TryFromPath(current.Path);
            if (here == null) return;
            sel = new List<FileItem> { here };
        }
        new PropertiesWindow(this, sel).Show(this);
    }

    void ShowPropertiesFor(string path)
    {
        var item = path == null || IsTag(path) || IsRemote(path) ? null : FileItem.TryFromPath(path);
        if (item != null) new PropertiesWindow(this, new List<FileItem> { item }).Show(this);
    }

    internal void ShowSystemProperties(string path)
    {
        if (path != null && !IsTag(path) && !IsRemote(path)) Try(() => Shell.Properties(path));
    }

    internal void AfterAttributesChanged() => LoadCurrent(keepSelection: true);

    // =====================================================================
    // Bookmarks & tags
    // =====================================================================
    void ToggleBookmark(string path)
    {
        path ??= current?.Path;
        if (path == null || IsTag(path)) return;
        int i = S.Bookmarks.FindIndex(b => Same(b, path));
        if (i >= 0) { S.Bookmarks.RemoveAt(i); Toast($"Removed “{Display(path)}” from bookmarks"); }
        else { S.Bookmarks.Add(path); Toast($"Bookmarked “{Display(path)}”"); }
        RebuildBookmarks();
        UpdateStar();
        SaveSoon();
    }

    void ApplyTags(FileItem f) => f.SetTags(S.Tags.TryGetValue(f.FullPath, out var l) ? l : null);

    void SetTag(List<FileItem> sel, string tag, bool on)
    {
        foreach (var f in sel)
        {
            var list = S.Tags.TryGetValue(f.FullPath, out var l) ? l.ToList() : new List<string>();
            if (on && !list.Contains(tag)) list.Add(tag);
            if (!on) list.Remove(tag);
            list = TagPalette.Names.Where(list.Contains).ToList();
            if (list.Count == 0) S.Tags.Remove(f.FullPath); else S.Tags[f.FullPath] = list;
            f.SetTags(list);
        }
        AfterTagChange();
    }

    void ClearTags(List<FileItem> sel)
    {
        foreach (var f in sel) { S.Tags.Remove(f.FullPath); f.SetTags(null); }
        AfterTagChange();
    }

    void RemoveTagEverywhere(string tag)
    {
        foreach (var k in S.Tags.Keys.ToList())
        {
            S.Tags[k].Remove(tag);
            if (S.Tags[k].Count == 0) S.Tags.Remove(k);
        }
        foreach (var f in items) ApplyTags(f);
        if (current != null && current.Path == "tag:" + tag) Navigate(Home);
        AfterTagChange();
    }

    void AfterTagChange()
    {
        RebuildTags();
        SaveSoon();
        if (IsTag(current?.Path)) LoadCurrent(keepSelection: true);
    }

    // =====================================================================
    // Context menu
    // =====================================================================
    OrbitMenuItem MI(string header, string glyph, Action act, string gesture = null, bool enabled = true)
    {
        var mi = new OrbitMenuItem(header, gesture) { IsEnabled = enabled };
        if (glyph != null)
            mi.Icon = new TextBlock
            {
                Text = glyph,
                Classes = { "icon" },
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        if (act != null) mi.Click += (_, e) => { e.Handled = true; Dispatcher.UIThread.Post(() => Try(act)); };
        return mi;
    }

    /// <summary>Opens a menu below <paramref name="anchor"/>, or at the pointer when <paramref name="below"/> isn't set.</summary>
    void OpenMenu(ContextMenu menu, Control anchor, bool? below, bool top = false)
    {
        anchor ??= ActiveList;
        menu.Placement = below == true ? (top ? PlacementMode.TopEdgeAlignedLeft : PlacementMode.BottomEdgeAlignedLeft) : PlacementMode.Pointer;
        menu.PlacementTarget = anchor;
        menu.Open(anchor);
    }

    async void ShowItemMenu(ListBox list, Control anchor)
    {
        if (current == null) return;
        var sel = Selected();
        var menu = new ContextMenu();
        bool tagView = IsTag(current.Path);
        if (RecycleBin.Is(current.Path)) { ShowRecycleMenu(list, anchor, sel); return; }
        if (Archives.IsArc(current.Path)) { ShowArchiveMenu(list, anchor, sel); return; }
        if (IsRemote(current.Path)) { ShowRemoteMenu(list, anchor, sel); return; }
        bool canPaste = await CanPaste();

        if (sel.Count == 0)
        {
            if (!tagView)
            {
                var nw = MI("New", "", null);
                nw.Items.Add(MI("Folder", "", NewFolder, "Ctrl+Shift+N"));
                nw.Items.Add(MI("Text document", "", NewTextFile));
                menu.Items.Add(nw);
                menu.Items.Add(MI("Paste", "", () => Paste(current.Path), "Ctrl+V", canPaste));
                menu.Items.Add(new Separator());
            }
            menu.Items.Add(MI("Refresh", "", () => LoadCurrent(keepSelection: true), "F5"));
            var hid = MI("Show hidden items", "", ToggleHidden, "Ctrl+H");
            hid.IsTicked = S.ShowHidden;
            menu.Items.Add(hid);
            menu.Items.Add(MI("Select all", "", () => ActiveList.SelectAll(), "Ctrl+A"));
            if (!tagView)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(MI("Copy path", "", () => SetClipboardText(current.Path), "Ctrl+Shift+C"));
                menu.Items.Add(MI("Open in Terminal", "", () => Shell.OpenTerminal(current.Path)));
                menu.Items.Add(MI("Open in File Manager", "", () => Shell.OpenInFileManager(current.Path)));
                menu.Items.Add(new Separator());
                menu.Items.Add(MI("Properties", "", () => ShowPropertiesFor(current.Path), "Alt+Enter"));
            }
        }
        else
        {
            var first = sel[0];
            bool allDirs = sel.All(f => f.IsDir);
            menu.Items.Add(MI("Open", "", () => OpenItems(sel), "Enter"));
            if (allDirs) menu.Items.Add(MI(sel.Count == 1 ? "Open in new tab" : "Open in new tabs", "", () => OpenItems(sel, true)));
            if (allDirs && sel.Count == 1) menu.Items.Add(MI("Open in split view", "", () => OpenSplit(first.FullPath), "Ctrl+\\"));
            if (sel.Count == 1 && !first.IsDir) menu.Items.Add(MI("Open with…", "", () => OpenWith(first.FullPath)));
            AddArchiveItemsToMenu(menu, sel);
            if (tagView) menu.Items.Add(MI("Show in folder", "", () => Navigate(SafeParent(first.FullPath), first.FullPath)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Cut", "", () => CopyToClipboard(true), "Ctrl+X"));
            menu.Items.Add(MI("Copy", "", () => CopyToClipboard(false), "Ctrl+C"));
            if (sel.Count == 1 && first.IsDir)
                menu.Items.Add(MI("Paste into folder", "", () => Paste(first.FullPath), null, canPaste));
            menu.Items.Add(MI("Copy path", "", () => CopyPaths(sel.Select(f => f.FullPath)), "Ctrl+Shift+C"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Rename", "", () => BeginRename(first), "F2", sel.Count == 1));
            menu.Items.Add(MI("Delete", "", () => DeleteSelected(false), "Del"));
            menu.Items.Add(new Separator());

            var tags = MI("Tags", "", null);
            foreach (var t in TagPalette.Names)
            {
                var tag = t;
                bool all = sel.All(f => f.Tags.Contains(tag));
                var mi = MI(tag, null, () => SetTag(sel, tag, !all));
                mi.Icon = new Avalonia.Controls.Shapes.Ellipse { Width = 10, Height = 10, Fill = TagPalette.Brush(tag) };
                mi.IsTicked = all;
                tags.Items.Add(mi);
            }
            tags.Items.Add(new Separator());
            tags.Items.Add(MI("Clear tags", "", () => ClearTags(sel), null, sel.Any(f => f.Tags.Count > 0)));
            menu.Items.Add(tags);

            if (sel.Count == 1 && first.IsDir)
            {
                bool marked = S.Bookmarks.Any(b => Same(b, first.FullPath));
                menu.Items.Add(MI(marked ? "Remove bookmark" : "Add bookmark", marked ? "" : "", () => ToggleBookmark(first.FullPath)));
                menu.Items.Add(MI(IsPinned(first.FullPath) ? "Remove from Places" : "Add to Places", "", () => TogglePin(first.FullPath)));
            }
            menu.Items.Add(MI("Add to Dropzone", "", () => AddToDropzone(sel.Select(f => f.FullPath))));
            menu.Items.Add(MI("Show in File Manager", "", () => Shell.ShowInFileManager(first.FullPath)));
            if (sel.Count == 1 && first.IsDir)
                menu.Items.Add(MI("Open in Terminal", "", () => Shell.OpenTerminal(first.FullPath)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Properties", "", ShowProperties, "Alt+Enter"));
        }
        OpenMenu(menu, anchor ?? list, anchor != null);
    }

    void OpenWith(string path)
    {
        if (path == null || IsRemote(path) || !File.Exists(path)) return;
        new OpenWithDialog(path).ShowDialog(this);
    }

    // =====================================================================
    // Dropzone
    // =====================================================================
    void LoadDropzone()
    {
        DropItems.Clear();
        foreach (var p in S.Dropzone)
        {
            var f = FileItem.TryFromPath(p);
            if (f != null) DropItems.Add(f);
        }
        S.Dropzone = DropItems.Select(f => f.FullPath).ToList();
        UpdateDropzoneChrome();
    }

    void UpdateDropzoneChrome()
    {
        DzEmpty.IsVisible = DropItems.Count == 0;
        DzClearBtn.IsVisible = DropItems.Count > 0;
    }

    void AddToDropzone(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var p in paths)
        {
            if (S.Dropzone.Any(x => Same(x, p))) continue;
            var f = FileItem.TryFromPath(p);
            if (f == null) continue;
            S.Dropzone.Add(f.FullPath);
            DropItems.Add(f);
            added++;
        }
        if (added > 0) Toast(added == 1 ? "Added to Dropzone" : $"Added {added} items to Dropzone");
        UpdateDropzoneChrome();
        SaveSoon();
    }

    void PruneDropzone()
    {
        foreach (var f in DropItems.ToList())
            if (!File.Exists(f.FullPath) && !Directory.Exists(f.FullPath)) DropItems.Remove(f);
        S.Dropzone = DropItems.Select(f => f.FullPath).ToList();
        UpdateDropzoneChrome();
        SaveSoon();
    }

    void ShowDropzoneMenu(Control anchor, FileItem f)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MI("Open", "", () => { if (f.IsDir) Navigate(f.FullPath); else Shell.Open(f.FullPath); }));
        menu.Items.Add(MI("Show in folder", "", () => Navigate(f.IsDir ? f.FullPath : SafeParent(f.FullPath), f.FullPath)));
        menu.Items.Add(MI("Copy here", "", () => FileOps.Copy(new[] { f.FullPath }, current.Path, this, Same(SafeParent(f.FullPath), current.Path)), null, !IsTag(current?.Path)));
        menu.Items.Add(MI("Move here", "", () => FileOps.Move(new[] { f.FullPath }, current.Path, this, () => Dispatcher.UIThread.Post(PruneDropzone)), null, !IsTag(current?.Path) && !Same(SafeParent(f.FullPath), current?.Path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Remove from Dropzone", "", () => RemoveDz(f)));
        OpenMenu(menu, anchor, null);
    }

    void DzRemove_Click(object sender, RoutedEventArgs e)
    {
        if (((Control)sender).Tag is FileItem f) RemoveDz(f);
        e.Handled = true;
    }

    void RemoveDz(FileItem f)
    {
        DropItems.Remove(f);
        S.Dropzone.RemoveAll(p => Same(p, f.FullPath));
        UpdateDropzoneChrome();
        SaveSoon();
    }

    void DzClear_Click(object sender, RoutedEventArgs e)
    {
        DropItems.Clear();
        S.Dropzone.Clear();
        UpdateDropzoneChrome();
        SaveSoon();
    }

    static string SafeParent(string p) { try { return IOPath.GetDirectoryName(p); } catch { return null; } }
}
