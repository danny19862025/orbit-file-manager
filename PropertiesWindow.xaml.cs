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

    void Build()
    {
        bool single = items.Count == 1;
        var first = items[0];
        bool remote = items.Any(i => i.IsRemote);

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
            var parents = items.Select(i => i.IsRemote ? Sftp.ParentOf(i.FullPath) : Path.GetDirectoryName(i.FullPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
                AddRow("Server", Sftp.Parse(first.FullPath).Host);
            }
        }

        // attributes (local only)
        if (remote) AttrPanel.Visibility = Visibility.Collapsed;
        else LoadAttributes();
        WinPropsBtn.Visibility = remote ? Visibility.Collapsed : Visibility.Visible;

        if (needsScan) _ = ScanAsync(fileBytes);
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
        foreach (var f in Sftp.List(url))
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
