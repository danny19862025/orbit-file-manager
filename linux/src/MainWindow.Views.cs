using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Orbit;

/// <summary>Widths of the Details view's columns, shared by the header and every row.</summary>
public sealed class DetailsColumns : ObservableObject
{
    public static DetailsColumns Instance { get; } = new();
    double name = 400, date = 170, type = 170, size = 100;
    public double NameWidth { get => name; set { name = Math.Max(120, value); OnChanged(); } }
    public double DateWidth { get => date; set { date = Math.Max(60, value); OnChanged(); } }
    public double TypeWidth { get => type; set { type = Math.Max(60, value); OnChanged(); } }
    public double SizeWidth { get => size; set { size = Math.Max(60, value); OnChanged(); } }
}

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
        GridList.IsVisible = m == "grid";
        DetailsHost.IsVisible = m == "details";
        TreeHost.IsVisible = m == "tree";
        ColumnsHost.IsVisible = m == "columns";
        ViewBtn.Content = ViewGlyph(m);
        ToolTip.SetTip(ViewBtn, $"View: {ViewModes.First(v => v.Id == m).Label} — click to switch");
    }

    /// <summary>Points the visible list at the current folder.</summary>
    void BindActiveView()
    {
        ApplyViewVisibility();
        var active = ActiveList;
        if (S.ViewMode == "tree") RebuildTree();
        else if (!ReferenceEquals(active.ItemsSource, view)) active.ItemsSource = view;

        if (S.ViewMode == "columns")
        {
            BuildAncestorColumns();
            UpdateColumnPreview();
        }
        else AncestorColumns.Children.Clear();
        if (ScrollOf(active) is { } sv) sv.Offset = new Vector(0, 0);
    }

    void SetView(string mode)
    {
        if (S.ViewMode == mode || ViewModes.All(v => v.Id != mode)) return;
        var keep = Selected().Select(f => f.FullPath).ToHashSet(StringComparer.Ordinal);
        S.ViewMode = mode;
        IconService.CancelPending();
        BindActiveView();
        var list = ActiveList;
        list.UnselectAll();
        foreach (var item in (list.ItemsSource as IEnumerable<FileItem>) ?? Enumerable.Empty<FileItem>())
            if (keep.Contains(item.FullPath)) list.SelectedItems.Add(item);
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

    /// <summary>Children of any folder (local, SFTP or archive), filtered and sorted like the main view.</summary>
    List<FileItem> LoadChildrenSorted(string path)
    {
        List<FileItem> list;
        try { list = Archives.IsArc(path) ? Archives.List(path) : IsRemote(path) ? Sftp.List(path) : Enumerate(path); }
        catch { return new List<FileItem>(); }
        bool hidden = S.ShowHidden;
        list = list.Where(f => hidden || !f.IsHidden).ToList();
        list.Sort(new ItemComparer(S.SortBy, S.SortDesc));
        return list;
    }

    // =====================================================================
    // Details header (click to sort, drag the edges to resize)
    // =====================================================================
    void BuildDetailsHeader()
    {
        if (DetailsHeader == null) return;
        var cols = DetailsColumns.Instance;
        if (S.Columns is { Length: 4 } c && DetailsHeader.Children.Count == 0)
        {
            cols.NameWidth = c[0]; cols.DateWidth = c[1]; cols.TypeWidth = c[2]; cols.SizeWidth = c[3];
        }
        DetailsHeader.Children.Clear();
        bool bin = RecycleBin.Is(current?.Path);
        foreach (var (key, text, prop) in new[] { ("name", "Name", nameof(DetailsColumns.NameWidth)), ("date", bin ? "Date deleted" : "Date modified", nameof(DetailsColumns.DateWidth)),
                                                   ("type", bin ? "Original location" : "Type", nameof(DetailsColumns.TypeWidth)), ("size", "Size", nameof(DetailsColumns.SizeWidth)) })
        {
            var k = key;
            var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = text, FontSize = 12.5 });
            if (S.SortBy == key)
            {
                var arrow = new TextBlock { Text = S.SortDesc ? "" : "", Classes = { "icon" }, FontSize = 9, Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                arrow.Bind(TextBlock.ForegroundProperty, arrow.GetResourceObservable("AccentBrush"));
                label.Children.Add(arrow);
            }
            var cell = new Border
            {
                Padding = new Thickness(10, 0), BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent, Child = label,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            cell.Bind(Border.BorderBrushProperty, cell.GetResourceObservable("LineBrush"));
            cell.Bind(TextBlock.ForegroundProperty, cell.GetResourceObservable("MutedBrush"));
            cell.Bind(WidthProperty, new Avalonia.Data.Binding(prop) { Source = cols });
            cell.PointerEntered += (_, _) => { cell.Bind(Border.BackgroundProperty, cell.GetResourceObservable("HoverBg")); cell.Bind(TextBlock.ForegroundProperty, cell.GetResourceObservable("TextBrush")); };
            cell.PointerExited += (_, _) => { cell.Background = Brushes.Transparent; cell.Bind(TextBlock.ForegroundProperty, cell.GetResourceObservable("MutedBrush")); };
            cell.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left || resizingColumn) return;
                if (S.SortBy == k) S.SortDesc = !S.SortDesc;
                else { S.SortBy = k; S.SortDesc = k is "date" or "size"; }
                ApplySort();
            };
            // resize grip on the right edge
            var grip = new Border { Width = 8, Margin = new Thickness(0, 0, -4, 0), HorizontalAlignment = HorizontalAlignment.Right, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeWestEast) };
            var line = new Border { Width = 1, Margin = new Thickness(0, 9) };
            line.Bind(Border.BackgroundProperty, line.GetResourceObservable("LineBrush"));
            grip.Child = line;
            double startX = 0, startW = 0;
            grip.PointerPressed += (_, e) => { resizingColumn = true; startX = e.GetPosition(this).X; startW = cell.Bounds.Width; e.Pointer.Capture(grip); e.Handled = true; };
            grip.PointerMoved += (_, e) =>
            {
                if (!resizingColumn || !ReferenceEquals(e.Pointer.Captured, grip)) return;
                var w = startW + e.GetPosition(this).X - startX;
                typeof(DetailsColumns).GetProperty(prop)!.SetValue(cols, w);
            };
            grip.PointerReleased += (_, e) => { e.Pointer.Capture(null); Dispatcher.UIThread.Post(() => resizingColumn = false); SaveSoon(); e.Handled = true; };
            var host = new Panel();
            host.Children.Add(cell);
            if (key != "size") host.Children.Add(grip);
            if (key == "name") cell.Margin = new Thickness(0);
            DetailsHeader.Children.Add(host);
        }
    }

    bool resizingColumn;

    // =====================================================================
    // Tree View
    // =====================================================================
    ObservableCollection<FileItem> treeRows = new();
    readonly HashSet<string> treeExpanded = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<FileItem>> treeCache = new(StringComparer.Ordinal);
    readonly Dictionary<string, FileSystemWatcher> treeWatchers = new(StringComparer.Ordinal);
    string treeRoot;
    DispatcherTimer treeDebounce;

    void ScheduleTreeRebuild()
    {
        treeDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => { treeDebounce.Stop(); RebuildTree(); });
        treeDebounce.Stop();
        treeDebounce.Start();
    }

    void RebuildTree()
    {
        if (S.ViewMode != "tree") return;
        if (!Same(treeRoot, current?.Path))
        {
            treeRoot = current?.Path;
            treeExpanded.Clear();
            treeCache.Clear();
            StopTreeWatchers();
        }
        var keep = new HashSet<string>(TreeList.SelectedItems.Cast<FileItem>().Select(f => f.FullPath), StringComparer.Ordinal);
        var rows = new List<FileItem>();
        foreach (var f in view) AddTreeRow(rows, f, 0);
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
        if (((Control)sender).Tag is FileItem f) ToggleTreeRow(f);
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
        if (e.Source is TextBox || e.KeyModifiers != KeyModifiers.None) return;
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
        FocusIndex(TreeList, i);
    }

    void WatchTreeFolder(string path)
    {
        if (IsRemote(path) || Archives.IsArc(path) || treeWatchers.ContainsKey(path) || treeWatchers.Count >= 40) return;
        try
        {
            var w = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite
            };
            FileSystemEventHandler h = (_, _) => Dispatcher.UIThread.Post(() =>
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
            Classes = { "flat", "rows" },
            ItemTemplate = (IDataTemplate)this.FindResource("ColumnRowTemplate"),
            Padding = new Thickness(0, 4, 0, 4),
            Focusable = false,
            SelectionMode = SelectionMode.Single
        };
        lb.AddHandler(PointerReleasedEvent, AncestorColumn_Click, RoutingStrategies.Tunnel, true);
        return lb;
    }

    Border ColumnFrame(Control child, double width)
    {
        var b = new Border { Width = width, BorderThickness = new Thickness(0, 0, 1, 0), Child = child };
        b.Bind(Border.BorderBrushProperty, b.GetResourceObservable("LineBrush"));
        return b;
    }

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
        ScrollColumnsToEnd();
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
        Dispatcher.UIThread.Post(() => ColumnsScroll.Offset = new Vector(double.MaxValue, 0), DispatcherPriority.Background);

    void AncestorColumn_Click(object sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (ItemFromSource((ListBox)sender, e.Source) is not FileItem f) return;
        e.Handled = true;
        if (f.IsDir) Navigate(f.FullPath);
        else Navigate(ParentOf(f.FullPath), f.FullPath);
    }

    void ColumnsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Source is TextBox || e.KeyModifiers != KeyModifiers.None) return;
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
        TextBlock Muted(string text, double size = 12.5)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("MutedBrush"));
            return t;
        }

        if (sel.Count == 0)
        {
            panel.Children.Add(Muted("Select something to see it here.\n→ opens a folder, ← goes up."));
            return;
        }
        if (sel.Count > 1)
        {
            long size = sel.Where(f => !f.IsDir).Sum(f => Math.Max(0, f.Size));
            panel.Children.Add(new TextBlock { Text = $"{sel.Count:N0} items selected", FontSize = 16, FontWeight = FontWeight.SemiBold });
            panel.Children.Add(Muted($"{sel.Count(f => f.IsDir)} folders, {sel.Count(f => !f.IsDir)} files" + (size > 0 ? $" · {Fmt.Size(size)}" : "")));
            return;
        }
        var item = sel[0];
        var img = new Image { Width = 140, Height = 140, Margin = new Thickness(0, 6, 0, 14), HorizontalAlignment = HorizontalAlignment.Center, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapInterpolationMode(img, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        img.Bind(Image.SourceProperty, new Avalonia.Data.Binding(nameof(FileItem.LargeIcon)) { Source = item });
        panel.Children.Add(img);
        panel.Children.Add(new TextBlock { Text = item.Name, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        var meta = item.IsDir ? item.TypeName : $"{item.TypeName} · {item.SizeText}";
        panel.Children.Add(Centered(Muted(meta)));
        if (item.ModifiedText.Length > 0) panel.Children.Add(Centered(Muted("Modified " + item.ModifiedText)));

        if (!item.IsDir)
        {
            var hint = Centered(Muted("Space to preview  ·  Enter to open", 12));
            hint.Margin = new Thickness(0, 14, 0, 0);
            panel.Children.Add(hint);
            return;
        }
        // folder: peek at its contents
        var header = Muted("Contents", 12);
        header.Margin = new Thickness(0, 18, 0, 6);
        panel.Children.Add(header);
        var path = item.FullPath;
        var kids = await Task.Run(() => LoadChildrenSorted(path));
        if (!ReferenceEquals(ColumnsList.SelectedItem, item) || ColumnsList.SelectedItems.Count != 1) return;
        header.Text = kids.Count == 0 ? "Empty folder" : $"{kids.Count(k => k.IsDir)} folders, {kids.Count(k => !k.IsDir)} files";
        foreach (var k in kids.Take(14))
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var icon = new Image { Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) };
            icon.Bind(Image.SourceProperty, new Avalonia.Data.Binding(nameof(FileItem.SmallIcon)) { Source = k });
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = k.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(row);
        }
        if (kids.Count > 14) panel.Children.Add(Muted($"and {kids.Count - 14:N0} more…", 12));
    }

    static TextBlock Centered(TextBlock t) { t.TextAlignment = TextAlignment.Center; return t; }
}
