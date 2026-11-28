using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Orbit;

/// <summary>Orbit-styled Properties for one or more files/folders, with live folder-size calculation.</summary>
public partial class PropertiesWindow : Window
{
    readonly MainWindow main;
    readonly List<FileItem> items;
    readonly CancellationTokenSource cts = new();
    TextBox sizeBox, containsBox;
    bool updatingChecks;

    public PropertiesWindow(MainWindow owner, List<FileItem> selection)
    {
        InitializeComponent();
        Owner = owner;
        main = owner;
        items = selection;
        Closed += (_, _) => cts.Cancel();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Build();
    }

    static string Bytes(long n) => n < 1024 ? $"{n:N0} bytes" : $"{Fmt.Size(n)}  ({n:N0} bytes)";
    static string When(DateTime d) => d.Year < 1700 ? "—" : d.ToString("dddd, d MMMM yyyy  ·  h:mm tt", CultureInfo.CurrentCulture);

    TextBox AddRow(string key, string value)
    {
        var g = Details;
        int row = g.RowDefinitions.Count;
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var k = new TextBlock { Text = key, Style = (Style)FindResource("Key") };
        Grid.SetRow(k, row);
        var v = new TextBox { Text = value, Style = (Style)FindResource("Val") };
        Grid.SetRow(v, row);
        Grid.SetColumn(v, 1);
        g.Children.Add(k);
        g.Children.Add(v);
        return v;
    }

    /// <summary>The drive a path is the root of ("C:\"), or null.</summary>
    static DriveInfo DriveOf(FileItem f)
    {
        if (f.IsRemote || f.InArchive || !f.IsDir) return null;
        try
        {
            var root = Path.GetPathRoot(f.FullPath);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\") ||
                !string.Equals(root.TrimEnd('\\'), f.FullPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;
            var d = new DriveInfo(root);
            return d.IsReady ? d : null;
        }
        catch { return null; }
    }

    void Build()
    {
        bool single = items.Count == 1;
        var first = items[0];
        bool remote = items.Any(i => i.IsRemote);

        if (single && DriveOf(first) is { } drive) { BuildDrive(drive, first); return; }

        if (single)
        {
            TitleText.Text = $"{first.Name} — Properties";
            NameBox.Text = first.Name;
            KindText.Text = first.TypeName;
            BigIcon.Source = first.IsRemote || IconPack.IsActive
                ? first.LargeIcon
                : (ImageSource)Native.GetShellImage(first.FullPath, (int)Math.Round(56 * IconService.Scale), Native.SIIGBF_ICONONLY) ?? first.LargeIcon;
            if (BigIcon.Source == null)
                BigIcon.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(FileItem.LargeIcon)) { Source = first });
        }
        else
        {
            int dirs = items.Count(i => i.IsDir);
            TitleText.Text = $"{items.Count} items — Properties";
            NameBox.Text = $"{items.Count:N0} items selected";
            KindText.Text = dirs == 0 ? $"{items.Count} files" : dirs == items.Count ? $"{dirs} folders" : $"{items.Count - dirs} files, {dirs} folders";
            BigIcon.Source = first.LargeIcon;
        }

        // location
        string parent = null;
        try
        {
            var parents = items.Select(i => i.InArchive ? Archives.ParentOf(i.FullPath) : i.IsRemote ? Sftp.ParentOf(i.FullPath) : Path.GetDirectoryName(i.FullPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            parent = parents.Count == 1 ? parents[0] : "Several folders";
        }
        catch { }
        if (single) AddRow("Type", first.TypeName + (first.Ext.Length > 0 ? $"  ({first.Ext})" : ""));
        AddRow("Location", parent ?? "—");

        // size: files are instant; folders are counted in the background
        long fileBytes = items.Where(i => !i.IsDir && i.Size > 0).Sum(i => i.Size);
        bool needsScan = items.Any(i => i.IsDir);
        sizeBox = AddRow("Size", needsScan ? "Calculating…" : Bytes(fileBytes));
        if (needsScan) containsBox = AddRow("Contains", "Calculating…");

        if (single)
        {
            if (!first.IsRemote)
            {
                try
                {
                    var info = first.IsDir ? (FileSystemInfo)new DirectoryInfo(first.FullPath) : new FileInfo(first.FullPath);
                    AddRow("Created", When(info.CreationTime));
                    AddRow("Modified", When(info.LastWriteTime));
                    AddRow("Accessed", When(info.LastAccessTime));
                }
                catch { AddRow("Modified", When(first.Modified)); }
                if (first.IsCloud) AddRow("Availability", "Online-only (stored in the cloud)");
                TryAddImageInfo(first);
            }
            else
            {
                AddRow("Modified", When(first.Modified));
                if (first.InArchive)
                {
                    AddRow("Packed size", first.IsDir ? "—" : Bytes(first.Packed));
                    AddRow("Archive", Archives.Parse(first.FullPath).archive);
                }
                else AddRow("Server", Sftp.Parse(first.FullPath).Host);
            }
        }

        // attributes (local only)
        if (remote) AttrPanel.Visibility = Visibility.Collapsed;
        else LoadAttributes();
        WinPropsBtn.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;

        if (needsScan) _ = ScanAsync(fileBytes);
    }

    // ---------- drives ----------
    /// <summary>A whole drive: label, type, file system and a used / free ring — no file counting.</summary>
    void BuildDrive(DriveInfo d, FileItem item)
    {
        string letter = d.Name.TrimEnd('\\');
        string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? DriveKind(d.DriveType) : d.VolumeLabel;
        TitleText.Text = $"{label} ({letter}) — Properties";
        NameBox.Text = $"{label} ({letter})";
        KindText.Text = DriveKind(d.DriveType);
        BigIcon.Source = (ImageSource)Native.GetShellImage(d.Name, (int)Math.Round(56 * IconService.Scale), Native.SIIGBF_ICONONLY) ?? item.LargeIcon;

        long total = d.TotalSize, free = d.TotalFreeSpace, used = Math.Max(0, total - free);
        AddRow("Type", DriveKind(d.DriveType));
        AddRow("File system", d.DriveFormat);
        AddRow("Location", d.Name);

        var accent = (Brush)FindResource("AccentBrush");
        var track = (Brush)FindResource("TrackBrush");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var legend = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        legend.Children.Add(LegendRow(accent, "Used space", used));
        legend.Children.Add(LegendRow(track, "Free space", free));
        legend.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 6, 0, 10), Background = (Brush)FindResource("LineBrush") });
        legend.Children.Add(LegendRow(null, "Capacity", total));
        grid.Children.Add(legend);

        double frac = total > 0 ? (double)used / total : 0;
        var ring = new Grid { Width = 128, Height = 128, Margin = new Thickness(20, 0, 0, 0) };
        ring.Children.Add(Arc(1.0, track));
        if (frac > 0) ring.Children.Add(Arc(Math.Max(frac, 0.005), accent));
        var pct = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        pct.Children.Add(new TextBlock { Text = $"{frac * 100:0}%", FontSize = 22, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        var sub = new TextBlock { Text = "used", HorizontalAlignment = HorizontalAlignment.Center };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        pct.Children.Add(sub);
        ring.Children.Add(pct);
        Grid.SetColumn(ring, 1);
        grid.Children.Add(ring);
        DrivePanel.Children.Add(grid);

        var analyze = new Button { Content = "Analyze disk space", Style = (Style)FindResource("DlgButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        analyze.Click += (_, _) => { main.OpenSpace(d.Name); Close(); };
        DrivePanel.Children.Add(analyze);
        DrivePanel.Visibility = Visibility.Visible;

        AttrPanel.Visibility = Visibility.Collapsed; // drives have no Read-only / Hidden switches
        WinPropsBtn.Visibility = Visibility.Visible; // Tools, Hardware, Sharing, Security… live there
    }

    static string DriveKind(DriveType t) => t switch
    {
        DriveType.Fixed => "Local Disk",
        DriveType.Removable => "Removable Disk",
        DriveType.Network => "Network Drive",
        DriveType.CDRom => "CD / DVD Drive",
        DriveType.Ram => "RAM Disk",
        _ => "Drive"
    };

    FrameworkElement LegendRow(Brush swatch, string label, long bytes)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var size = new TextBlock { Text = Fmt.Size(bytes), FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 0, 0) };
        DockPanel.SetDock(size, Dock.Right);
        row.Children.Add(size);
        if (swatch != null)
        {
            var sw = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = swatch, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(sw);
        }
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = label });
        var exact = new TextBlock { Text = $"{bytes:N0} bytes", FontSize = 11.5 };
        exact.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        text.Children.Add(exact);
        row.Children.Add(text);
        return row;
    }

    /// <summary>A ring segment from 12 o'clock, clockwise, covering <paramref name="fraction"/> of the circle.</summary>
    static FrameworkElement Arc(double fraction, Brush brush)
    {
        const double size = 128, thick = 18, r = (size - thick) / 2, c = size / 2;
        if (fraction >= 0.9999)
            return new System.Windows.Shapes.Ellipse { Width = size - thick, Height = size - thick, Stroke = brush, StrokeThickness = thick };
        double a = fraction * 2 * Math.PI;
        var start = new Point(c, c - r);
        var end = new Point(c + r * Math.Sin(a), c - r * Math.Cos(a));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        return new System.Windows.Shapes.Path
        {
            Data = new PathGeometry(new[] { fig }), Stroke = brush, StrokeThickness = thick,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = size, Height = size
        };
    }

    void TryAddImageInfo(FileItem f)
    {
        var ext = f.Ext;
        if (!new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".ico", ".heic", ".jfif" }.Contains(ext)) return;
        try
        {
            using var fs = new FileStream(f.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            AddRow("Dimensions", $"{frame.PixelWidth} × {frame.PixelHeight} pixels" + (frame.DpiX > 0 ? $"  ·  {Math.Round(frame.DpiX)} dpi" : ""));
        }
        catch { }
    }

    async Task ScanAsync(long baseBytes)
    {
        var ct = cts.Token;
        long bytes = baseBytes, files = items.Count(i => !i.IsDir), folders = 0;
        int skipped = 0;
        var progress = new Progress<(long b, long f, long d)>(p =>
        {
            sizeBox.Text = Bytes(p.b) + "  …";
            containsBox.Text = $"{p.f:N0} files, {p.d:N0} folders  …";
        });
        IProgress<(long, long, long)> report = progress;
        try
        {
            await Task.Run(() =>
            {
                var last = DateTime.UtcNow;
                foreach (var it in items.Where(i => i.IsDir))
                {
                    if (it.IsRemote)
                    {
                        CountRemote(it.FullPath, ref bytes, ref files, ref folders, ct);
                        continue;
                    }
                    var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false };
                    // don't follow junctions/symlinks into other trees (avoids double counting and loops)
                    opts.AttributesToSkip = FileAttributes.ReparsePoint;
                    foreach (var fsi in new DirectoryInfo(it.FullPath).EnumerateFileSystemInfos("*", opts))
                    {
                        ct.ThrowIfCancellationRequested();
                        if ((fsi.Attributes & FileAttributes.Directory) != 0) folders++;
                        else { files++; try { bytes += ((FileInfo)fsi).Length; } catch { skipped++; } }
                        if ((DateTime.UtcNow - last).TotalMilliseconds > 250) { last = DateTime.UtcNow; report.Report((bytes, files, folders)); }
                    }
                }
            }, ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { if (!ct.IsCancellationRequested) containsBox.Text = "Couldn't finish: " + ex.Message; return; }
        if (ct.IsCancellationRequested) return;
        sizeBox.Text = Bytes(bytes);
        containsBox.Text = $"{files:N0} files, {folders:N0} folders" + (skipped > 0 ? $"  ({skipped} couldn't be read)" : "");
    }

    static void CountRemote(string url, ref long bytes, ref long files, ref long folders, CancellationToken ct)
    {
        foreach (var f in Archives.IsArc(url) ? Archives.List(url) : Sftp.List(url))
        {
            ct.ThrowIfCancellationRequested();
            if (f.IsDir) { folders++; CountRemote(f.FullPath, ref bytes, ref files, ref folders, ct); }
            else { files++; bytes += Math.Max(0, f.Size); }
        }
    }

    // ---------- attributes ----------
    void LoadAttributes()
    {
        updatingChecks = true;
        try
        {
            var attrs = items.Select(i => SafeAttrs(i.FullPath)).ToList();
            ReadOnlyCheck.IsChecked = Tri(attrs.Select(a => (a & FileAttributes.ReadOnly) != 0));
            HiddenCheck.IsChecked = Tri(attrs.Select(a => (a & FileAttributes.Hidden) != 0));
        }
        finally { updatingChecks = false; }
    }

    static FileAttributes SafeAttrs(string p) { try { return File.GetAttributes(p); } catch { return 0; } }

    static bool? Tri(IEnumerable<bool> values)
    {
        var v = values.Distinct().ToList();
        return v.Count == 1 ? v[0] : (bool?)null;
    }

    void Attr_Click(object sender, RoutedEventArgs e)
    {
        if (updatingChecks) return;
        var box = (CheckBox)sender;
        bool on = box.IsChecked == true; // a mixed state turns on with the first click
        var flag = ReferenceEquals(box, ReadOnlyCheck) ? FileAttributes.ReadOnly : FileAttributes.Hidden;
        var errors = new List<string>();
        foreach (var it in items)
        {
            try
            {
                var a = File.GetAttributes(it.FullPath);
                File.SetAttributes(it.FullPath, on ? a | flag : a & ~flag);
            }
            catch (Exception ex) { errors.Add($"{it.Name}: {ex.Message}"); }
        }
        AttrError.Text = string.Join("\n", errors.Take(3));
        AttrError.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LoadAttributes();
        main.AfterAttributesChanged();
    }

    void WinProps_Click(object sender, RoutedEventArgs e) => main.ShowWindowsProperties(items[0].FullPath);

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource == this) DragMove();
    }
}
