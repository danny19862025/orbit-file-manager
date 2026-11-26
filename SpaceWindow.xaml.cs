using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using IOPath = System.IO.Path;

namespace Orbit;

/// <summary>"Space, reclaimed": bubble map of what fills a folder or drive, with select-and-clean.</summary>
public partial class SpaceWindow : Window
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

    const int MaxBubbles = 24;

    public SpaceWindow(MainWindow owner, string path)
    {
        InitializeComponent();
        Owner = owner;
        main = owner;
        root = path;
        Loaded += (_, _) => Scan();
        Closed += (_, _) => cts?.Cancel();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Back) Up_Click(null, null);
        };
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
    void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void Up_Click(object sender, RoutedEventArgs e)
    {
        var parent = Directory.GetParent(root.TrimEnd('\\'))?.FullName;
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
        PathText.Text = root;
        UpBtn.IsEnabled = Directory.GetParent(root.TrimEnd('\\')) != null;
        UpdateDrive();
        UpdateSelection();
        Redraw();
        ScanPanel.Visibility = Visibility.Visible;
        ScanText.Text = "Scanning…";

        var progress = new Progress<(List<Node> snapshot, long bytes, int files)>(p =>
        {
            if (ct.IsCancellationRequested) return;
            nodes = p.snapshot;
            totalFiles = p.files;
            ScanText.Text = "";
            StatusText.Text = $"Scanning…  {p.files:N0} files · {Fmt.Size(p.bytes)}";
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
            ScanText.Text = "Couldn't scan this folder: " + ex.Message;
            return;
        }
        if (ct.IsCancellationRequested) return;
        scanning = false;
        nodes = result;
        totalFiles = result.Sum(n => n.Files);
        ScanPanel.Visibility = nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScanText.Text = nodes.Count == 0 ? "This folder is empty" : "";
        long total = nodes.Sum(n => n.Size);
        StatusText.Text = $"{totalFiles:N0} files · {Fmt.Size(total)} in this folder. Click to select, double-click a folder to look inside.";
        Redraw();
        UpdateSelection();
    }

    static List<Node> ScanFolder(string dir, IProgress<(List<Node>, long, int)> report, CancellationToken ct)
    {
        var list = new List<Node>();
        var top = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false };
        var deep = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = true, ReturnSpecialDirectories = false };
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
            var d = new DriveInfo(IOPath.GetPathRoot(root)!);
            DriveName.Text = string.IsNullOrEmpty(d.VolumeLabel) ? $"Local Disk ({d.Name.TrimEnd('\\')})" : $"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})";
            long used = d.TotalSize - d.AvailableFreeSpace;
            DriveFree.Text = $"{Fmt.Size(used)} of {Fmt.Size(d.TotalSize)}";
            driveTotal = d.TotalSize;
            driveUsed = used;
        }
        catch { DriveName.Text = root; DriveFree.Text = ""; driveTotal = 0; }
    }

    long driveTotal, driveUsed;

    // =====================================================================
    // Bubble layout
    // =====================================================================
    void Bubbles_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

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
        Bubbles.Children.Clear();
        double W = Bubbles.ActualWidth, H = Bubbles.ActualHeight;
        var vis = Visible();
        if (vis.Count == 0 || W < 50 || H < 50) return;

        // place circles (radius ∝ √size) on a widened spiral, then scale to fit
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

        var line = (Brush)FindResource("LineBrush");
        var accent = ((SolidColorBrush)FindResource("AccentBrush")).Color;
        var panel = ((SolidColorBrush)FindResource("PanelBg")).Color;
        var sidebar = ((SolidColorBrush)FindResource("SidebarBg")).Color;
        var muted = (Brush)FindResource("MutedBrush");

        for (int i = 0; i < vis.Count; i++)
        {
            var n = vis[i];
            var (x, y, r) = pos[i];
            double R = Math.Max(10, r * s), cx = ox + x * s, cy = oy + y * s;
            var g = new Grid { Width = R * 2, Height = R * 2, Cursor = n.IsOther ? Cursors.Arrow : Cursors.Hand, Tag = n };
            var fill = new RadialGradientBrush { GradientOrigin = new Point(0.5, 0.15), Center = new Point(0.5, 0.35), RadiusX = 0.8, RadiusY = 0.8 };
            if (n.Selected)
            {
                fill.GradientStops.Add(new GradientStop(Mix(panel, accent, 0.15), 0));
                fill.GradientStops.Add(new GradientStop(Mix(panel, accent, 0.55), 0.75));
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
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                if (n.Item != null) img.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(FileItem.LargeIcon)) { Source = n.Item });
                content.Children.Add(img);
            }
            if (R > 44)
                content.Children.Add(new TextBlock
                {
                    Text = n.Name, FontWeight = FontWeights.SemiBold, FontSize = Math.Clamp(R / 6.5, 11, 17), TextTrimming = TextTrimming.CharacterEllipsis,
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
                    Child = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, Foreground = new SolidColorBrush(accent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                };
                g.Children.Add(badge);
            }

            g.ToolTip = n.IsOther
                ? $"{n.Files:N0} smaller items · {Fmt.Size(n.Size)}"
                : $"{n.Name}\n{Fmt.Size(n.Size)}" + (n.IsDir ? $" · {n.Files:N0} files\nDouble-click to look inside" : "");
            g.MouseLeftButtonDown += Bubble_MouseDown;
            g.MouseRightButtonUp += Bubble_RightClick;
            Canvas.SetLeft(g, cx - R);
            Canvas.SetTop(g, cy - R);
            Bubbles.Children.Add(g);
        }
    }

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    // =====================================================================
    // Interaction
    // =====================================================================
    void Bubble_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not Node n || n.IsOther) return;
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            if (n.IsDir && !scanning) { root = n.Path; Scan(); }
            return;
        }
        n.Selected = !n.Selected;
        Redraw();
        UpdateSelection();
    }

    void Bubble_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not Node n || n.IsOther) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        void Add(string text, Action a)
        {
            var mi = new MenuItem { Header = new TextBlock { Text = text } };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        if (n.IsDir) Add("Look inside", () => { root = n.Path; Scan(); });
        Add("Show in Orbit", () => { main.Activate(); main.ShowInOrbit(n.Path); });
        Add(n.Selected ? "Deselect" : "Select for cleaning", () => { n.Selected = !n.Selected; Redraw(); UpdateSelection(); });
        menu.IsOpen = true;
    }

    void UpdateSelection()
    {
        var sel = nodes.Where(n => n.Selected).ToList();
        long size = sel.Sum(n => n.Size);
        SelTitle.Text = sel.Count == 0 ? "Nothing selected" : $"{sel.Count} selected";
        SelSub.Text = $"of {nodes.Count:N0} items" + (totalFiles > 0 ? $" · {totalFiles:N0} files" : "");
        CleanText.Text = sel.Count == 0 ? "Clean" : $"Clean {Fmt.Size(size)}";
        CleanBtn.IsEnabled = sel.Count > 0 && !scanning;

        var bar = (FrameworkElement)DriveUsed.Parent;
        double w = bar.ActualWidth > 0 ? bar.ActualWidth : 384;
        if (driveTotal > 0)
        {
            double usedW = Math.Max(0, (driveUsed - size) * w / driveTotal);
            DriveUsed.Width = usedW;
            DriveSelected.Margin = new Thickness(usedW, 0, 0, 0);
            DriveSelected.Width = Math.Max(size > 0 ? 6 : 0, size * w / driveTotal);
        }
    }

    void Clean_Click(object sender, RoutedEventArgs e)
    {
        var sel = nodes.Where(n => n.Selected).ToList();
        if (sel.Count == 0) return;
        long size = sel.Sum(n => n.Size);
        var what = sel.Count == 1 ? $"“{sel[0].Name}”" : $"{sel.Count} items";
        if (MessageBox.Show(this, $"Move {what} ({Fmt.Size(size)}) to the Recycle Bin?\n\nThe space is freed once you empty the Recycle Bin. Until then you can restore them.",
                "Clean up", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        CleanBtn.IsEnabled = false;
        FileOps.Delete(sel.Select(n => n.Path), true, hwnd, () => Dispatcher.BeginInvoke(() =>
        {
            Scan();
            main.ToastFromSpace($"Moved {what} to the Recycle Bin");
        }));
    }
}
