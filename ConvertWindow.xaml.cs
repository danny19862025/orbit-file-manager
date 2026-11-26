using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Orbit;

public partial class ConvertWindow : Window
{
    readonly MainWindow main;
    readonly List<string> files;
    readonly string kind;
    readonly string ffmpeg;
    Converter.Format chosen;
    int maxSide;
    CancellationTokenSource estimateCts;
    readonly Dictionary<string, TextBlock> savingLabels = new();
    bool running;

    public ConvertWindow(MainWindow owner, List<string> paths)
    {
        InitializeComponent();
        Owner = owner;
        main = owner;
        files = paths;
        kind = Converter.KindOf(paths[0]);
        ffmpeg = kind == "image" ? null : Converter.FindFfmpeg();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !running) Close(); };

        Subtitle.Text = kind switch
        {
            "image" => "Images, converted locally with Windows' own codecs.",
            "video" => "Video, converted locally with ffmpeg.",
            _ => "Audio, converted locally with ffmpeg."
        };
        long total = paths.Sum(p => { try { return new FileInfo(p).Length; } catch { return 0L; } });
        SrcName.Text = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} {kind} files";
        SrcInfo.Text = Fmt.Size(total);
        var item = FileItem.TryFromPath(paths[0]);
        if (item != null) SrcIcon.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(FileItem.LargeIcon)) { Source = item });

        ImageOptions.Visibility = kind == "image" ? Visibility.Visible : Visibility.Collapsed;
        FfmpegNote.Visibility = kind != "image" && ffmpeg == null ? Visibility.Visible : Visibility.Collapsed;
        QualityText.Text = $"{(int)QualitySlider.Value}";

        var srcExt = Path.GetExtension(paths[0]).ToLowerInvariant();
        var formats = Converter.FormatsFor(kind);
        BuildCards(formats);
        chosen = formats.FirstOrDefault(f => f.Ext != srcExt && !(srcExt == ".jpeg" && f.Id == "jpg")) ?? formats.First();
        Select(chosen);
        Loaded += (_, _) => EstimateAsync();
    }

    // ---------- format cards ----------
    void BuildCards(Converter.Format[] formats)
    {
        FormatCards.Children.Clear();
        savingLabels.Clear();
        foreach (var f in formats)
        {
            var saving = new TextBlock { FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
            saving.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            savingLabels[f.Id] = saving;
            var note = new TextBlock { Text = f.Note, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            note.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = f.Label, FontSize = 17, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(saving);
            stack.Children.Add(note);
            var card = new Border
            {
                Width = 118, Height = 108, Margin = new Thickness(5), Padding = new Thickness(8, 12, 8, 8), CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Child = stack, Tag = f
            };
            card.SetResourceReference(Border.BackgroundProperty, "PanelBg");
            card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            card.MouseLeftButtonUp += (s, _) => { if (!running) Select((Converter.Format)((FrameworkElement)s).Tag); };
            FormatCards.Children.Add(card);
        }
    }

    void Select(Converter.Format f)
    {
        chosen = f;
        foreach (Border card in FormatCards.Children)
        {
            bool on = ReferenceEquals(card.Tag, f);
            if (on)
            {
                var accent = ((SolidColorBrush)FindResource("AccentBrush")).Color;
                card.Background = new LinearGradientBrush(Color.FromArgb(0x18, accent.R, accent.G, accent.B), Color.FromArgb(0x44, accent.R, accent.G, accent.B), 90);
                card.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                card.BorderThickness = new Thickness(2);
            }
            else
            {
                card.SetResourceReference(Border.BackgroundProperty, "PanelBg");
                card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
                card.BorderThickness = new Thickness(1);
            }
        }
        QualityRow.Visibility = f.Id == "jpg" ? Visibility.Visible : Visibility.Collapsed;
        bool possible = kind == "image" || ffmpeg != null;
        ConvertBtn.IsEnabled = possible;
        ConvertBtn.Content = files.Count == 1 ? $"Convert to {f.Label}" : $"Convert {files.Count} files to {f.Label}";
        UpdateSavingChip();
    }

    void Quality_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (QualityText == null) return;
        QualityText.Text = $"{(int)e.NewValue}";
        if (IsLoaded) EstimateAsync();
    }

    void Size_Click(object sender, RoutedEventArgs e)
    {
        maxSide = ((FrameworkElement)sender).Tag is string s && int.TryParse(s, out var v) ? v : 0;
        EstimateAsync();
    }

    // ---------- live size estimates (images): encode the first file in memory ----------
    readonly Dictionary<string, long> estimates = new();

    async void EstimateAsync()
    {
        if (kind != "image") { foreach (var l in savingLabels.Values) l.Text = ""; return; }
        estimateCts?.Cancel();
        var cts = new CancellationTokenSource();
        estimateCts = cts;
        var src = files[0];
        long before;
        try { before = new FileInfo(src).Length; } catch { return; }
        int q = (int)QualitySlider.Value, side = maxSide;
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
                if (pct < 0) savingLabels[f.Id].SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                else savingLabels[f.Id].SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            }
            catch (OperationCanceledException) { return; }
            catch { savingLabels[f.Id].Text = "can't read"; }
        }
        UpdateSavingChip();
    }

    void UpdateSavingChip()
    {
        if (kind == "image" && estimates.TryGetValue(chosen.Id, out var size) && files.Count == 1)
        {
            long before = new FileInfo(files[0]).Length;
            SavingText.Text = $"{Fmt.Size(before)} → {Fmt.Size(size)}";
            SavingChip.Visibility = Visibility.Visible;
        }
        else SavingChip.Visibility = Visibility.Collapsed;
    }

    // ---------- converting ----------
    async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (running) return;
        running = true;
        ConvertBtn.IsEnabled = false;
        var fmt = chosen;
        int q = (int)QualitySlider.Value, side = maxSide;
        long before = 0, after = 0;
        int ok = 0;
        var errors = new List<string>();
        string lastOut = null;
        for (int i = 0; i < files.Count; i++)
        {
            var src = files[i];
            StatusText.Text = $"Converting {i + 1} of {files.Count}: {Path.GetFileName(src)}…";
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
        ConvertBtn.IsEnabled = true;
        var pct = before > 0 ? (after - before) * 100 / before : 0;
        StatusText.Text = (ok > 0 ? $"Converted {ok} file{(ok == 1 ? "" : "s")} · {Fmt.Size(before)} → {Fmt.Size(after)} ({(pct <= 0 ? "−" + (-pct) : "+" + pct)}%)" : "Nothing was converted.")
                          + (errors.Count > 0 ? "\n" + string.Join("\n", errors.Take(3)) : "");
        if (lastOut != null) main.ShowInOrbit(lastOut);
    }

    void GetFfmpeg_Click(object sender, RoutedEventArgs e) => AppInfo.OpenUrl("https://ffmpeg.org/download.html");

    void Close_Click(object sender, RoutedEventArgs e) { if (!running) Close(); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource == this) DragMove();
    }
}
