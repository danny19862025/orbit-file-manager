using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Orbit;

public partial class SettingsWindow : Window
{
    readonly MainWindow main;
    static AppSettings S => AppSettings.Current;

    public SettingsWindow(MainWindow owner, string page = null)
    {
        InitializeComponent();
        Owner = owner;
        main = owner;

        BuildThemeCards();
        BuildIconCards();
        ThumbCheck.IsChecked = S.PackThumbnails;
        TranslucentCheck.IsChecked = S.Translucent;
        HiddenCheck.IsChecked = S.ShowHidden;
        RestoreCheck.IsChecked = S.RestoreTabs;
        ViewGrid.IsChecked = S.ViewMode == "grid";
        ViewColumns.IsChecked = S.ViewMode == "columns";
        ViewTree.IsChecked = S.ViewMode == "tree";
        ViewDetails.IsChecked = S.ViewMode == "details";
        foreach (var rb in new[] { SortName, SortDate, SortSize, SortType }) rb.IsChecked = (string)rb.Tag == S.SortBy;
        DescCheck.IsChecked = S.SortDesc;
        BuildKnownHosts();
        BuildShortcutRows();
        FixedList.ItemsSource = MainWindow.FixedShortcuts;
        BuildSidebarRows();
        VersionText.Text = $"Version {AppInfo.VersionText}  ·  .NET {Environment.Version.ToString(2)}  ·  {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}";
        SetupAbout();

        switch (page)
        {
            case "general": NavGeneral.IsChecked = true; break;
            case "network": NavNetwork.IsChecked = true; break;
            case "shortcuts": NavShortcuts.IsChecked = true; break;
            case "icons": NavIcons.IsChecked = true; break;
            case "about": NavAbout.IsChecked = true; break;
        }
        Themes.Changed += OnThemeChanged;
        Closed += (_, _) => Themes.Changed -= OnThemeChanged;
        PreviewKeyDown += Settings_PreviewKeyDown;
    }


    void OnThemeChanged() => BuildThemeCards();

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageAppearance == null) return;
        PageAppearance.Visibility = NavAppearance.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageIcons.Visibility = NavIcons.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageGeneral.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageNetwork.Visibility = NavNetwork.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageShortcuts.Visibility = NavShortcuts.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavNetwork.IsChecked == true) BuildKnownHosts();
        PageScroll.ScrollToTop();
    }

    // ---------- Appearance ----------
    void BuildThemeCards()
    {
        ThemeCards.Children.Clear();
        foreach (var t in Themes.All)
        {
            bool selected = Themes.Current.Name == t.Name;
            Brush B(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

            // miniature of the app in this theme
            var preview = new Grid { Height = 84, ClipToBounds = true };
            preview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
            preview.ColumnDefinitions.Add(new ColumnDefinition());
            var side = new StackPanel { Background = B(t.Mantle) };
            side.Children.Add(new Border { Height = 6, Width = 22, CornerRadius = new CornerRadius(3), Background = B(t.Accent), Margin = new Thickness(0, 10, 0, 6) });
            for (int i = 0; i < 4; i++)
                side.Children.Add(new Border { Height = 4, Width = 22, CornerRadius = new CornerRadius(2), Background = B(t.Surface1), Margin = new Thickness(0, 0, 0, 6) });
            preview.Children.Add(side);
            var body = new Canvas { Background = B(t.Base) };
            Grid.SetColumn(body, 1);
            var tab = new Border { Width = 34, Height = 2, Background = B(t.Accent) };
            Canvas.SetLeft(tab, 8); Canvas.SetTop(tab, 12);
            body.Children.Add(tab);
            for (int i = 0; i < 3; i++)
            {
                var tile = new Border
                {
                    Width = 30, Height = 28, CornerRadius = new CornerRadius(5),
                    Background = B(i == 1 ? t.Surface0 : t.Base),
                    BorderBrush = i == 1 ? B(t.Lavender) : B(t.Surface0),
                    BorderThickness = new Thickness(1),
                    Child = new Border { Width = 14, Height = 10, CornerRadius = new CornerRadius(2), Background = B("#F9E2AF"), Margin = new Thickness(0, 4, 0, 0), VerticalAlignment = VerticalAlignment.Top }
                };
                Canvas.SetLeft(tile, 8 + i * 36); Canvas.SetTop(tile, 26);
                body.Children.Add(tile);
            }
            var line = new Border { Width = 70, Height = 4, CornerRadius = new CornerRadius(2), Background = B(t.Subtext), Opacity = 0.5 };
            Canvas.SetLeft(line, 8); Canvas.SetTop(line, 66);
            body.Children.Add(line);
            preview.Children.Add(body);

            var stack = new StackPanel();
            stack.Children.Add(new Border { CornerRadius = new CornerRadius(7), ClipToBounds = true, Child = preview, Margin = new Thickness(0, 0, 0, 8) });
            var nameRow = new DockPanel();
            if (selected)
            {
                var tick = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                tick.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                DockPanel.SetDock(tick, Dock.Right);
                nameRow.Children.Add(tick);
            }
            nameRow.Children.Add(new TextBlock { Text = t.Name, FontWeight = FontWeights.SemiBold });
            stack.Children.Add(nameRow);
            var desc = new TextBlock { Text = t.Description, FontSize = 12 };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            stack.Children.Add(desc);

            var card = new Border
            {
                Width = 166, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(8), CornerRadius = new CornerRadius(11),
                BorderThickness = new Thickness(selected ? 2 : 1), Cursor = Cursors.Hand, Child = stack, Tag = t.Name,
                ToolTip = selected ? "Current theme" : "Use " + t.Name
            };
            card.SetResourceReference(Border.BackgroundProperty, "PanelBg");
            card.SetResourceReference(Border.BorderBrushProperty, selected ? "AccentBrush" : "LineBrush");
            card.MouseLeftButtonUp += (s, _) => main.SetTheme((string)((FrameworkElement)s).Tag);
            ThemeCards.Children.Add(card);
        }
    }

    void Translucent_Click(object sender, RoutedEventArgs e) => main.SetTranslucent(TranslucentCheck.IsChecked == true);

    void BuildIconCards()
    {
        IconCards.Children.Clear();
        var samples = new[] { ("folder-base", true), ("folder-images", true), ("folder-src", true), ("javascript", false),
                              ("python", false), ("pdf", false), ("zip", false) };
        foreach (var (id, name, description) in IconPack.All)
        {
            bool selected = S.IconPack == id;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 10), Height = 30, ClipToBounds = true };
            if (id == IconPack.Material)
            {
                foreach (var (icon, _) in samples)
                    row.Children.Add(new Image { Source = IconPack.Render(icon), Width = 24, Height = 24, Margin = new Thickness(0, 0, 6, 0) });
            }
            else
            {
                foreach (var glyph in new[] { "", "", "", "", "", "", "", "" }.Take(7))
                {
                    var t = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 20, Margin = new Thickness(0, 0, 8, 0), Foreground = new SolidColorBrush(Color.FromRgb(0xF9, 0xD8, 0x6B)) };
                    row.Children.Add(t);
                }
            }
            var stack = new StackPanel();
            stack.Children.Add(row);
            var nameRow = new DockPanel();
            if (selected)
            {
                var tick = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                tick.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                DockPanel.SetDock(tick, Dock.Right);
                nameRow.Children.Add(tick);
            }
            nameRow.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
            stack.Children.Add(nameRow);
            var desc = new TextBlock { Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            stack.Children.Add(desc);

            var card = new Border
            {
                Width = 270, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(12), CornerRadius = new CornerRadius(11),
                BorderThickness = new Thickness(selected ? 2 : 1), Cursor = Cursors.Hand, Child = stack, Tag = id
            };
            card.SetResourceReference(Border.BackgroundProperty, "PanelBg");
            card.SetResourceReference(Border.BorderBrushProperty, selected ? "AccentBrush" : "LineBrush");
            card.MouseLeftButtonUp += (s, _) =>
            {
                main.SetIconPack((string)((FrameworkElement)s).Tag);
                BuildIconCards();
            };
            IconCards.Children.Add(card);
        }
    }

    void Thumb_Click(object sender, RoutedEventArgs e) => main.SetPackThumbnails(ThumbCheck.IsChecked == true);

    // ---------- General ----------
    void Hidden_Click(object sender, RoutedEventArgs e) => main.SetShowHidden(HiddenCheck.IsChecked == true);

    void Restore_Click(object sender, RoutedEventArgs e)
    {
        S.RestoreTabs = RestoreCheck.IsChecked == true;
        main.SaveSettingsSoon();
    }

    void View_Click(object sender, RoutedEventArgs e) =>
        main.SetViewMode(ViewDetails.IsChecked == true ? "details" : ViewColumns.IsChecked == true ? "columns" : ViewTree.IsChecked == true ? "tree" : "grid");

    void Sort_Click(object sender, RoutedEventArgs e) => main.SetSort((string)((FrameworkElement)sender).Tag, S.SortDesc);

    void Desc_Click(object sender, RoutedEventArgs e) => main.SetSort(S.SortBy, DescCheck.IsChecked == true);

    // ---------- Network ----------
    void BuildKnownHosts()
    {
        KnownHostsPanel.Children.Clear();
        if (S.KnownHosts.Count == 0)
        {
            var none = new TextBlock { Text = "None yet — they appear here after your first SFTP connection.", FontSize = 12.5 };
            none.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            KnownHostsPanel.Children.Add(none);
            return;
        }
        foreach (var kv in S.KnownHosts.OrderBy(k => k.Key))
        {
            var host = kv.Key;
            var row = new Border { Style = (Style)FindResource("Row") };
            var dock = new DockPanel();
            var forget = new Button { Content = "Forget", Style = (Style)FindResource("DlgButton"), MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            forget.Click += (_, _) =>
            {
                S.KnownHosts.Remove(host);
                main.SaveSettingsSoon();
                BuildKnownHosts();
            };
            DockPanel.SetDock(forget, Dock.Right);
            dock.Children.Add(forget);
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = host.Trim('[').Replace("]:", " : ") });
            var fp = new TextBlock { Text = kv.Value, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
            fp.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            info.Children.Add(fp);
            dock.Children.Add(info);
            row.Child = dock;
            KnownHostsPanel.Children.Add(row);
        }
    }

    void Connect_Click(object sender, RoutedEventArgs e)
    {
        Close();
        main.OpenConnectDialog();
    }

    void Redetect_Click(object sender, RoutedEventArgs e) => main.RedetectNetwork();

    // ---------- About ----------
    string updateDownloadUrl;

    void SetupAbout()
    {
        // planet orbiting the logo, plus a soft breathing glow
        var spin = new System.Windows.Media.Animation.DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(7)))
        {
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
        };
        OrbitSpin.BeginAnimation(RotateTransform.AngleProperty, spin);
        var glow = new System.Windows.Media.Animation.DoubleAnimation(0.10, 0.32, new Duration(TimeSpan.FromSeconds(2.4)))
        {
            AutoReverse = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            EasingFunction = new System.Windows.Media.Animation.SineEase()
        };
        LogoGlow.BeginAnimation(OpacityProperty, glow);

        bool repo = AppInfo.HasRepo, coffee = AppInfo.CoffeeUrl.Length > 0;
        RepoBtn.Visibility = IssueBtn.Visibility = repo ? Visibility.Visible : Visibility.Collapsed;
        CoffeeBtn.Visibility = coffee ? Visibility.Visible : Visibility.Collapsed;
        LinksHint.Visibility = repo && coffee ? Visibility.Collapsed : Visibility.Visible;
        LinksHint.Text = !repo && !coffee ? "Repository and coffee links aren't set yet — fill them in at the top of AppInfo.cs."
                       : !repo ? "Add your GitHub repository link at the top of AppInfo.cs to show the repo and bug-report buttons."
                       : "Add your Buy Me a Coffee link at the top of AppInfo.cs to show the coffee button.";
        if (!repo) UpdateText.Text = "Needs your GitHub repository link (AppInfo.cs).";
    }

    async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (updateDownloadUrl != null) { AppInfo.OpenUrl(updateDownloadUrl); return; }
        UpdateBtn.IsEnabled = false;
        UpdateBtn.Content = "Checking…";
        var r = await AppInfo.CheckForUpdateAsync();
        UpdateBtn.IsEnabled = true;
        UpdateTitle.Text = r.UpdateAvailable ? "Update available" : "Check whether a newer Orbit is available";
        UpdateText.Text = r.Message;
        if (r.UpdateAvailable && r.DownloadUrl != null)
        {
            updateDownloadUrl = r.DownloadUrl;
            UpdateBtn.Content = "Download " + r.LatestVersion;
        }
        else UpdateBtn.Content = "Check again";
    }

    void Repo_Click(object sender, RoutedEventArgs e) => AppInfo.OpenUrl(AppInfo.RepoUrl);
    void Issue_Click(object sender, RoutedEventArgs e) => AppInfo.OpenUrl(AppInfo.RepoUrl.TrimEnd('/') + "/issues/new");
    void Coffee_Click(object sender, RoutedEventArgs e) => AppInfo.OpenUrl(AppInfo.CoffeeUrl);

    void OpenData_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Orbit");
        Directory.CreateDirectory(dir);
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); } catch { }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- General › Sidebar ----------
    void BuildSidebarRows()
    {
        SidebarRows.Children.Clear();
        foreach (var (id, label) in MainWindow.SidebarSections)
        {
            var row = new Border { Style = (Style)FindResource("Row"), Padding = new Thickness(14, 9, 14, 9), Margin = new Thickness(0, 0, 0, 6) };
            var dock = new DockPanel();
            var check = new CheckBox { Style = (Style)FindResource("Check"), IsChecked = main.IsSectionVisible(id), VerticalAlignment = VerticalAlignment.Center };
            var sectionId = id;
            check.Click += (_, _) => main.SetSectionVisible(sectionId, check.IsChecked == true);
            DockPanel.SetDock(check, Dock.Right);
            dock.Children.Add(check);
            dock.Children.Add(new TextBlock { Text = "Show " + label, VerticalAlignment = VerticalAlignment.Center });
            row.Child = dock;
            SidebarRows.Children.Add(row);
        }
    }

    // ---------- Shortcuts (click, then press keys) ----------
    string capturingId;
    Button capturingButton;

    void BuildShortcutRows()
    {
        ShortcutRows.Children.Clear();
        string group = null;
        foreach (var s in MainWindow.Shortcuts)
        {
            if (s.Group != group)
            {
                group = s.Group;
                var h = new TextBlock { Text = group, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 16, 0, 8) };
                h.SetResourceReference(TextBlock.ForegroundProperty, "SectionBrush");
                ShortcutRows.Children.Add(h);
            }
            var gesture = MainWindow.GestureFor(s.Id);
            bool custom = S.Keymap.ContainsKey(s.Id);
            var btn = new Button
            {
                Style = (Style)FindResource("DlgButton"),
                MinWidth = 170,
                Padding = new Thickness(12, 5, 12, 5),
                Content = new TextBlock { Text = MainWindow.PrettyGesture(gesture), FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5 },
                Tag = s.Id,
                ToolTip = custom ? $"Changed from {MainWindow.PrettyGesture(s.Default)} — click to change" : "Click, then press the new keys",
                Focusable = false
            };
            if (custom) btn.SetResourceReference(Button.BorderBrushProperty, "AccentBrush");
            btn.Click += Shortcut_Click;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(btn, Dock.Right);
            row.Children.Add(btn);
            row.Children.Add(new TextBlock { Text = s.Label, VerticalAlignment = VerticalAlignment.Center });
            ShortcutRows.Children.Add(row);
        }
    }

    void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        CancelCapture();
        capturingButton = (Button)sender;
        capturingId = (string)capturingButton.Tag;
        capturingButton.Content = new TextBlock { Text = "Press keys…", FontStyle = FontStyles.Italic };
        capturingButton.SetResourceReference(Button.BorderBrushProperty, "AccentBrush");
        KeyNote.Visibility = Visibility.Collapsed;
        Focus();
    }

    void CancelCapture()
    {
        if (capturingId == null) return;
        capturingId = null;
        capturingButton = null;
        BuildShortcutRows();
    }

    void Settings_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (capturingId == null)
        {
            if (e.Key == Key.Escape) Close();
            return;
        }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var m = Keyboard.Modifiers;
        if (key == Key.Escape && m == ModifierKeys.None) { CancelCapture(); return; }
        string gesture;
        if ((key == Key.Back || key == Key.Delete) && m == ModifierKeys.None && capturingId != "delete") gesture = "";
        else
        {
            gesture = MainWindow.GestureFrom(key, m);
            if (gesture == null) return; // only a modifier so far — keep waiting
        }
        var label = MainWindow.Shortcuts.First(s => s.Id == capturingId).Label;
        var displaced = MainWindow.SetGesture(capturingId, gesture);
        main.SaveSettingsSoon();
        capturingId = null;
        capturingButton = null;
        BuildShortcutRows();
        KeyNote.Text = gesture.Length == 0
            ? $"“{label}” no longer has a shortcut."
            : $"“{label}” is now {MainWindow.PrettyGesture(gesture)}." + (displaced != null ? $" It was used by “{displaced}”, which now has none." : "");
        KeyNote.Visibility = Visibility.Visible;
    }

    void ResetKeys_Click(object sender, RoutedEventArgs e)
    {
        capturingId = null;
        MainWindow.ResetShortcuts();
        main.SaveSettingsSoon();
        BuildShortcutRows();
        KeyNote.Text = "All shortcuts are back to their defaults.";
        KeyNote.Visibility = Visibility.Visible;
    }
}
