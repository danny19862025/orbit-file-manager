using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Orbit;

/// <summary>"Convert anything": pictures, video and audio to common formats, saved next to the originals.</summary>
public sealed class ConvertWindow : OrbitWindow
{
    readonly MainWindow main;
    readonly List<string> files;
    readonly string kind;
    readonly string ffmpeg;
    Converter.Format chosen;
    int maxSide;
    CancellationTokenSource estimateCts;
    readonly Dictionary<string, TextBlock> savingLabels = new();
    readonly Dictionary<string, long> estimates = new();
    bool running;

    readonly WrapPanel formatCards = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) };
    readonly Slider qualitySlider;
    readonly TextBlock qualityText, savingText, statusText;
    readonly Border savingChip;
    readonly DockPanel qualityRow;
    readonly Button convertBtn;

    public ConvertWindow(MainWindow owner, List<string> paths)
    {
        main = owner;
        files = paths;
        kind = Converter.KindOf(paths[0]);
        ffmpeg = Converter.FindFfmpeg();
        Title = "Convert";
        Width = 720;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !running) Close(); };

        var root = new StackPanel();
        root.Children.Add(Ui.TitleBar(this, "", "Convert", out _, out _, () => { if (!running) Close(); }));
        var body = new StackPanel { Margin = new Thickness(32, 4, 32, 26) };
        body.Children.Add(new TextBlock { Text = "Convert anything", FontSize = 24, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        body.Children.Add(new TextBlock
        {
            Text = kind switch { "image" => "Pictures, converted on this computer.", "video" => "Video, converted on this computer with ffmpeg.", _ => "Audio, converted on this computer with ffmpeg." },
            FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 18)
        }.Brush(TextBlock.ForegroundProperty, "MutedBrush"));

        // source summary
        long total = paths.Sum(p => { try { return new FileInfo(p).Length; } catch { return 0L; } });
        var src = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new Image { Width = 44, Height = 44, Margin = new Thickness(0, 0, 14, 0) };
        var item = FileItem.TryFromPath(paths[0]);
        if (item != null) icon.Bind(Image.SourceProperty, new Avalonia.Data.Binding(nameof(FileItem.LargeIcon)) { Source = item });
        src.Children.Add(icon);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} {kind} files", FontWeight = FontWeight.SemiBold, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 });
        names.Children.Add(new TextBlock { Text = Fmt.Size(total) }.Brush(TextBlock.ForegroundProperty, "MutedBrush"));
        src.Children.Add(names);
        savingText = new TextBlock { FontWeight = FontWeight.SemiBold }.Brush(TextBlock.ForegroundProperty, "AccentBrush");
        savingChip = new Border { Margin = new Thickness(18, 0, 0, 0), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 4), VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Child = savingText }
            .Brush(Border.BackgroundProperty, "SelectedBg");
        src.Children.Add(savingChip);
        var srcCard = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(18, 14), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 360, Child = src }
            .Brush(Border.BackgroundProperty, "PanelBg").Brush(Border.BorderBrushProperty, "LineBrush");
        body.Children.Add(srcCard);
        body.Children.Add(formatCards);

        // options
        var imageOptions = new StackPanel { Margin = new Thickness(0, 14, 0, 0), IsVisible = kind == "image" };
        qualityRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var ql = new TextBlock { Text = "Quality", Width = 90, VerticalAlignment = VerticalAlignment.Center }.Brush(TextBlock.ForegroundProperty, "MutedBrush");
        DockPanel.SetDock(ql, Dock.Left);
        qualityRow.Children.Add(ql);
        qualityText = new TextBlock { Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = "82" };
        DockPanel.SetDock(qualityText, Dock.Right);
        qualityRow.Children.Add(qualityText);
        qualitySlider = new Slider { Minimum = 40, Maximum = 100, Value = 82, SmallChange = 1, LargeChange = 5, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
        qualitySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            qualityText.Text = $"{(int)qualitySlider.Value}";
            if (IsLoaded) EstimateAsync();
        };
        qualityRow.Children.Add(qualitySlider);
        imageOptions.Children.Add(qualityRow);
        var sizeRow = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var sl = new TextBlock { Text = "Size", Width = 90, VerticalAlignment = VerticalAlignment.Center }.Brush(TextBlock.ForegroundProperty, "MutedBrush");
        DockPanel.SetDock(sl, Dock.Left);
        sizeRow.Children.Add(sl);
        var sizes = new[] { Ui.Seg("Original", "size", true, "0"), Ui.Seg("Max 3840 px", "size", false, "3840"), Ui.Seg("Max 1920 px", "size", false, "1920"), Ui.Seg("Max 1280 px", "size", false, "1280") };
        foreach (var r in sizes) r.Click += (s, _) => { maxSide = int.Parse((string)((Control)s!).Tag); EstimateAsync(); };
        sizeRow.Children.Add(Ui.SegBar(sizes));
        imageOptions.Children.Add(sizeRow);
        body.Children.Add(imageOptions);

        if (kind != "image" && ffmpeg == null)
        {
            var note = new DockPanel();
            var get = Ui.Dlg("Get ffmpeg");
            get.VerticalAlignment = VerticalAlignment.Center;
            get.Click += (_, _) => AppInfo.OpenUrl("https://ffmpeg.org/download.html#build-linux");
            DockPanel.SetDock(get, Dock.Right);
            note.Children.Add(get);
            note.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
                Text = "Video and audio conversion uses ffmpeg, a free tool that isn't installed yet. Install it with your package manager (for example: sudo pacman -S ffmpeg or sudo apt install ffmpeg), then open Convert again." }
                .Brush(TextBlock.ForegroundProperty, "MutedBrush"));
            body.Children.Add(new Border { Margin = new Thickness(0, 16, 0, 0), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10), BorderThickness = new Thickness(1), Child = note }
                .Brush(Border.BackgroundProperty, "PanelBg").Brush(Border.BorderBrushProperty, "LineBrush"));
        }
        statusText = new TextBlock { Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center }.Brush(TextBlock.ForegroundProperty, "MutedBrush");
        body.Children.Add(statusText);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0), Spacing = 10 };
        var close = Ui.Dlg("Close");
        close.Click += (_, _) => { if (!running) Close(); };
        convertBtn = Ui.Dlg("Convert", true);
        convertBtn.Click += (_, _) => Convert_Click();
        buttons.Children.Add(close);
        buttons.Children.Add(convertBtn);
        body.Children.Add(buttons);
        root.Children.Add(body);
        SetBody(root);

        var srcExt = Path.GetExtension(paths[0]).ToLowerInvariant();
        var formats = Converter.FormatsFor(kind);
        BuildCards(formats);
        chosen = formats.FirstOrDefault(f => f.Ext != srcExt && !(srcExt == ".jpeg" && f.Id == "jpg")) ?? formats.First();
        Select(chosen);
        Opened += (_, _) => EstimateAsync();
    }

    // ---------- format cards ----------
    void BuildCards(Converter.Format[] formats)
    {
        formatCards.Children.Clear();
        savingLabels.Clear();
        foreach (var f in formats)
        {
            var saving = new TextBlock { FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) }.Brush(TextBlock.ForegroundProperty, "MutedBrush");
            savingLabels[f.Id] = saving;
            var note = new TextBlock { Text = f.Note, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 0) }.Brush(TextBlock.ForegroundProperty, "MutedBrush");
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = f.Label, FontSize = 17, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(saving);
            stack.Children.Add(note);
            var card = new Border
            {
                Width = 118, Height = 108, Margin = new Thickness(5), Padding = new Thickness(8, 12, 8, 8), CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1), Cursor = new Cursor(StandardCursorType.Hand), Child = stack, Tag = f
            };
            card.PointerReleased += (s, e) => { if (!running && e.InitialPressMouseButton == MouseButton.Left) Select((Converter.Format)((Control)s!).Tag); };
            formatCards.Children.Add(card);
        }
    }

    void Select(Converter.Format f)
    {
        chosen = f;
        foreach (var card in formatCards.Children.OfType<Border>())
        {
            bool on = ReferenceEquals(card.Tag, f);
            if (on)
            {
                var accent = Themes.ColorOf("AccentBrush");
                card.Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.FromArgb(0x18, accent.R, accent.G, accent.B), 0), new GradientStop(Color.FromArgb(0x44, accent.R, accent.G, accent.B), 1) }
                };
                card.Brush(Border.BorderBrushProperty, "AccentBrush");
                card.BorderThickness = new Thickness(2);
            }
            else
            {
                card.Brush(Border.BackgroundProperty, "PanelBg").Brush(Border.BorderBrushProperty, "LineBrush");
                card.BorderThickness = new Thickness(1);
            }
        }
        qualityRow.IsVisible = f.Id == "jpg";
        bool possible = kind == "image" || ffmpeg != null;
        convertBtn.IsEnabled = possible;
        convertBtn.Content = files.Count == 1 ? $"Convert to {f.Label}" : $"Convert {files.Count} files to {f.Label}";
        UpdateSavingChip();
    }

    // ---------- live size estimates (images): encode the first file in memory ----------
    async void EstimateAsync()
    {
        if (kind != "image") { foreach (var l in savingLabels.Values) l.Text = ""; return; }
        estimateCts?.Cancel();
        var cts = new CancellationTokenSource();
        estimateCts = cts;
        var src = files[0];
        long before;
        try { before = new FileInfo(src).Length; } catch { return; }
        int q = (int)qualitySlider.Value, side = maxSide;
        foreach (var f in Converter.ImageFormats)
        {
            savingLabels[f.Id].Text = "…";
            try
            {
                var size = await Task.Run(() => (long)Converter.EncodeImage(src, f, q, side).Length, cts.Token);
                if (cts.IsCancellationRequested) return;
                estimates[f.Id] = size;
                long pct = before > 0 ? (size - before) * 100 / before : 0;
                savingLabels[f.Id].Text = pct == 0 ? "±0%" : (pct > 0 ? $"+{pct}%" : $"−{-pct}%");
                savingLabels[f.Id].Brush(TextBlock.ForegroundProperty, pct < 0 ? "AccentBrush" : "MutedBrush");
            }
            catch (OperationCanceledException) { return; }
            catch { savingLabels[f.Id].Text = f.Id is "tiff" or "bmp" or "gif" && IconService.Which("magick") == null && Converter.FindFfmpeg() == null ? "needs ImageMagick" : "can't read"; }
        }
        UpdateSavingChip();
    }

    void UpdateSavingChip()
    {
        if (kind == "image" && estimates.TryGetValue(chosen.Id, out var size) && files.Count == 1)
        {
            long before = new FileInfo(files[0]).Length;
            savingText.Text = $"{Fmt.Size(before)} → {Fmt.Size(size)}";
            savingChip.IsVisible = true;
        }
        else savingChip.IsVisible = false;
    }

    // ---------- converting ----------
    async void Convert_Click()
    {
        if (running) return;
        running = true;
        convertBtn.IsEnabled = false;
        var fmt = chosen;
        int q = (int)qualitySlider.Value, side = maxSide;
        long before = 0, after = 0;
        int ok = 0;
        var errors = new List<string>();
        string lastOut = null;
        for (int i = 0; i < files.Count; i++)
        {
            var src = files[i];
            statusText.Text = $"Converting {i + 1} of {files.Count}: {Path.GetFileName(src)}…";
            var dest = Converter.OutputPath(src, fmt.Ext);
            try
            {
                if (kind == "image")
                {
                    var bytes = await Task.Run(() => Converter.EncodeImage(src, fmt, q, side));
                    await File.WriteAllBytesAsync(dest, bytes);
                }
                else
                {
                    var err = await Converter.RunFfmpegAsync(ffmpeg, src, dest, fmt);
                    if (err != null) { errors.Add($"{Path.GetFileName(src)}: {err}"); continue; }
                }
                before += new FileInfo(src).Length;
                after += new FileInfo(dest).Length;
                ok++;
                lastOut = dest;
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(src)}: {ex.Message}"); }
        }
        running = false;
        convertBtn.IsEnabled = true;
        var pct = before > 0 ? (after - before) * 100 / before : 0;
        statusText.Text = (ok > 0 ? $"Converted {ok} file{(ok == 1 ? "" : "s")} · {Fmt.Size(before)} → {Fmt.Size(after)} ({(pct <= 0 ? "−" + (-pct) : "+" + pct)}%)" : "Nothing was converted.")
                          + (errors.Count > 0 ? "\n" + string.Join("\n", errors.Take(3)) : "");
        if (lastOut != null) main.ShowInOrbit(lastOut);
    }
}
