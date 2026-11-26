using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Orbit;

/// <summary>Orbit-styled Properties for one or more files/folders, with live folder-size calculation.</summary>
public sealed class PropertiesWindow : OrbitWindow
{
    readonly MainWindow main;
    readonly List<FileItem> items;
    readonly CancellationTokenSource cts = new();
    readonly Grid details = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(22, 16, 22, 4) };
    TextBox sizeBox, containsBox;
    CheckBox readOnlyCheck, hiddenCheck;
    TextBlock attrError;
    bool updatingChecks;

    public PropertiesWindow(MainWindow owner, List<FileItem> selection)
    {
        main = owner;
        items = selection;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Closed += (_, _) => cts.Cancel();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Build();
    }

    static string Bytes(long n) => n < 1024 ? $"{n:N0} bytes" : $"{Fmt.Size(n)}  ({n:N0} bytes)";
    static string When(DateTime d) => d.Year < 1700 ? "—" : d.ToString("dddd, d MMMM yyyy  ·  h:mm tt", CultureInfo.CurrentCulture);

    TextBox AddRow(string key, string value)
    {
        int row = details.RowDefinitions.Count;
        details.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var k = new TextBlock { Text = key, Margin = new Thickness(0, 0, 16, 10), VerticalAlignment = VerticalAlignment.Top }.Brush(TextBlock.ForegroundProperty, "MutedBrush");
        Grid.SetRow(k, row);
        var v = new TextBox { Text = value, Classes = { "val" } };
        Grid.SetRow(v, row);
        Grid.SetColumn(v, 1);
        details.Children.Add(k);
        details.Children.Add(v);
        return v;
    }

    void Build()
    {
        bool single = items.Count == 1;
        var first = items[0];
        bool remote = items.Any(i => i.IsRemote);

        var stack = new StackPanel();
        stack.Children.Add(Ui.TitleBar(this, null, single ? $"{first.Name} — Properties" : $"{items.Count} items — Properties", out var titleText, out _, Close, 40, 18));
        Title = titleText.Text;

        // header
        var header = new DockPanel { Margin = new Thickness(22, 6, 22, 16) };
        var bigIcon = new Image { Width = 56, Height = 56, Margin = new Thickness(0, 0, 16, 0) };
        RenderOptions.SetBitmapInterpolationMode(bigIcon, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        bigIcon.Bind(Image.SourceProperty, new Avalonia.Data.Binding(nameof(FileItem.LargeIcon)) { Source = first });
        header.Children.Add(bigIcon);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameBox = new TextBox { Classes = { "val" }, FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 2) };
        var kind = new TextBlock().Brush(TextBlock.ForegroundProperty, "MutedBrush");
        names.Children.Add(nameBox);
        names.Children.Add(kind);
        header.Children.Add(names);
        stack.Children.Add(header);
        stack.Children.Add(Ui.Line(new Thickness(22, 0)));
        stack.Children.Add(details);

        if (single)
        {
            nameBox.Text = first.Name;
            kind.Text = first.TypeName;
        }
        else
        {
            int dirs = items.Count(i => i.IsDir);
            nameBox.Text = $"{items.Count:N0} items selected";
            kind.Text = dirs == 0 ? $"{items.Count} files" : dirs == items.Count ? $"{dirs} folders" : $"{items.Count - dirs} files, {dirs} folders";
        }

        // location
        string parent = null;
        try
        {
            var parents = items.Select(i => i.InArchive ? Archives.ParentOf(i.FullPath) : i.IsRemote ? Sftp.ParentOf(i.FullPath) : Path.GetDirectoryName(i.FullPath)).Distinct(StringComparer.Ordinal).ToList();
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
                    if (info.LinkTarget != null) AddRow("Links to", info.LinkTarget);
                    AddRow("Permissions", Permissions(info));
                }
                catch { AddRow("Modified", When(first.Modified)); }
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
        var attrPanel = new StackPanel { Margin = new Thickness(22, 4, 22, 0) };
        attrPanel.Children.Add(Ui.Line(new Thickness(0, 0, 0, 14)));
        var attrRow = new StackPanel { Orientation = Orientation.Horizontal };
        attrRow.Children.Add(new TextBlock { Text = "Attributes", Margin = new Thickness(0, 0, 22, 0), VerticalAlignment = VerticalAlignment.Center }.Brush(TextBlock.ForegroundProperty, "MutedBrush"));
        readOnlyCheck = Ui.Check("Read-only");
        readOnlyCheck.Margin = new Thickness(0, 0, 22, 0);
        hiddenCheck = Ui.Check("Hidden");
        ToolTip.SetTip(hiddenCheck, "On Linux a hidden item's name starts with a dot");
        readOnlyCheck.Click += Attr_Click;
        hiddenCheck.Click += Attr_Click;
        attrRow.Children.Add(readOnlyCheck);
        attrRow.Children.Add(hiddenCheck);
        attrPanel.Children.Add(attrRow);
        attrError = new TextBlock { Foreground = new SolidColorBrush(Color.Parse("#F38BA8")), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), IsVisible = false };
        attrPanel.Children.Add(attrError);
        bool noAttrs = remote || items.Any(i => i.IsRecycled);
        attrPanel.IsVisible = !noAttrs;
        stack.Children.Add(attrPanel);
        if (!noAttrs) LoadAttributes();

        // buttons
        var buttons = new DockPanel { Margin = new Thickness(22, 20, 22, 20) };
        var close = Ui.Dlg("Close");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        buttons.Children.Add(close);
        if (!remote && single)
        {
            var sys = Ui.Dlg("System properties…");
            ToolTip.SetTip(sys, "Your desktop file manager's Properties dialog");
            sys.HorizontalAlignment = HorizontalAlignment.Left;
            sys.Click += (_, _) => main.ShowSystemProperties(items[0].FullPath);
            buttons.Children.Add(sys);
        }
        stack.Children.Add(buttons);
        SetBody(stack);

        if (needsScan) _ = ScanAsync(fileBytes);
    }

    static string Permissions(FileSystemInfo info)
    {
        try
        {
            var m = info.UnixFileMode;
            string Tri(UnixFileMode r, UnixFileMode w, UnixFileMode x) => $"{((m & r) != 0 ? 'r' : '-')}{((m & w) != 0 ? 'w' : '-')}{((m & x) != 0 ? 'x' : '-')}";
            var s = (info is DirectoryInfo ? "d" : "-") + Tri(UnixFileMode.UserRead, UnixFileMode.UserWrite, UnixFileMode.UserExecute)
                    + Tri(UnixFileMode.GroupRead, UnixFileMode.GroupWrite, UnixFileMode.GroupExecute)
                    + Tri(UnixFileMode.OtherRead, UnixFileMode.OtherWrite, UnixFileMode.OtherExecute);
            var you = (m & UnixFileMode.UserWrite) != 0 ? "you can read and change it" : (m & UnixFileMode.UserRead) != 0 ? "read-only for you" : "not readable";
            return $"{s}  ({you})";
        }
        catch { return "—"; }
    }

    void TryAddImageInfo(FileItem f)
    {
        if (!new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".ico", ".jfif" }.Contains(f.Ext)) return;
        var (w, h) = Orbit.Render.Dimensions(f.FullPath);
        if (w > 0) AddRow("Dimensions", $"{w} × {h} pixels");
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
                    // don't follow symlinks into other trees (avoids double counting and loops)
                    var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false };
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

    // ---------- attributes: read-only = no write permission for you; hidden = name starts with a dot ----------
    void LoadAttributes()
    {
        updatingChecks = true;
        try
        {
            readOnlyCheck.IsChecked = Tri(items.Select(i => { try { return (File.GetUnixFileMode(i.FullPath) & UnixFileMode.UserWrite) == 0; } catch { return false; } }));
            hiddenCheck.IsChecked = Tri(items.Select(i => Path.GetFileName(i.FullPath).StartsWith('.')));
        }
        finally { updatingChecks = false; }
    }

    static bool? Tri(IEnumerable<bool> values)
    {
        var v = values.Distinct().ToList();
        return v.Count == 1 ? v[0] : null;
    }

    void Attr_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (updatingChecks) return;
        var box = (CheckBox)sender;
        bool on = box.IsChecked == true; // a mixed state turns on with the first click
        var errors = new List<string>();
        for (int k = 0; k < items.Count; k++)
        {
            var it = items[k];
            try
            {
                if (ReferenceEquals(box, readOnlyCheck))
                {
                    var m = File.GetUnixFileMode(it.FullPath);
                    var w = UnixFileMode.UserWrite | UnixFileMode.GroupWrite;
                    File.SetUnixFileMode(it.FullPath, on ? m & ~(w | UnixFileMode.OtherWrite) : m | UnixFileMode.UserWrite);
                }
                else
                {
                    var dir = Path.GetDirectoryName(it.FullPath)!;
                    var name = Path.GetFileName(it.FullPath);
                    var newName = on ? (name.StartsWith('.') ? name : "." + name) : name.TrimStart('.');
                    if (newName.Length == 0 || newName == name) continue;
                    var dest = Path.Combine(dir, newName);
                    if (File.Exists(dest) || Directory.Exists(dest)) { errors.Add($"{name}: “{newName}” already exists"); continue; }
                    if (Directory.Exists(it.FullPath) && !it.IsLink) Directory.Move(it.FullPath, dest); else File.Move(it.FullPath, dest);
                    it.Rebase(newName, dest);
                }
            }
            catch (Exception ex) { errors.Add($"{it.Name}: {ex.Message}"); }
        }
        attrError.Text = string.Join("\n", errors.Take(3));
        attrError.IsVisible = errors.Count > 0;
        LoadAttributes();
        main.AfterAttributesChanged();
    }
}
