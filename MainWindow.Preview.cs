using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IOPath = System.IO.Path;

namespace Orbit;

/// <summary>Quick preview: press Space on a file to look inside it without opening an app.</summary>
public partial class MainWindow
{
    bool previewOpen;
    FileItem previewItem;
    string previewLocalPath;
    CancellationTokenSource previewCts;
    DispatcherTimer mediaTimer;
    bool seeking, mediaPlaying;

    sealed record ListRow(string Name, string Size, string Extra, ImageSource Icon);

    static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".json", ".jsonc", ".xml", ".xaml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf",
        ".csv", ".tsv", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".py", ".cs", ".csproj", ".sln", ".props", ".targets", ".cpp",
        ".cc", ".c", ".h", ".hpp", ".java", ".kt", ".go", ".rs", ".rb", ".php", ".swift", ".dart", ".lua", ".r", ".sh", ".bash",
        ".zsh", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".html", ".htm", ".css", ".scss", ".sass", ".less", ".vue", ".svelte",
        ".sql", ".gitignore", ".gitattributes", ".gitconfig", ".editorconfig", ".env", ".properties", ".gradle", ".srt", ".vtt",
        ".svg", ".manifest", ".reg", ".nfo", ".diff", ".patch", ".tex", ".rst", ".adoc", ".lock"
    };
    static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif", ".avif", ".jxr", ".wdp", ".dds" };
    static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg", ".3gp" };
    static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".aiff", ".mid" };

    const long MaxRemotePreview = 64L * 1024 * 1024;
    const int MaxTextBytes = 1024 * 1024;

    // =====================================================================
    // Open / close
    // =====================================================================
    void TogglePreview()
    {
        if (previewOpen) ClosePreview();
        else OpenPreview();
    }

    void OpenPreview()
    {
        var list = ActiveList;
        var f = list.SelectedItem as FileItem;
        if (Keyboard.FocusedElement is ListBoxItem lbi && lbi.DataContext is FileItem focused && list.SelectedItems.Contains(focused)) f = focused;
        if (f == null) { Toast("Select a file, then press Space to preview it"); return; }
        previewOpen = true;
        PreviewOverlay.Visibility = Visibility.Visible;
        ShowPreview(f);
    }

    void ClosePreview()
    {
        if (!previewOpen) return;
        previewOpen = false;
        previewCts?.Cancel();
        StopMedia();
        PreviewOverlay.Visibility = Visibility.Collapsed;
        previewItem = null;
        FocusList();
    }

    void PreviewBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, PreviewOverlay)) { ClosePreview(); e.Handled = true; }
    }

    void PreviewCard_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
    void PvClose_Click(object sender, RoutedEventArgs e) => ClosePreview();

    void PvOpen_Click(object sender, RoutedEventArgs e)
    {
        var f = previewItem;
        if (f == null) return;
        ClosePreview();
        OpenItems(new List<FileItem> { f });
    }

    void PvOpenWith_Click(object sender, RoutedEventArgs e)
    {
        var path = previewLocalPath ?? previewItem?.FullPath;
        if (path != null && !IsRemote(path) && File.Exists(path)) Shell.OpenWith(path);
    }

    /// <summary>Follow the selection while the preview is open (arrow keys in the list).</summary>
    void PreviewFollowSelection()
    {
        if (!previewOpen) return;
        if (ActiveList.SelectedItem is FileItem f && !ReferenceEquals(f, previewItem)) ShowPreview(f);
    }

    // =====================================================================
    // Rendering
    // =====================================================================
    void ResetPreviewViews()
    {
        StopMedia();
        PvImage.Source = null;
        PvText.Text = "";
        PvPagesPanel.Children.Clear();
        PvList.ItemsSource = null;
        PvInfoImage.Source = null;
        PvNote.Text = "";
        foreach (var el in new UIElement[] { PvImage, PvText, PvMediaHost, PvPages, PvList, PvInfo, PvMediaBar, PvAudioGlyph })
            el.Visibility = Visibility.Collapsed;
        PvLoading.Visibility = Visibility.Visible;
    }

    void Show(UIElement el)
    {
        PvLoading.Visibility = Visibility.Collapsed;
        el.Visibility = Visibility.Visible;
    }

    async void ShowPreview(FileItem f)
    {
        previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        previewCts = cts;
        var ct = cts.Token;
        previewItem = f;
        previewLocalPath = null;
        ResetPreviewViews();

        PvName.Text = f.Name;
        var meta = new List<string> { f.TypeName };
        if (!f.IsDir) meta.Add(f.SizeText);
        if (f.ModifiedText.Length > 0) meta.Add("Modified " + f.ModifiedText);
        if (f.IsRemote) meta.Add(Sftp.Parse(f.FullPath).Host);
        PvMeta.Text = string.Join("  ·  ", meta.Where(m => !string.IsNullOrEmpty(m)));
        PvIcon.SetBinding(Image.SourceProperty, new Binding(nameof(FileItem.LargeIcon)) { Source = f });
        PvOpenBtn.Content = f.IsDir ? "Open folder" : "Open";

        try
        {
            if (f.IsDir) { await PreviewFolder(f, ct); return; }

            var ext = f.Ext;
            string local = f.FullPath;
            if (f.IsRemote && !f.InArchive)
            {
                if (f.Size > MaxRemotePreview)
                {
                    ShowInfo(null, $"This file is {f.SizeText} — too large to preview from the server.\nUse Download, then open it.");
                    return;
                }
                local = Sftp.TempPathFor(f.FullPath);
                var url = f.FullPath;
                await Task.Run(() => Sftp.Download(url, local), ct);
                if (ct.IsCancellationRequested) return;
            }
            if (f.InArchive)
            {
                var url = f.FullPath;
                local = (await Task.Run(() => Archives.ExtractToTemp(new[] { url }), ct)).FirstOrDefault() ?? local;
                if (ct.IsCancellationRequested) return;
            }
            previewLocalPath = local;

            if (f.IsCloud && !f.IsRemote && (ImageExts.Contains(ext) || VideoExts.Contains(ext) || AudioExts.Contains(ext) || ext == ".pdf"))
                PvNote.Text = "Online-only file — previewing downloads it from the cloud";

            if (ImageExts.Contains(ext)) await PreviewImage(local, ct);
            else if (VideoExts.Contains(ext) || AudioExts.Contains(ext)) PreviewMedia(local, AudioExts.Contains(ext));
            else if (ext == ".pdf") await PreviewPdf(local, ct);
            else if (ext == ".zip" || ext == ".jar" || ext == ".nupkg") await PreviewZip(local, ct);
            else if (TextExts.Contains(ext) || await LooksLikeText(local, f.Size, ct)) await PreviewText(local, ct);
            else await PreviewFallback(f, local, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            var msg = f.IsRemote ? Sftp.Describe(ex) : ex.Message;
            try { await PreviewFallback(f, previewLocalPath ?? f.FullPath, ct, "Couldn't preview this file: " + msg); }
            catch { ShowInfo(null, "Couldn't preview this file: " + msg); }
        }
    }

    void ShowInfo(ImageSource img, string text)
    {
        PvInfoImage.Source = img ?? previewItem?.LargeIcon;
        PvInfoImage.Visibility = PvInfoImage.Source == null ? Visibility.Collapsed : Visibility.Visible;
        PvInfoText.Text = text;
        Show(PvInfo);
    }

    // ---------- images ----------
    async Task PreviewImage(string path, CancellationToken ct)
    {
        var (img, w, h) = await Task.Run(() =>
        {
            var bytes = File.ReadAllBytes(path);
            int pw = 0, ph = 0;
            try
            {
                using var ms0 = new MemoryStream(bytes);
                var frame = BitmapDecoder.Create(ms0, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                pw = frame.PixelWidth; ph = frame.PixelHeight;
            }
            catch { }
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = new MemoryStream(bytes);
            if (pw > 2600) bi.DecodePixelWidth = 2600; // keep huge photos light on memory
            bi.EndInit();
            bi.Freeze();
            return ((ImageSource)bi, pw, ph);
        }, ct);
        if (ct.IsCancellationRequested) return;
        PvImage.Source = img;
        if (w > 0) PvMeta.Text += $"  ·  {w} × {h}";
        Show(PvImage);
    }

    // ---------- text / code ----------
    static async Task<bool> LooksLikeText(string path, long size, CancellationToken ct)
    {
        if (size <= 0 || size > 8L * 1024 * 1024) return false;
        return await Task.Run(() =>
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[Math.Min(8192, (int)Math.Min(size, 8192))];
            int n = fs.Read(buf, 0, buf.Length);
            if (n >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF))) return true; // UTF-16 BOM
            int control = 0;
            for (int i = 0; i < n; i++)
            {
                if (buf[i] == 0) return false;
                if (buf[i] < 8 || (buf[i] > 13 && buf[i] < 32 && buf[i] != 27)) control++;
            }
            return n > 0 && control < n / 50 + 1;
        }, ct);
    }

    async Task PreviewText(string path, CancellationToken ct)
    {
        var (text, truncated) = await Task.Run(() =>
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bool cut = fs.Length > MaxTextBytes;
            var buf = new byte[Math.Min(fs.Length, MaxTextBytes)];
            int read = 0;
            while (read < buf.Length) { int n = fs.Read(buf, read, buf.Length - read); if (n == 0) break; read += n; }
            using var reader = new StreamReader(new MemoryStream(buf, 0, read), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return (reader.ReadToEnd(), cut);
        }, ct);
        if (ct.IsCancellationRequested) return;
        PvText.Text = text;
        PvText.ScrollToHome();
        int lines = 1;
        foreach (var ch in text) if (ch == '\n') lines++;
        PvMeta.Text += $"  ·  {lines:N0} lines";
        if (truncated) PvNote.Text = "Showing the first 1 MB";
        Show(PvText);
    }

    // ---------- video / audio ----------
    void PreviewMedia(string path, bool audio)
    {
        mediaTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => UpdateMediaTime(), Dispatcher);
        PvMedia.Source = new Uri(path);
        PvAudioGlyph.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        PvMediaBar.Visibility = Visibility.Visible;
        PvSeek.Value = 0;
        PvTime.Text = "";
        Show(PvMediaHost);
        PvMedia.Play();
        mediaPlaying = true;
        PvPlayBtn.Content = ""; // pause
        mediaTimer.Start();
    }

    void PvMedia_Opened(object sender, RoutedEventArgs e)
    {
        if (PvMedia.NaturalDuration.HasTimeSpan) PvSeek.Maximum = Math.Max(1, PvMedia.NaturalDuration.TimeSpan.TotalSeconds);
        if (PvMedia.NaturalVideoWidth > 0) PvMeta.Text += $"  ·  {PvMedia.NaturalVideoWidth} × {PvMedia.NaturalVideoHeight}";
        if (PvMedia.NaturalDuration.HasTimeSpan) PvMeta.Text += "  ·  " + FormatTime(PvMedia.NaturalDuration.TimeSpan);
    }

    void PvMedia_Ended(object sender, RoutedEventArgs e)
    {
        mediaPlaying = false;
        PvPlayBtn.Content = "";
        PvMedia.Stop();
    }

    async void PvMedia_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        StopMedia();
        if (previewItem != null)
            await PreviewFallback(previewItem, previewLocalPath ?? previewItem.FullPath, previewCts?.Token ?? default,
                "Windows can't play this format here. Press Open to use your media player.");
    }

    void PvPlay_Click(object sender, RoutedEventArgs e)
    {
        if (mediaPlaying) { PvMedia.Pause(); mediaPlaying = false; PvPlayBtn.Content = ""; }
        else { PvMedia.Play(); mediaPlaying = true; PvPlayBtn.Content = ""; }
    }

    void PvSeek_DragStarted(object sender, DragStartedEventArgs e) => seeking = true;

    void PvSeek_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        seeking = false;
        PvMedia.Position = TimeSpan.FromSeconds(PvSeek.Value);
    }

    void PvSeek_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!seeking) PvMedia.Position = TimeSpan.FromSeconds(PvSeek.Value);
    }

    void UpdateMediaTime()
    {
        if (PvMediaHost.Visibility != Visibility.Visible) return;
        var pos = PvMedia.Position;
        if (!seeking) PvSeek.Value = Math.Min(PvSeek.Maximum, pos.TotalSeconds);
        var total = PvMedia.NaturalDuration.HasTimeSpan ? PvMedia.NaturalDuration.TimeSpan : TimeSpan.Zero;
        PvTime.Text = $"{FormatTime(pos)} / {FormatTime(total)}";
    }

    static string FormatTime(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    void StopMedia()
    {
        mediaTimer?.Stop();
        mediaPlaying = false;
        try { PvMedia.Stop(); } catch { }
        PvMedia.Source = null;
    }

    // ---------- PDF (Windows' built-in PDF renderer) ----------
    async Task PreviewPdf(string path, CancellationToken ct)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        var doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
        if (ct.IsCancellationRequested) return;
        uint pages = doc.PageCount;
        PvMeta.Text += $"  ·  {pages} page{(pages == 1 ? "" : "s")}";
        uint show = Math.Min(pages, 20u);
        if (pages > show) PvNote.Text = $"Showing the first {show} of {pages} pages";
        Show(PvPages);
        for (uint i = 0; i < show; i++)
        {
            if (ct.IsCancellationRequested) return;
            using var page = doc.GetPage(i);
            using var ras = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(ras, new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = 1400 });
            var bi = new BitmapImage();
            using (var s = ras.AsStreamForRead())
            {
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.StreamSource = s;
                bi.EndInit();
            }
            bi.Freeze();
            if (ct.IsCancellationRequested) return;
            PvPagesPanel.Children.Add(new Border
            {
                Margin = new Thickness(0, 0, 0, 14), Background = Brushes.White, CornerRadius = new CornerRadius(4),
                MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Center,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35 },
                Child = new Image { Source = bi, Stretch = Stretch.Uniform }
            });
        }
    }

    // ---------- ZIP contents ----------
    async Task PreviewZip(string path, CancellationToken ct)
    {
        var (rows, count, total, packed) = await Task.Run(() =>
        {
            using var zip = ZipFile.OpenRead(path);
            var entries = zip.Entries.ToList();
            long t = entries.Sum(x => x.Length), p = entries.Sum(x => x.CompressedLength);
            var r = entries.OrderBy(x => x.FullName, StringComparer.CurrentCultureIgnoreCase).Take(1000)
                .Select(x => new ListRow(x.FullName, x.FullName.EndsWith('/') ? "" : Fmt.Size(x.Length),
                                         x.FullName.EndsWith('/') ? "" : Fmt.Size(x.CompressedLength),
                                         IconPack.IsActive ? IconPack.Render(IconPack.Resolve(IOPath.GetFileName(x.FullName.TrimEnd('/')), x.FullName.EndsWith('/'))) : null))
                .ToList();
            return (r, entries.Count, t, p);
        }, ct);
        if (ct.IsCancellationRequested) return;
        PvListExtraCol.Header = "Packed";
        PvList.ItemsSource = rows;
        PvMeta.Text += $"  ·  {count:N0} items  ·  {Fmt.Size(total)} unpacked";
        if (count > rows.Count) PvNote.Text = $"Showing the first {rows.Count:N0} items";
        Show(PvList);
    }

    // ---------- folders ----------
    async Task PreviewFolder(FileItem f, CancellationToken ct)
    {
        var path = f.FullPath;
        List<FileItem> children = await Task.Run(() =>
        {
            if (f.InArchive) return Archives.List(path);
            if (f.IsRemote) return Sftp.List(path);
            var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
            var list = new List<FileItem>();
            foreach (var fsi in new DirectoryInfo(path).EnumerateFileSystemInfos("*", opts))
            {
                try { list.Add(FileItem.From(fsi)); } catch { }
                if (list.Count >= 5000) break;
            }
            return list;
        }, ct);
        if (ct.IsCancellationRequested) return;
        var visible = children.Where(c => S.ShowHidden || !c.IsHidden).ToList();
        visible.Sort(new ItemComparer("name", false).Compare);
        int dirs = visible.Count(c => c.IsDir), files = visible.Count - dirs;
        long size = visible.Where(c => !c.IsDir).Sum(c => c.Size);
        PvMeta.Text += $"  ·  {dirs:N0} folders, {files:N0} files" + (size > 0 ? $"  ·  {Fmt.Size(size)} in files here" : "");
        if (visible.Count == 0) { ShowInfo(null, "This folder is empty."); return; }
        PvListExtraCol.Header = "Modified";
        PvList.ItemsSource = visible.Take(1000).Select(c => new ListRow(c.Name, c.SizeText, c.Modified.Year < 1700 ? "" : c.Modified.ToString("d"),
            IconPack.IsActive ? IconPack.Render(IconPack.Resolve(c.Name, c.IsDir)) : c.SmallIcon)).ToList();
        if (visible.Count > 1000) PvNote.Text = "Showing the first 1,000 items";
        Show(PvList);
    }

    // ---------- anything else: big shell thumbnail + details ----------
    async Task PreviewFallback(FileItem f, string localPath, CancellationToken ct, string message = null)
    {
        ImageSource big = null;
        if (!IsRemote(localPath) && File.Exists(localPath))
        {
            int px = (int)Math.Round(256 * IconService.Scale);
            var flags = f.IsCloud ? Native.SIIGBF_INCACHEONLY : Native.SIIGBF_RESIZETOFIT;
            big = await Task.Run(() => (ImageSource)Native.GetShellImage(localPath, px, flags) ?? Native.GetShellImage(localPath, px, Native.SIIGBF_ICONONLY), ct);
        }
        if (ct.IsCancellationRequested) return;
        var lines = new List<string>();
        if (message != null) lines.Add(message);
        else lines.Add("No built-in preview for this type — press Open to use its app.");
        try
        {
            if (!IsRemote(localPath) && File.Exists(localPath))
            {
                var fi = new FileInfo(localPath);
                lines.Add($"\nCreated {fi.CreationTime:g}\nLocation {IOPath.GetDirectoryName(f.FullPath)}");
            }
        }
        catch { }
        ShowInfo(big, string.Join("\n", lines));
    }
}
