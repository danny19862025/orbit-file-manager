using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Orbit;

/// <summary>"Where your space goes": a read-only bubble map of what fills a folder or drive. It never deletes anything —
/// "Show in Orbit" takes you to the item so you can decide there.</summary>
public sealed class SpaceWindow : OrbitWindow
{
    sealed class Node
    {
        public string Name, Path;
        public bool IsDir, IsOther;
        public long Size;
        public int Files;
        public bool Selected;
        public FileItem Item;
    }

    readonly MainWindow main;
    string root;
    CancellationTokenSource cts;
    List<Node> nodes = new();
    int totalFiles;
    bool scanning;
    long driveTotal, driveUsed;

    const int MaxBubbles = 24;

    readonly Canvas bubbles = new() { ClipToBounds = true, Background = Brushes.Transparent };
    readonly TextBlock pathText, driveName, driveFree, scanText, statusText, selTitle, selSub;
    readonly Border driveUsedBar, driveSelectedBar;
    readonly Grid driveBar;
    readonly Button upBtn, showBtn;

    public SpaceWindow(MainWindow owner, string path)
    {
        // Not an owned window: an owned window always sits on top of Orbit, which hid "Show in Orbit"'s result.
        // It gets its own taskbar button instead, opens centred over Orbit, and closes with it.
        main = owner;
        ShowInTaskbar = true;
        Title = "Disk space";
        Width = 1100;
        Height = 760;
        MinWidth = 720;
        MinHeight = 520;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (owner.WindowState != WindowState.Minimized)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(owner.Position.X + (int)((owner.Bounds.Width - Width) / 2 * owner.RenderScaling), owner.Position.Y + (int)((owner.Bounds.Height - Height) / 2 * owner.RenderScaling));
        }
        EventHandler closeWithOrbit = (_, _) => Close();
        owner.Closed += closeWithOrbit;
        Closed += (_, _) => { owner.Closed -= closeWithOrbit; cts?.Cancel(); };
        root = path;

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        // title bar
        var title = new DockPanel { Height = 44, Margin = new Thickness(18, 0, 6, 0) };
        var close = Ui.Icon("", 11);
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        title.Children.Add(close);
        var max = Ui.Icon("", 10);
        max.Margin = new Thickness(0, 0, 2, 0);
        max.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        DockPanel.SetDock(max, Dock.Right);
        title.Children.Add(max);
        var tsp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        tsp.Children.Add(Ui.Glyph("", 15, "AccentBrush", new Thickness(0, 0, 10, 0)));
        tsp.Children.Add(new TextBlock { Text = "Disk space", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(tsp);
        DragArea(title);
        grid.Children.Add(title);

        // header
        var header = new Grid { Margin = new Thickness(32, 6, 32, 10), ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetRow(header, 1);
        var hl = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        hl.Children.Add(new TextBlock { Text = "Where your space goes", FontSize = 26, FontWeight = FontWeight.SemiBold });
        hl.Children.Add(new TextBlock { Text = "See what fills a drive or folder. Click a bubble to see what it is; double-click a folder to look inside.", FontSize = 14, Margin = new Thickness(0, 4, 0, 12), TextWrapping = TextWrapping.Wrap }.Brush(TextBlock.ForegroundProperty, "MutedBrush"));
        var pathRow = new StackPanel { Orientation = Orientation.Horizontal };
        upBtn = Ui.Icon("", 15, "Up one level");
        upBtn.Click += (_, _) => Up();
        pathRow.Children.Add(upBtn);
        pathText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 560 };
        pathRow.Children.Add(pathText);
        hl.Children.Add(pathRow);
        header.Children.Add(hl);
        var driveCard = new StackPanel();
        var driveTop = new DockPanel();
        driveFree = new TextBlock { VerticalAlignment = VerticalAlignment.Center }.Brush(TextBlock.ForegroundProperty, "MutedBrush");
        DockPanel.SetDock(driveFree, Dock.Right);
        driveTop.Children.Add(driveFree);
        driveTop.Children.Add(Ui.Glyph("", 15, null, new Thickness(0, 0, 10, 0)));
        driveName = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        driveTop.Children.Add(driveName);
        driveCard.Children.Add(driveTop);
        driveBar = new Grid { Height = 8, Margin = new Thickness(0, 12, 0, 0) };
        driveBar.Children.Add(new Border { CornerRadius = new CornerRadius(4) }.Brush(Border.BackgroundProperty, "TrackBrush"));
        driveUsedBar = new Border { CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0.7 }.Brush(Border.BackgroundProperty, "MutedBrush");
        driveSelectedBar = new Border { CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left }.Brush(Border.BackgroundProperty, "AccentBrush");
        driveBar.Children.Add(driveUsedBar);
        driveBar.Children.Add(driveSelectedBar);
        driveCard.Children.Add(driveBar);
        var dc = new Border { Width = 420, Padding = new Thickness(18, 14), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top, Child = driveCard }
            .Brush(Border.BackgroundProperty, "PanelBg").Brush(Border.BorderBrushProperty, "LineBrush");
        Grid.SetColumn(dc, 1);
        header.Children.Add(dc);
        grid.Children.Add(header);

        // bubbles
        var mid = new Grid { Margin = new Thickness(20, 0) };
        Grid.SetRow(mid, 2);
        mid.Children.Add(bubbles);
        scanText = new TextBlock { FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false }.Brush(TextBlock.ForegroundProperty, "MutedBrush");
        mid.Children.Add(scanText);
        grid.Children.Add(mid);
        bubbles.SizeChanged += (_, _) => Redraw();

        // footer
        var footer = new DockPanel { Margin = new Thickness(32, 10, 32, 24) };
        Grid.SetRow(footer, 3);
        statusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 }.Brush(TextBlock.ForegroundProperty, "MutedBrush");
        DockPanel.SetDock(statusText, Dock.Left);
        footer.Children.Add(statusText);
        var selPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var selText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 26, 0) };
        selTitle = new TextBlock { Text = "Nothing selected", FontWeight = FontWeight.SemiBold, FontSize = 15, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis };
        selSub = new TextBlock { FontSize = 12.5 }.Brush(TextBlock.ForegroundProperty, "MutedBrush");
        selText.Children.Add(selTitle);
        selText.Children.Add(selSub);
        selPanel.Children.Add(selText);
        var showContent = new StackPanel { Orientation = Orientation.Horizontal };
        showContent.Children.Add(Ui.Glyph("", 14, null, new Thickness(0, 0, 10, 0)));
        showContent.Children.Add(new TextBlock { Text = "Show in Orbit" });
        showBtn = Ui.Dlg(null, true);
        showBtn.Content = showContent;
        showBtn.Padding = new Thickness(22, 11);
        showBtn.FontSize = 14;
        showBtn.IsEnabled = false;
        showBtn.Click += (_, _) => ShowSelected();
        selPanel.Children.Add(showBtn);
        var selCard = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(22, 10, 10, 10), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Right, Child = selPanel }
            .Brush(Border.BackgroundProperty, "PanelBg").Brush(Border.BorderBrushProperty, "LineBrush");
        DockPanel.SetDock(selCard, Dock.Right);
        footer.Children.Add(selCard);
        grid.Children.Add(footer);
        SetBody(WithResizeGrips(grid));

        Opened += (_, _) => Scan();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Back) Up();
        };
    }

    /// <summary>Edges to resize the window by (Orbit draws its own frame).</summary>
    Control WithResizeGrips(Control body)
    {
        var p = new Panel();
        p.Children.Add(body);
        void Grip(WindowEdge edge, double? w, double? h, HorizontalAlignment ha, VerticalAlignment va, StandardCursorType cursor)
        {
            var b = new Border { Background = Brushes.Transparent, HorizontalAlignment = ha, VerticalAlignment = va, Cursor = new Cursor(cursor) };
            if (w != null) b.Width = w.Value;
            if (h != null) b.Height = h.Value;
            b.PointerPressed += (_, e) => { if (WindowState == WindowState.Normal) { BeginResizeDrag(edge, e); e.Handled = true; } };
            p.Children.Add(b);
        }
        Grip(WindowEdge.East, 5, null, HorizontalAlignment.Right, VerticalAlignment.Stretch, StandardCursorType.RightSide);
        Grip(WindowEdge.West, 5, null, HorizontalAlignment.Left, VerticalAlignment.Stretch, StandardCursorType.LeftSide);
        Grip(WindowEdge.South, null, 5, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, StandardCursorType.BottomSide);
        Grip(WindowEdge.SouthEast, 10, 10, HorizontalAlignment.Right, VerticalAlignment.Bottom, StandardCursorType.BottomRightCorner);
        Grip(WindowEdge.SouthWest, 10, 10, HorizontalAlignment.Left, VerticalAlignment.Bottom, StandardCursorType.BottomLeftCorner);
        return p;
    }

    void Up()
    {
        var parent = Directory.GetParent(root.Length > 1 ? root.TrimEnd('/') : root)?.FullName;
        if (parent == null) return;
        root = parent;
        Scan();
    }

    // =====================================================================
    // Scanning
    // =====================================================================
    async void Scan()
    {
        cts?.Cancel();
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        nodes = new List<Node>();
        totalFiles = 0;
        scanning = true;
        pathText.Text = root;
        upBtn.IsEnabled = root != "/";
        UpdateDrive();
        UpdateSelection();
        Redraw();
        scanText.IsVisible = true;
        scanText.Text = "Scanning…";

        var progress = new Progress<(List<Node> snapshot, long bytes, int files)>(p =>
        {
            if (ct.IsCancellationRequested) return;
            nodes = p.snapshot;
            totalFiles = p.files;
            scanText.Text = "";
            statusText.Text = $"Scanning…  {p.files:N0} files · {Fmt.Size(p.bytes)}";
            Redraw();
        });
        IProgress<(List<Node>, long, int)> report = progress;
        var dir = root;
        List<Node> result;
        try
        {
            result = await Task.Run(() => ScanFolder(dir, report, ct), ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            scanning = false;
            scanText.Text = "Couldn't scan this folder: " + ex.Message;
            return;
        }
        if (ct.IsCancellationRequested) return;
        scanning = false;
        nodes = result;
        totalFiles = result.Sum(n => n.Files);
        scanText.IsVisible = nodes.Count == 0;
        scanText.Text = nodes.Count == 0 ? "This folder is empty" : "";
        long total = nodes.Sum(n => n.Size);
        statusText.Text = $"{totalFiles:N0} files · {Fmt.Size(total)} in this folder. Click to select, double-click a folder to look inside.";
        Redraw();
        UpdateSelection();
    }

    static List<Node> ScanFolder(string dir, IProgress<(List<Node>, long, int)> report, CancellationToken ct)
    {
        var list = new List<Node>();
        // stay on this drive and skip symlinks and the kernel's virtual folders
        var top = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false };
        var deep = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = true, ReturnSpecialDirectories = false };
        var virtualRoots = new HashSet<string> { "/proc", "/sys", "/dev", "/run" };
        long bytes = 0;
        int files = 0;
        var last = DateTime.UtcNow;
        var di = new DirectoryInfo(dir);

        foreach (var f in di.EnumerateFiles("*", top))
        {
            long len = 0;
            try { len = f.Length; } catch { }
            list.Add(new Node { Name = f.Name, Path = f.FullName, Size = len, Files = 1 });
            bytes += len; files++;
        }
        foreach (var d in di.EnumerateDirectories("*", top))
        {
            ct.ThrowIfCancellationRequested();
            if (virtualRoots.Contains(d.FullName)) continue;
            var node = new Node { Name = d.Name, Path = d.FullName, IsDir = true };
            list.Add(node);
            try
            {
                foreach (var f in d.EnumerateFiles("*", deep))
                {
                    long len = 0;
                    try { len = f.Length; } catch { }
                    node.Size += len; node.Files++;
                    bytes += len; files++;
                    if ((files & 1023) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        if ((DateTime.UtcNow - last).TotalMilliseconds > 600)
                        {
                            last = DateTime.UtcNow;
                            report.Report((Snapshot(list), bytes, files));
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            report.Report((Snapshot(list), bytes, files));
        }
        return list;
    }

    static List<Node> Snapshot(List<Node> list) =>
        list.Select(n => new Node { Name = n.Name, Path = n.Path, IsDir = n.IsDir, Size = n.Size, Files = n.Files }).ToList();

    void UpdateDrive()
    {
        try
        {
            var mount = RecycleBin.MountOf(root);
            var d = new DriveInfo(mount);
            var drive = Drives.List().FirstOrDefault(x => x.Mount == mount);
            driveName.Text = drive != null ? $"{drive.Name} ({mount})" : mount;
            long used = d.TotalSize - d.AvailableFreeSpace;
            driveFree.Text = $"{Fmt.Size(used)} of {Fmt.Size(d.TotalSize)}";
            driveTotal = d.TotalSize;
            driveUsed = used;
        }
        catch { driveName.Text = root; driveFree.Text = ""; driveTotal = 0; }
    }

    // =====================================================================
    // Bubble layout
    // =====================================================================
    List<Node> Visible()
    {
        var sorted = nodes.Where(n => n.Size > 0).OrderByDescending(n => n.Size).ToList();
        if (sorted.Count <= MaxBubbles) return sorted;
        var shown = sorted.Take(MaxBubbles - 1).ToList();
        var rest = sorted.Skip(MaxBubbles - 1).ToList();
        shown.Add(new Node { Name = "Other items", IsOther = true, Size = rest.Sum(r => r.Size), Files = rest.Sum(r => r.Files) });
        return shown;
    }

    void Redraw()
    {
        bubbles.Children.Clear();
        double W = bubbles.Bounds.Width, H = bubbles.Bounds.Height;
        var vis = Visible();
        if (vis.Count == 0 || W < 50 || H < 50) return;

        // place circles on a widened spiral, then scale to fit
        double aspect = Math.Clamp(W / H, 1, 3);
        // a gentler curve than √size keeps small items readable next to huge ones
        var radii = vis.Select(n => Math.Pow(n.Size, 0.4)).ToList();
        double pad = radii.Max() * 0.06;
        var pos = new List<(double x, double y, double r)>();
        foreach (var r in radii)
        {
            if (pos.Count == 0) { pos.Add((0, 0, r)); continue; }
            bool placed = false;
            for (double d = r * 0.5; !placed && d < 1e9; d += Math.Max(r * 0.15, radii[0] * 0.02))
            {
                for (double a = 0; a < Math.PI * 2; a += 0.22)
                {
                    double x = Math.Cos(a) * d * aspect, y = Math.Sin(a) * d;
                    if (pos.All(p => (p.x - x) * (p.x - x) + (p.y - y) * (p.y - y) >= (p.r + r + pad) * (p.r + r + pad)))
                    {
                        pos.Add((x, y, r));
                        placed = true;
                        break;
                    }
                }
            }
        }
        double minX = pos.Min(p => p.x - p.r), maxX = pos.Max(p => p.x + p.r);
        double minY = pos.Min(p => p.y - p.r), maxY = pos.Max(p => p.y + p.r);
        const double margin = 16;
        double s = Math.Min((W - margin * 2) / (maxX - minX), (H - margin * 2) / (maxY - minY));
        double ox = (W - (maxX - minX) * s) / 2 - minX * s, oy = (H - (maxY - minY) * s) / 2 - minY * s;

        var line = Themes.Brush("LineBrush");
        var accent = Themes.ColorOf("AccentBrush");
        var panel = Themes.ColorOf("PanelBg");
        var sidebar = Themes.ColorOf("SidebarBg");
        sidebar = Color.FromRgb(sidebar.R, sidebar.G, sidebar.B);
        var muted = Themes.Brush("MutedBrush");

        for (int i = 0; i < vis.Count; i++)
        {
            var n = vis[i];
            var (x, y, r) = pos[i];
            double R = Math.Max(10, r * s), cx = ox + x * s, cy = oy + y * s;
            var g = new Grid { Width = R * 2, Height = R * 2, Cursor = new Cursor(n.IsOther ? StandardCursorType.Arrow : StandardCursorType.Hand), Tag = n };
            var fill = new RadialGradientBrush
            {
                GradientOrigin = new RelativePoint(0.5, 0.15, RelativeUnit.Relative), Center = new RelativePoint(0.5, 0.35, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.8, RelativeUnit.Relative), RadiusY = new RelativeScalar(0.8, RelativeUnit.Relative)
            };
            if (n.Selected)
            {
                fill.GradientStops.Add(new GradientStop(Themes.Mix(panel, accent, 0.15), 0));
                fill.GradientStops.Add(new GradientStop(Themes.Mix(panel, accent, 0.55), 0.75));
                fill.GradientStops.Add(new GradientStop(accent, 1));
            }
            else
            {
                fill.GradientStops.Add(new GradientStop(panel, 0));
                fill.GradientStops.Add(new GradientStop(sidebar, 1));
            }
            g.Children.Add(new Ellipse { Fill = fill, Stroke = n.Selected ? new SolidColorBrush(accent) : line, StrokeThickness = n.Selected ? 2 : 1 });

            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = R * 1.55 };
            double iconSize = Math.Min(56, R * 0.45);
            if (n.IsOther)
                content.Children.Add(new TextBlock { Text = "•••", FontSize = Math.Min(26, R * 0.3), Foreground = muted, HorizontalAlignment = HorizontalAlignment.Center });
            else
            {
                n.Item ??= FileItem.TryFromPath(n.Path);
                var img = new Image { Width = iconSize, Height = iconSize, HorizontalAlignment = HorizontalAlignment.Center };
                if (n.Item != null) img.Bind(Image.SourceProperty, new Avalonia.Data.Binding(nameof(FileItem.LargeIcon)) { Source = n.Item });
                content.Children.Add(img);
            }
            if (R > 44)
                content.Children.Add(new TextBlock
                {
                    Text = n.Name, FontWeight = FontWeight.SemiBold, FontSize = Math.Clamp(R / 6.5, 11, 17), TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0)
                });
            if (R > 30)
                content.Children.Add(new TextBlock
                {
                    Text = Fmt.Size(n.Size), FontSize = Math.Clamp(R / 8, 10.5, 15), Foreground = n.Selected ? Brushes.White : muted,
                    HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0)
                });
            g.Children.Add(content);

            if (n.Selected)
            {
                var badge = new Border
                {
                    Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(R + R * 0.7071 - 13, R - R * 0.7071 - 13, 0, 0),
                    Child = new TextBlock { Text = "", Classes = { "icon" }, FontSize = 13, Foreground = new SolidColorBrush(accent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                };
                g.Children.Add(badge);
            }

            ToolTip.SetTip(g, n.IsOther
                ? $"{n.Files:N0} smaller items · {Fmt.Size(n.Size)}"
                : $"{n.Name}\n{Fmt.Size(n.Size)}" + (n.IsDir ? $" · {n.Files:N0} files\nDouble-click to look inside" : ""));
            g.PointerPressed += Bubble_PointerPressed;
            Canvas.SetLeft(g, cx - R);
            Canvas.SetTop(g, cy - R);
            bubbles.Children.Add(g);
        }
    }

    // =====================================================================
    // Interaction
    // =====================================================================
    void Bubble_PointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (((Control)sender).Tag is not Node n || n.IsOther) return;
        e.Handled = true;
        var pt = e.GetCurrentPoint(this);
        if (pt.Properties.IsRightButtonPressed)
        {
            var menu = new ContextMenu();
            void Add(string text, string glyph, Action a)
            {
                var mi = new OrbitMenuItem(text, null) { Icon = Ui.Glyph(glyph, 14) };
                mi.Click += (_, _) => a();
                menu.Items.Add(mi);
            }
            if (n.IsDir) Add("Look inside", "", () => { root = n.Path; Scan(); });
            Add("Show in Orbit", "", () => { main.Activate(); main.ShowInOrbit(n.Path); });
            menu.Open((Control)sender);
            return;
        }
        if (!pt.Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            if (n.IsDir && !scanning) { root = n.Path; Scan(); }
            return;
        }
        // one highlighted bubble at a time
        bool was = n.Selected;
        foreach (var o in nodes) o.Selected = false;
        n.Selected = !was;
        Redraw();
        UpdateSelection();
    }

    void UpdateSelection()
    {
        var sel = nodes.Where(n => n.Selected).ToList();
        long size = sel.Sum(n => n.Size);
        long all = nodes.Sum(n => n.Size);
        if (sel.Count == 1)
        {
            selTitle.Text = sel[0].Name;
            selSub.Text = Fmt.Size(size) + (all > 0 ? $" · {size * 100.0 / all:0.#}% of this folder" : "");
        }
        else
        {
            selTitle.Text = "Click a bubble";
            selSub.Text = $"{nodes.Count:N0} items" + (totalFiles > 0 ? $" · {totalFiles:N0} files" : "");
        }
        showBtn.IsEnabled = sel.Count == 1 && !sel[0].IsOther;

        double w = driveBar.Bounds.Width > 0 ? driveBar.Bounds.Width : 384;
        if (driveTotal > 0)
        {
            // the highlighted item's share of the drive, shown inside the used part of the bar
            driveUsedBar.Width = Math.Max(0, driveUsed * w / driveTotal);
            driveSelectedBar.Width = Math.Max(size > 0 ? 6 : 0, size * w / driveTotal);
        }
    }

    void ShowSelected()
    {
        var n = nodes.FirstOrDefault(x => x.Selected);
        if (n == null || n.IsOther) return;
        main.Activate();
        main.ShowInOrbit(n.Path);
    }
}
