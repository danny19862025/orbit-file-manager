using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using IOPath = System.IO.Path;

namespace Orbit;

public sealed record ListRow(string Name, string Size, string Extra, IImage Icon);

/// <summary>Quick preview: press Space on a file to look inside it without opening an app.</summary>
public partial class MainWindow
{
    bool previewOpen;
    FileItem previewItem;
    string previewLocalPath;
    CancellationTokenSource previewCts;
    DispatcherTimer mediaTimer;
    bool seeking, mediaPlaying, seekFromCode;
    MpvPlayer player;

    static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".json", ".jsonc", ".xml", ".xaml", ".axaml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf",
        ".csv", ".tsv", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".py", ".cs", ".csproj", ".sln", ".props", ".targets", ".cpp",
        ".cc", ".c", ".h", ".hpp", ".java", ".kt", ".go", ".rs", ".rb", ".php", ".swift", ".dart", ".lua", ".r", ".sh", ".bash",
        ".zsh", ".fish", ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".html", ".htm", ".css", ".scss", ".sass", ".less", ".vue", ".svelte",
        ".sql", ".gitignore", ".gitattributes", ".gitconfig", ".editorconfig", ".env", ".properties", ".gradle", ".srt", ".vtt",
        ".svg", ".manifest", ".reg", ".nfo", ".diff", ".patch", ".tex", ".rst", ".adoc", ".lock", ".desktop", ".service", ".timer",
        ".rules", ".nix", ".cmake", ".mk", ".pkgbuild"
    };
    static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif", ".avif", ".wbmp" };
    static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg", ".3gp", ".flv", ".ogv" };
    static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".aiff", ".mid" };

    const long MaxRemotePreview = 64L * 1024 * 1024;
    const int MaxTextBytes = 1024 * 1024;

    void WirePreview()
    {
        PvSeek.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty && !seekFromCode && player != null) player.Seek(PvSeek.Value);
        };
    }

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
        if (FocusManager?.GetFocusedElement() is ListBoxItem lbi && lbi.DataContext is FileItem focused && list.SelectedItems.Contains(focused)) f = focused;
        if (f == null) { Toast("Select a file, then press Space to preview it"); return; }
        previewOpen = true;
        PreviewOverlay.IsVisible = true;
        ShowPreview(f);
    }

    void ClosePreview()
    {
        if (!previewOpen) return;
        previewOpen = false;
        previewCts?.Cancel();
        StopMedia();
        PreviewOverlay.IsVisible = false;
        previewItem = null;
        FocusList();
    }

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
        if (path != null && !IsRemote(path) && File.Exists(path)) OpenWith(path);
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
        PvListBox.ItemsSource = null;
        PvInfoImage.Source = null;
        PvPoster.Source = null;
        PvNote.Text = "";
        foreach (var el in new Control[] { PvImage, PvText, PvMediaHost, PvPages, PvList, PvInfo, PvMediaBar, PvAudioGlyph, PvPoster })
            el.IsVisible = false;
        PvLoading.IsVisible = true;
    }

    void Show(Control el)
    {
        PvLoading.IsVisible = false;
        el.IsVisible = true;
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
        if (f.IsRemote && !f.InArchive) meta.Add(Sftp.Parse(f.FullPath).Host);
        PvMeta.Text = string.Join("  ·  ", meta.Where(m => !string.IsNullOrEmpty(m)));
        PvIcon.Bind(Image.SourceProperty, new Avalonia.Data.Binding(nameof(FileItem.LargeIcon)) { Source = f });
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

            if (ImageExts.Contains(ext)) await PreviewImage(local, ct);
            else if (VideoExts.Contains(ext) || AudioExts.Contains(ext)) await PreviewMedia(local, AudioExts.Contains(ext), ct);
            else if (ext == ".pdf") await PreviewPdf(local, ct);
            else if (ext is ".zip" or ".jar" or ".nupkg" or ".apk" or ".whl" or ".cbz" or ".vsix" or ".xpi") await PreviewZip(local, ct);
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

    void ShowInfo(IImage img, string text)
    {
        PvInfoImage.Source = img ?? previewItem?.LargeIcon;
        PvInfoImage.IsVisible = PvInfoImage.Source != null;
        PvInfoText.Text = text;
        Show(PvInfo);
    }

    // ---------- images ----------
    async Task PreviewImage(string path, CancellationToken ct)
    {
        var (img, w, h) = await Task.Run(() =>
        {
            var (pw, ph) = Orbit.Render.Dimensions(path);
            Bitmap bmp = null;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                bmp = pw > 2600 || ph > 2600 ? Orbit.Render.Fit(fs, 2600) : new Bitmap(fs); // keep huge photos light on memory
            }
            catch { }
            if (bmp == null)
            {
                // HEIC / AVIF and other formats Skia can't read: let ffmpeg decode them
                var ff = Converter.FindFfmpeg();
                var png = ff == null ? null : IconService.RunBytes(ff, new[] { "-v", "error", "-i", path, "-frames:v", "1", "-vf", "scale='min(2600,iw)':-2", "-f", "image2pipe", "-vcodec", "png", "-" }, 20000);
                if (png is { Length: > 0 }) { bmp = new Bitmap(new MemoryStream(png)); if (pw == 0) { pw = bmp.PixelSize.Width; ph = bmp.PixelSize.Height; } }
            }
            return (bmp, pw, ph);
        }, ct);
        if (ct.IsCancellationRequested) return;
        if (img == null) { await PreviewFallback(previewItem, path, ct, "This picture's format can't be shown here. Press Open to use its app."); return; }
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
        PvText.CaretIndex = 0;
        int lines = 1;
        foreach (var ch in text) if (ch == '\n') lines++;
        PvMeta.Text += $"  ·  {lines:N0} lines";
        if (truncated) PvNote.Text = "Showing the first 1 MB";
        Show(PvText);
    }

    // ---------- video / audio (mpv, embedded into the preview) ----------
    async Task PreviewMedia(string path, bool audio, CancellationToken ct)
    {
        var mpv = IconService.Which("mpv");
        // a poster frame first, so there's something to see right away
        if (!audio)
        {
            var poster = await Task.Run(() => IconServiceThumb(path, 1280), ct);
            if (ct.IsCancellationRequested) return;
            if (poster != null) { PvPoster.Source = poster; PvPoster.IsVisible = true; }
        }
        PvAudioGlyph.IsVisible = audio;
        Show(PvMediaHost);
        if (mpv == null)
        {
            PvNote.Text = "Install mpv to play videos and music right here — or press Open to use your player";
            if (audio) PvAudioGlyph.IsVisible = true;
            return;
        }
        mediaTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => UpdateMediaTime());
        PvMediaBar.IsVisible = true;
        seekFromCode = true; PvSeek.Value = 0; seekFromCode = false;
        PvTime.Text = "";
        var host = new MpvHost();
        PvMedia.Content = audio ? null : host;
        player = new MpvPlayer(mpv);
        var p = player;
        if (audio) p.Start(path, null, audio: true);
        else host.HandleReady += wid => { if (ReferenceEquals(player, p)) { p.Start(path, wid, audio: false); PvPoster.IsVisible = false; } };
        mediaPlaying = true;
        PvPlayBtn.Content = ""; // pause
        mediaTimer.Start();
        bool metaShown = false;
        p.Info += (w, h, dur) => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(player, p) || metaShown) return;
            metaShown = true;
            if (dur > 0) PvSeek.Maximum = Math.Max(1, dur);
            if (w > 0) PvMeta.Text += $"  ·  {w} × {h}";
            if (dur > 0) PvMeta.Text += "  ·  " + FormatTime(TimeSpan.FromSeconds(dur));
        });
        p.Failed += () => Dispatcher.UIThread.Post(async () =>
        {
            if (!ReferenceEquals(player, p) || previewItem == null) return;
            StopMedia();
            await PreviewFallback(previewItem, previewLocalPath ?? previewItem.FullPath, previewCts?.Token ?? default,
                "This format can't be played here. Press Open to use your media player.");
        });
    }

    static IImage IconServiceThumb(string path, int px)
    {
        var ff = Converter.FindFfmpeg();
        if (ff == null) return null;
        foreach (var seek in new[] { "3", "0" })
        {
            var bytes = IconService.RunBytes(ff, new[] { "-v", "error", "-ss", seek, "-i", path, "-frames:v", "1", "-an", "-vf", $"scale='min({px},iw)':-2", "-f", "image2pipe", "-vcodec", "png", "-" }, 10000);
            if (bytes is { Length: > 100 }) return new Bitmap(new MemoryStream(bytes));
        }
        return null;
    }

    void PvPlay_Click(object sender, RoutedEventArgs e)
    {
        if (player == null) return;
        player.TogglePause();
        mediaPlaying = !mediaPlaying;
        PvPlayBtn.Content = mediaPlaying ? "" : "";
    }

    void UpdateMediaTime()
    {
        if (player == null || !PvMediaHost.IsVisible) return;
        var (pos, dur, paused, ended) = player.State;
        if (dur > 0 && Math.Abs(PvSeek.Maximum - dur) > 0.5) PvSeek.Maximum = dur;
        seekFromCode = true;
        PvSeek.Value = Math.Min(PvSeek.Maximum, pos);
        seekFromCode = false;
        PvTime.Text = $"{FormatTime(TimeSpan.FromSeconds(pos))} / {FormatTime(TimeSpan.FromSeconds(dur))}";
        mediaPlaying = !paused && !ended;
        PvPlayBtn.Content = mediaPlaying ? "" : "";
    }

    static string FormatTime(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    void StopMedia()
    {
        mediaTimer?.Stop();
        mediaPlaying = false;
        try { player?.Dispose(); } catch { }
        player = null;
        PvMedia.Content = null;
    }

    // ---------- PDF (poppler's pdftoppm) ----------
    async Task PreviewPdf(string path, CancellationToken ct)
    {
        var pdftoppm = IconService.Which("pdftoppm");
        if (pdftoppm == null) { await PreviewFallback(previewItem, path, ct, "Install poppler (pdftoppm) to preview PDFs here."); return; }
        int pages = await Task.Run(() => PdfPages(path), ct);
        if (ct.IsCancellationRequested) return;
        if (pages > 0) PvMeta.Text += $"  ·  {pages} page{(pages == 1 ? "" : "s")}";
        int show = pages > 0 ? Math.Min(pages, 20) : 20;
        if (pages > show) PvNote.Text = $"Showing the first {show} of {pages} pages";
        Show(PvPages);
        for (int i = 1; i <= show; i++)
        {
            if (ct.IsCancellationRequested) return;
            int page = i;
            var bytes = await Task.Run(() => IconService.RunBytes(pdftoppm, new[] { "-png", "-singlefile", "-f", page.ToString(), "-l", page.ToString(), "-scale-to-x", "1400", "-scale-to-y", "-1", path }, 20000), ct);
            if (ct.IsCancellationRequested) return;
            if (bytes == null || bytes.Length < 100) break;
            var bmp = new Bitmap(new MemoryStream(bytes));
            PvPagesPanel.Children.Add(new Border
            {
                Margin = new Thickness(0, 0, 0, 14), Background = Brushes.White, CornerRadius = new CornerRadius(4),
                MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Center, BoxShadow = BoxShadows.Parse("0 2 14 0 #59000000"),
                Child = new Image { Source = bmp, Stretch = Stretch.Uniform }
            });
        }
        if (PvPagesPanel.Children.Count == 0) await PreviewFallback(previewItem, path, ct, "Couldn't render this PDF.");
    }

    static int PdfPages(string path)
    {
        var info = IconService.Which("pdfinfo");
        if (info == null) return 0;
        var bytes = IconService.RunBytes(info, new[] { path }, 8000);
        if (bytes == null) return 0;
        foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n'))
            if (line.StartsWith("Pages:") && int.TryParse(line[6..].Trim(), out var n)) return n;
        return 0;
    }

    // ---------- ZIP contents ----------
    async Task PreviewZip(string path, CancellationToken ct)
    {
        var (rows, count, total) = await Task.Run(() =>
        {
            using var zip = ZipFile.OpenRead(path);
            var entries = zip.Entries.ToList();
            long t = entries.Sum(x => x.Length);
            var r = entries.OrderBy(x => x.FullName, StringComparer.CurrentCultureIgnoreCase).Take(1000)
                .Select(x => new ListRow(x.FullName, x.FullName.EndsWith('/') ? "" : Fmt.Size(x.Length),
                                         x.FullName.EndsWith('/') ? "" : Fmt.Size(x.CompressedLength),
                                         IconPack.IsActive ? IconPack.Render(IconPack.Resolve(IOPath.GetFileName(x.FullName.TrimEnd('/')), x.FullName.EndsWith('/')), 32)
                                                           : IconService.Themed(x.FullName.EndsWith('/') ? new[] { "folder" } : Mime.IconNames(Mime.ForName(IOPath.GetFileName(x.FullName))), 32)))
                .ToList();
            return (r, entries.Count, t);
        }, ct);
        if (ct.IsCancellationRequested) return;
        PvListExtraCol.Text = "Packed";
        PvListBox.ItemsSource = rows;
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
            if (f.IsRecycled) return new List<FileItem>();
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
        visible.Sort(new ItemComparer("name", false));
        int dirs = visible.Count(c => c.IsDir), files = visible.Count - dirs;
        long size = visible.Where(c => !c.IsDir).Sum(c => c.Size);
        PvMeta.Text += $"  ·  {dirs:N0} folders, {files:N0} files" + (size > 0 ? $"  ·  {Fmt.Size(size)} in files here" : "");
        if (visible.Count == 0) { ShowInfo(null, "This folder is empty."); return; }
        PvListExtraCol.Text = "Modified";
        PvListBox.ItemsSource = visible.Take(1000).Select(c => new ListRow(c.Name, c.SizeText, c.Modified.Year < 1700 ? "" : c.Modified.ToString("d"),
            IconPack.IsActive ? IconPack.Render(IconPack.Resolve(c.Name, c.IsDir), 32) : IconService.Themed(c.IsDir ? new[] { "folder" } : Mime.IconNames(Mime.ForName(c.Name)), 32))).ToList();
        if (visible.Count > 1000) PvNote.Text = "Showing the first 1,000 items";
        Show(PvList);
    }

    // ---------- anything else: a big icon + details ----------
    async Task PreviewFallback(FileItem f, string localPath, CancellationToken ct, string message = null)
    {
        if (f == null) return;
        IImage big = await Task.Run(() => IconPack.IsActive
            ? IconPack.Render(IconPack.Resolve(f.Name, f.IsDir), 256)
            : IconService.Themed(f.IsDir ? new[] { "folder" } : Mime.IconNames(Mime.ForName(f.Name)), 256), ct);
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

/// <summary>An empty native window inside the preview that mpv draws the video into (X11).</summary>
public sealed class MpvHost : NativeControlHost
{
    public event Action<long> HandleReady;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var h = base.CreateNativeControlCore(parent);
        var id = (long)h.Handle;
        Dispatcher.UIThread.Post(() => HandleReady?.Invoke(id), DispatcherPriority.Background);
        return h;
    }
}

/// <summary>Plays one file with mpv and talks to it over its JSON IPC socket (play / pause / seek / position).</summary>
public sealed class MpvPlayer : IDisposable
{
    readonly string exe;
    Process proc;
    Socket sock;
    readonly string socketPath;
    volatile bool disposed;
    double pos, dur;
    bool paused, ended;
    public event Action<int, int, double> Info;
    public event Action Failed;

    public MpvPlayer(string exe)
    {
        this.exe = exe;
        socketPath = IOPath.Combine(Xdg.Runtime, $"orbit-mpv-{Environment.ProcessId}-{Guid.NewGuid():N}.sock");
    }

    public (double pos, double dur, bool paused, bool ended) State => (pos, dur, paused, ended);

    public void Start(string file, long? wid, bool audio)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "--no-terminal", "--keep-open=yes", "--input-ipc-server=" + socketPath, "--osc=no", "--no-input-default-bindings",
                                  "--input-vo-keyboard=no", "--really-quiet", "--hwdec=auto-safe" })
            psi.ArgumentList.Add(a);
        if (wid != null)
        {
            psi.ArgumentList.Add("--wid=" + wid.Value);
            psi.ArgumentList.Add("--force-window=yes");
            // Orbit's window lives on X11 (XWayland); mpv must draw there too, or it opens a window of its own
            psi.Environment.Remove("WAYLAND_DISPLAY");
            psi.ArgumentList.Add("--gpu-context=x11egl,x11");
        }
        else { psi.ArgumentList.Add("--force-window=no"); psi.ArgumentList.Add("--vid=no"); }
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(file);
        try { proc = Process.Start(psi); }
        catch { Failed?.Invoke(); return; }
        proc!.StandardOutput.ReadToEndAsync();
        proc.StandardError.ReadToEndAsync();
        new Thread(Loop) { IsBackground = true, Name = "mpv-ipc" }.Start();
    }

    void Loop()
    {
        // wait for the socket
        for (int i = 0; i < 50 && !disposed; i++)
        {
            if (proc.HasExited) { if (!disposed) Failed?.Invoke(); return; }
            try
            {
                var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                s.Connect(new UnixDomainSocketEndPoint(socketPath));
                sock = s;
                break;
            }
            catch { Thread.Sleep(100); }
        }
        if (sock == null) return;
        foreach (var (prop, id) in new[] { ("time-pos", 1), ("duration", 2), ("pause", 3), ("eof-reached", 4), ("width", 5), ("height", 6) })
            Send($"{{\"command\":[\"observe_property\",{id},\"{prop}\"]}}");
        int w = 0, h = 0;
        bool infoSent = false;
        var buf = new byte[8192];
        var pending = new StringBuilder();
        try
        {
            while (!disposed)
            {
                int n = sock.Receive(buf);
                if (n <= 0) break;
                pending.Append(Encoding.UTF8.GetString(buf, 0, n));
                string all = pending.ToString();
                int nl;
                while ((nl = all.IndexOf('\n')) >= 0)
                {
                    var line = all[..nl];
                    all = all[(nl + 1)..];
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var r = doc.RootElement;
                        if (r.TryGetProperty("event", out var ev))
                        {
                            var evName = ev.GetString();
                            if (evName == "property-change" && r.TryGetProperty("name", out var nm) && r.TryGetProperty("data", out var data))
                            {
                                switch (nm.GetString())
                                {
                                    case "time-pos" when data.ValueKind == JsonValueKind.Number: pos = data.GetDouble(); break;
                                    case "duration" when data.ValueKind == JsonValueKind.Number: dur = data.GetDouble(); break;
                                    case "pause" when data.ValueKind is JsonValueKind.True or JsonValueKind.False: paused = data.GetBoolean(); break;
                                    case "eof-reached" when data.ValueKind is JsonValueKind.True or JsonValueKind.False: ended = data.GetBoolean(); break;
                                    case "width" when data.ValueKind == JsonValueKind.Number: w = data.GetInt32(); break;
                                    case "height" when data.ValueKind == JsonValueKind.Number: h = data.GetInt32(); break;
                                }
                                if (!infoSent && dur > 0) { infoSent = true; Info?.Invoke(w, h, dur); }
                            }
                            else if (evName == "end-file" && r.TryGetProperty("reason", out var reason) && reason.GetString() == "error") Failed?.Invoke();
                        }
                    }
                    catch { }
                }
                pending.Clear().Append(all);
            }
        }
        catch { }
    }

    void Send(string json)
    {
        try { sock?.Send(Encoding.UTF8.GetBytes(json + "\n")); } catch { }
    }

    public void TogglePause()
    {
        if (ended) { Send("{\"command\":[\"seek\",0,\"absolute\"]}"); Send("{\"command\":[\"set_property\",\"pause\",false]}"); ended = false; return; }
        Send("{\"command\":[\"cycle\",\"pause\"]}");
    }

    public void Seek(double seconds) =>
        Send($"{{\"command\":[\"seek\",{seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"absolute\"]}}");

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Send("{\"command\":[\"quit\"]}");
        try { sock?.Dispose(); } catch { }
        try { if (proc != null && !proc.WaitForExit(500)) proc.Kill(true); } catch { }
        try { File.Delete(socketPath); } catch { }
    }
}
