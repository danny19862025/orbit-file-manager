using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IOPath = System.IO.Path;

namespace Orbit;

/// <summary>Four layouts over the same folder: Grid, Details, Miller Columns and Tree.</summary>
public partial class MainWindow
{
    public static readonly (string Id, string Label, string Glyph)[] ViewModes =
    {
        ("grid", "Grid", ""), ("details", "Details", ""), ("columns", "Columns", ""), ("tree", "Tree", ""),
    };

    static string ViewGlyph(string mode) => ViewModes.FirstOrDefault(v => v.Id == mode).Glyph ?? "";

    // =====================================================================
    // Switching
    // =====================================================================
    void ApplyViewVisibility()
    {
        var m = S.ViewMode;
        GridList.Visibility = m == "grid" ? Visibility.Visible : Visibility.Collapsed;
        DetailsList.Visibility = m == "details" ? Visibility.Visible : Visibility.Collapsed;
        TreeHost.Visibility = m == "tree" ? Visibility.Visible : Visibility.Collapsed;
        ColumnsHost.Visibility = m == "columns" ? Visibility.Visible : Visibility.Collapsed;
        ViewBtn.Content = ViewGlyph(m);
        ViewBtn.ToolTip = $"View: {ViewModes.First(v => v.Id == m).Label} — click to switch";
    }

    /// <summary>Points the visible list at the current folder.</summary>
    void BindActiveView()
    {
        ApplyViewVisibility();
        var active = ActiveList;
        foreach (var l in new ListBox[] { GridList, DetailsList, ColumnsList, TreeList })
            if (!ReferenceEquals(l, active)) l.ItemsSource = null;

        if (S.ViewMode == "tree") RebuildTree();
        else active.ItemsSource = view;

        if (S.ViewMode == "columns")
        {
            BuildAncestorColumns();
            UpdateColumnPreview();
        }
        else AncestorColumns.Children.Clear();
        FindChild<ScrollViewer>(active)?.ScrollToTop();
    }

    void SetView(string mode)
    {
        if (S.ViewMode == mode || ViewModes.All(v => v.Id != mode)) return;
        var keep = Selected().Select(f => f.FullPath).ToList();
        S.ViewMode = mode;
        IconService.CancelPending();
        BindActiveView();
        var list = ActiveList;
        foreach (var item in list.Items.Cast<FileItem>())
            if (keep.Contains(item.FullPath, StringComparer.OrdinalIgnoreCase)) list.SelectedItems.Add(item);
        if (list.SelectedItems.Count > 0) list.ScrollIntoView(list.SelectedItems[0]);
        UpdateSelText();
        FocusList();
        SaveSoon();
    }

    void ViewToggle_Click(object sender, RoutedEventArgs e)
    {
        int i = Array.FindIndex(ViewModes, v => v.Id == S.ViewMode);
        SetView(ViewModes[(i + 1) % ViewModes.Length].Id);
        UpdateCommandBar();
    }

    /// <summary>Called whenever sorting, filtering or the folder's contents change.</summary>
    void AfterViewChanged()
    {
        if (S.ViewMode == "tree") ScheduleTreeRebuild();
    }

    /// <summary>Children of any folder (local or SFTP), filtered and sorted like the main view.</summary>
    List<FileItem> LoadChildrenSorted(string path)
    {
        List<FileItem> list;
        try { list = IsRemote(path) ? Sftp.List(path) : Enumerate(path); }
        catch { return new List<FileItem>(); }
        bool hidden = S.ShowHidden;
        list = list.Where(f => hidden || !f.IsHidden).ToList();
        var cmp = new ItemComparer(S.SortBy, S.SortDesc);
        list.Sort(cmp.Compare);
        return list;
    }

    // =====================================================================
    // Tree View
    // =====================================================================
    ObservableCollection<FileItem> treeRows = new();
    readonly HashSet<string> treeExpanded = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<FileItem>> treeCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, FileSystemWatcher> treeWatchers = new(StringComparer.OrdinalIgnoreCase);
    string treeRoot;
    DispatcherTimer treeDebounce;

    void ScheduleTreeRebuild()
    {
        treeDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => { treeDebounce.Stop(); RebuildTree(); }, Dispatcher);
        treeDebounce.Stop();
        treeDebounce.Start();
    }

    void RebuildTree()
    {
        if (view == null || S.ViewMode != "tree") return;
        if (!Same(treeRoot, current?.Path))
        {
            treeRoot = current?.Path;
            treeExpanded.Clear();
            treeCache.Clear();
            StopTreeWatchers();
        }
        var keep = new HashSet<string>(TreeList.SelectedItems.Cast<FileItem>().Select(f => f.FullPath), StringComparer.OrdinalIgnoreCase);
        var rows = new List<FileItem>();
        foreach (FileItem f in view) AddTreeRow(rows, f, 0);
        treeRows = new ObservableCollection<FileItem>(rows);
        TreeList.ItemsSource = treeRows;
        foreach (var r in rows) if (keep.Contains(r.FullPath)) TreeList.SelectedItems.Add(r);
    }

    void AddTreeRow(List<FileItem> rows, FileItem f, int depth)
    {
        f.Depth = depth;
        f.IsExpanded = f.IsDir && treeExpanded.Contains(f.FullPath);
        rows.Add(f);
        if (!f.IsExpanded) return;
        foreach (var c in TreeChildren(f.FullPath)) AddTreeRow(rows, c, depth + 1);
    }

    List<FileItem> TreeChildren(string path)
    {
        if (treeCache.TryGetValue(path, out var cached)) return cached;
        var list = LoadChildrenSorted(path);
        foreach (var f in list) ApplyTags(f);
        treeCache[path] = list;
        WatchTreeFolder(path);
        return list;
    }

    void TreeChevron_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is FileItem f) ToggleTreeRow(f);
        e.Handled = true;
    }

    async void ToggleTreeRow(FileItem f)
    {
        if (!f.IsDir) return;
        int i = treeRows.IndexOf(f);
        if (i < 0) return;
        if (f.IsExpanded)
        {
            // collapse: drop every deeper row that follows
            while (i + 1 < treeRows.Count && treeRows[i + 1].Depth > f.Depth)
            {
                var child = treeRows[i + 1];
                if (child.IsDir) treeExpanded.Remove(child.FullPath);
                treeRows.RemoveAt(i + 1);
            }
            f.IsExpanded = false;
            treeExpanded.Remove(f.FullPath);
            return;
        }
        var path = f.FullPath;
        if (!treeCache.ContainsKey(path))
        {
            var loaded = await Task.Run(() => LoadChildrenSorted(path));
            foreach (var c in loaded) ApplyTags(c);
            treeCache[path] = loaded;
            WatchTreeFolder(path);
        }
        i = treeRows.IndexOf(f);
        if (i < 0 || f.IsExpanded) return;
        f.IsExpanded = true;
        treeExpanded.Add(path);
        var rows = new List<FileItem>();
        foreach (var c in treeCache[path]) AddTreeRow(rows, c, f.Depth + 1);
        for (int k = 0; k < rows.Count; k++) treeRows.Insert(i + 1 + k, rows[k]);
        if (rows.Count == 0) Toast($"“{f.Name}” is empty");
    }

    void TreeList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || Keyboard.Modifiers != ModifierKeys.None) return;
        if (TreeList.SelectedItem is not FileItem f) return;
        int i = treeRows.IndexOf(f);
        if (e.Key == Key.Right && f.IsDir)
        {
            if (!f.IsExpanded) ToggleTreeRow(f);
            else if (i + 1 < treeRows.Count && treeRows[i + 1].Depth > f.Depth) SelectTreeRow(i + 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            if (f.IsDir && f.IsExpanded) ToggleTreeRow(f);
            else
            {
                for (int k = i - 1; k >= 0; k--)
                    if (treeRows[k].Depth < f.Depth) { SelectTreeRow(k); break; }
            }
            e.Handled = true;
        }
    }

    void SelectTreeRow(int i)
    {
        TreeList.SelectedItem = treeRows[i];
        TreeList.ScrollIntoView(treeRows[i]);
        TreeList.UpdateLayout();
        (TreeList.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem)?.Focus();
    }

    void WatchTreeFolder(string path)
    {
        if (IsRemote(path) || treeWatchers.ContainsKey(path) || treeWatchers.Count >= 40) return;
        try
        {
            var w = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite
            };
            FileSystemEventHandler h = (_, _) => Dispatcher.BeginInvoke(() =>
            {
                treeCache.Remove(path);
                ScheduleTreeRebuild();
            });
            w.Created += h; w.Deleted += h; w.Changed += h;
            w.Renamed += (s, e) => h(s, e);
            w.EnableRaisingEvents = true;
            treeWatchers[path] = w;
        }
        catch { }
    }

    void StopTreeWatchers()
    {
        foreach (var w in treeWatchers.Values) { try { w.Dispose(); } catch { } }
        treeWatchers.Clear();
    }

    // =====================================================================
    // Miller Columns
    // =====================================================================
    int columnsVersion;

    ListBox MakeColumnList()
    {
        var lb = new ListBox
        {
            Style = (Style)FindResource("FlatList"),
            ItemContainerStyle = (Style)FindResource("RowItemStyle"),
            ItemTemplate = (DataTemplate)FindResource("ColumnRowTemplate"),
            Padding = new Thickness(0, 4, 0, 4),
            Focusable = false
        };
        VirtualizingPanel.SetIsVirtualizing(lb, true);
        VirtualizingPanel.SetScrollUnit(lb, ScrollUnit.Pixel);
        lb.PreviewMouseLeftButtonUp += AncestorColumn_Click;
        return lb;
    }

    Border ColumnFrame(UIElement child, double width) => new Border
    {
        Width = width,
        BorderThickness = new Thickness(0, 0, 1, 0),
        Child = child
    }.WithResource(Border.BorderBrushProperty, "LineBrush");

    async void BuildAncestorColumns()
    {
        AncestorColumns.Children.Clear();
        int ver = ++columnsVersion;
        var p = current?.Path;
        if (p == null || IsTag(p)) return;
        var chain = new List<string>();
        for (var a = ParentOf(p); a != null; a = ParentOf(a)) chain.Insert(0, a);
        var lists = new List<(ListBox lb, string folder, string next)>();
        for (int i = 0; i < chain.Count; i++)
        {
            var lb = MakeColumnList();
            AncestorColumns.Children.Add(ColumnFrame(lb, 230));
            lists.Add((lb, chain[i], i + 1 < chain.Count ? chain[i + 1] : p));
        }
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ColumnsScroll.ScrollToRightEnd());
        // fill nearest columns first so what's on screen appears quickly
        foreach (var (lb, folder, next) in Enumerable.Reverse(lists))
        {
            var kids = await Task.Run(() => LoadChildrenSorted(folder));
            if (ver != columnsVersion) return;
            lb.ItemsSource = kids;
            var onPath = kids.FirstOrDefault(k => Same(k.FullPath, next));
            if (onPath != null) { lb.SelectedItem = onPath; lb.ScrollIntoView(onPath); }
        }
        ScrollColumnsToEnd();
    }

    /// <summary>Keep the current folder and the preview column in view.</summary>
    void ScrollColumnsToEnd() =>
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => { ColumnsScroll.UpdateLayout(); ColumnsScroll.ScrollToRightEnd(); });

    void AncestorColumn_Click(object sender, MouseButtonEventArgs e)
    {
        if (ItemFromSource((ListBox)sender, e.OriginalSource) is not FileItem f) return;
        e.Handled = true;
        if (f.IsDir) Navigate(f.FullPath);
        else Navigate(ParentOf(f.FullPath), f.FullPath);
    }

    void ColumnsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Right && ColumnsList.SelectedItems.Count == 1 && ColumnsList.SelectedItem is FileItem f && f.IsDir)
        {
            Navigate(f.FullPath);
            e.Handled = true;
        }
        else if (e.Key == Key.Left && ParentOf(current?.Path) != null)
        {
            GoUp();
            e.Handled = true;
        }
    }

    async void UpdateColumnPreview()
    {
        if (S.ViewMode != "columns") return;
        var panel = ColumnPreviewPanel;
        panel.Children.Clear();
        ScrollColumnsToEnd();
        var sel = ColumnsList.SelectedItems.Cast<FileItem>().ToList();
        TextBlock Muted(string text, double size = 12.5) =>
            new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) }.WithResource(TextBlock.ForegroundProperty, "MutedBrush");

        if (sel.Count == 0)
        {
            panel.Children.Add(Muted("Select something to see it here.\n→ opens a folder, ← goes up."));
            return;
        }
        if (sel.Count > 1)
        {
            long size = sel.Where(f => !f.IsDir).Sum(f => Math.Max(0, f.Size));
            panel.Children.Add(new TextBlock { Text = $"{sel.Count:N0} items selected", FontSize = 16, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(Muted($"{sel.Count(f => f.IsDir)} folders, {sel.Count(f => !f.IsDir)} files" + (size > 0 ? $" · {Fmt.Size(size)}" : "")));
            return;
        }
        var item = sel[0];
        var img = new Image { Width = 140, Height = 140, Margin = new Thickness(0, 6, 0, 14), HorizontalAlignment = HorizontalAlignment.Center, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        img.SetBinding(Image.SourceProperty, new Binding(nameof(FileItem.LargeIcon)) { Source = item });
        panel.Children.Add(img);
        panel.Children.Add(new TextBlock { Text = item.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        var meta = item.IsDir ? item.TypeName : $"{item.TypeName} · {item.SizeText}";
        panel.Children.Add(Muted(meta).Centered());
        if (item.ModifiedText.Length > 0) panel.Children.Add(Muted("Modified " + item.ModifiedText).Centered());

        if (!item.IsDir)
        {
            panel.Children.Add(Muted("Space to preview  ·  Enter to open", 12).Centered().WithMargin(0, 14, 0, 0));
            return;
        }
        // folder: peek at its contents
        var header = Muted("Contents", 12).WithMargin(0, 18, 0, 6);
        panel.Children.Add(header);
        var path = item.FullPath;
        var kids = await Task.Run(() => LoadChildrenSorted(path));
        if (!ReferenceEquals(ColumnsList.SelectedItem, item) || ColumnsList.SelectedItems.Count != 1) return;
        header.Text = kids.Count == 0 ? "Empty folder" : $"{kids.Count(k => k.IsDir)} folders, {kids.Count(k => !k.IsDir)} files";
        foreach (var k in kids.Take(14))
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var icon = new Image { Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) };
            icon.SetBinding(Image.SourceProperty, new Binding(nameof(FileItem.SmallIcon)) { Source = k });
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = k.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(row);
        }
        if (kids.Count > 14) panel.Children.Add(Muted($"and {kids.Count - 14:N0} more…", 12));
    }
}

static class UiExtensions
{
    public static T WithResource<T>(this T el, DependencyProperty dp, string key) where T : FrameworkElement
    {
        el.SetResourceReference(dp, key);
        return el;
    }

    public static TextBlock Centered(this TextBlock t)
    {
        t.TextAlignment = TextAlignment.Center;
        return t;
    }

    public static T WithMargin<T>(this T el, double l, double t, double r, double b) where T : FrameworkElement
    {
        el.Margin = new Thickness(l, t, r, b);
        return el;
    }
}
