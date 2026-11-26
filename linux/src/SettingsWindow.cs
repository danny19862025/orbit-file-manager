using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Orbit;

/// <summary>Settings: Appearance, Icons, General, Network, Shortcuts, About. Every change applies at once.</summary>
public sealed class SettingsWindow : OrbitWindow
{
    readonly MainWindow main;
    static AppSettings S => AppSettings.Current;

    readonly RadioButton navAppearance, navIcons, navGeneral, navNetwork, navShortcuts, navAbout;
    readonly StackPanel pageAppearance = new(), pageIcons = new(), pageGeneral = new(), pageNetwork = new(), pageShortcuts = new(), pageAbout = new();
    readonly ScrollViewer pageScroll;
    WrapPanel themeCards, iconCards;
    CheckBox translucentCheck, thumbCheck;
    Slider transparencySlider;
    TextBlock transparencyText, keyNote, updateTitle, updateText;
    Border transparencyRow;
    StackPanel knownHostsPanel, shortcutRows, sidebarRows;
    Button updateBtn;
    string updateDownloadUrl;
    bool ready;

    public SettingsWindow(MainWindow owner, string page = null)
    {
        main = owner;
        Title = "Settings";
        Width = 780;
        Height = 560;
        CanResize = false;

        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*") };
        // ---------- navigation ----------
        var navBorder = new Border { BorderThickness = new Thickness(0, 0, 1, 0) }.Brush(Border.BackgroundProperty, "SidebarBg").Brush(Border.BorderBrushProperty, "LineBrush");
        var nav = new StackPanel { Margin = new Thickness(10, 0, 10, 10) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Height = 48, Margin = new Thickness(6, 0, 0, 6) };
        head.Children.Add(Ui.Glyph("", 16, "AccentBrush", new Thickness(0, 0, 10, 0)));
        head.Children.Add(new TextBlock { Text = "Settings", FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        DragArea(head);
        nav.Children.Add(head);
        RadioButton Nav(string glyph, string text)
        {
            var r = new RadioButton { Classes = { "nav" }, Tag = glyph, Content = text, GroupName = "nav" };
            r.IsCheckedChanged += (_, _) => { if (r.IsChecked == true) ShowPage(); };
            nav.Children.Add(r);
            return r;
        }
        navAppearance = Nav("", "Appearance");
        navIcons = Nav("", "Icons");
        navGeneral = Nav("", "General");
        navNetwork = Nav("", "Network");
        navShortcuts = Nav("", "Shortcuts");
        navAbout = Nav("", "About");
        navBorder.Child = nav;
        root.Children.Add(navBorder);

        // ---------- pages ----------
        var right = new Grid();
        Grid.SetColumn(right, 1);
        var topBar = new Border { Height = 48, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent };
        DragArea(topBar);
        right.Children.Add(topBar);
        var close = Ui.Icon("", 11);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = new Thickness(0, 8, 8, 0);
        close.Click += (_, _) => Close();
        close.ZIndex = 2;
        right.Children.Add(close);
        var pages = new Grid { Margin = new Thickness(28, 0, 28, 24) };
        foreach (var p in new[] { pageAppearance, pageIcons, pageGeneral, pageNetwork, pageShortcuts, pageAbout }) pages.Children.Add(p);
        pageScroll = new ScrollViewer { Margin = new Thickness(0, 48, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = pages };
        right.Children.Add(pageScroll);
        root.Children.Add(right);
        SetBody(root);

        BuildAppearance();
        BuildIcons();
        BuildGeneral();
        BuildNetwork();
        BuildShortcuts();
        BuildAbout();

        switch (page)
        {
            case "general": navGeneral.IsChecked = true; break;
            case "network": navNetwork.IsChecked = true; break;
            case "shortcuts": navShortcuts.IsChecked = true; break;
            case "icons": navIcons.IsChecked = true; break;
            case "about": navAbout.IsChecked = true; break;
            default: navAppearance.IsChecked = true; break;
        }
        Themes.Changed += OnThemeChanged;
        Closed += (_, _) => Themes.Changed -= OnThemeChanged;
        AddHandler(KeyDownEvent, Settings_PreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ready = true;
    }

    void OnThemeChanged() => Dispatcher.UIThread.Post(BuildThemeCards);

    void ShowPage()
    {
        pageAppearance.IsVisible = navAppearance?.IsChecked == true;
        pageIcons.IsVisible = navIcons?.IsChecked == true;
        pageGeneral.IsVisible = navGeneral?.IsChecked == true;
        pageNetwork.IsVisible = navNetwork?.IsChecked == true;
        pageShortcuts.IsVisible = navShortcuts?.IsChecked == true;
        pageAbout.IsVisible = navAbout?.IsChecked == true;
        if (navNetwork?.IsChecked == true) BuildKnownHosts();
        if (pageScroll != null) pageScroll.Offset = new Vector(0, 0);
    }

    // =====================================================================
    // Appearance
    // =====================================================================
    void BuildAppearance()
    {
        var p = pageAppearance;
        p.Children.Add(Ui.H1("Appearance"));
        p.Children.Add(Ui.Sub("Changes apply instantly and are remembered."));
        p.Children.Add(Ui.H2("Theme"));
        themeCards = new WrapPanel();
        p.Children.Add(themeCards);
        BuildThemeCards();
        p.Children.Add(Ui.H2("Window"));
        translucentCheck = Ui.Check(null, S.Translucent);
        translucentCheck.Click += (_, _) =>
        {
            main.SetTranslucent(translucentCheck.IsChecked == true);
            transparencyRow.Opacity = S.Translucent ? 1 : 0.6;
        };
        p.Children.Add(Ui.SettingRow("Translucent window (blur)", "Lets your desktop show through, blurred, where the compositor supports it (KDE Plasma, Hyprland, picom…).", translucentCheck));

        var tr = new StackPanel();
        var top = new DockPanel();
        transparencyText = new TextBlock { Text = $"{S.Transparency}%", FontWeight = FontWeight.SemiBold }.Brush(TextBlock.ForegroundProperty, "AccentBrush");
        DockPanel.SetDock(transparencyText, Dock.Right);
        top.Children.Add(transparencyText);
        top.Children.Add(new TextBlock { Text = "Transparency" });
        tr.Children.Add(top);
        transparencySlider = new Slider { Minimum = 5, Maximum = 90, Value = S.Transparency, SmallChange = 1, LargeChange = 5, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 6, 0, 0) };
        transparencySlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || !ready) return;
            int pct = (int)Math.Round(transparencySlider.Value);
            transparencyText.Text = $"{pct}%";
            main.SetTransparency(pct); // moving the slider turns the glass on
            translucentCheck.IsChecked = true;
            transparencyRow.Opacity = 1;
        };
        tr.Children.Add(transparencySlider);
        var ends = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        var glass = Ui.Sub("Glass");
        DockPanel.SetDock(glass, Dock.Right);
        ends.Children.Add(glass);
        ends.Children.Add(Ui.Sub("Subtle"));
        tr.Children.Add(ends);
        tr.Children.Add(Ui.Sub("How much of your desktop shows through. The sidebar is a little clearer than the file list so names stay easy to read.", new Thickness(0, 6, 0, 0)));
        transparencyRow = Ui.Row(tr);
        transparencyRow.Opacity = S.Translucent ? 1 : 0.6;
        p.Children.Add(transparencyRow);
    }

    void BuildThemeCards()
    {
        themeCards.Children.Clear();
        var entries = new System.Collections.Generic.List<object>();
        foreach (var (title, items) in Themes.Groups) { entries.Add(title); entries.AddRange(items); }
        entries.Add("Dynamic");
        entries.Add(WallpaperTheme.Build());
        foreach (var entry in entries)
        {
            if (entry is string heading)
            {
                var h = new TextBlock { Text = heading, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, themeCards.Children.Count == 0 ? 0 : 6, 0, 8), Width = 520 }.Brush(TextBlock.ForegroundProperty, "SectionBrush");
                themeCards.Children.Add(h);
                continue;
            }
            var t = (ThemeDef)entry;
            bool selected = Themes.Current.Name == t.Name;
            IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

            // miniature of the app in this theme
            var preview = new Grid { Height = 84, ClipToBounds = true, ColumnDefinitions = new ColumnDefinitions("38,*") };
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
                var tick = Ui.Glyph("", 12, "AccentBrush");
                DockPanel.SetDock(tick, Dock.Right);
                nameRow.Children.Add(tick);
            }
            nameRow.Children.Add(new TextBlock { Text = t.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(nameRow);
            stack.Children.Add(Ui.Sub(t.Description));

            var card = new Border
            {
                Width = 166, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(8), CornerRadius = new CornerRadius(11),
                BorderThickness = new Thickness(selected ? 2 : 1), Cursor = new Cursor(StandardCursorType.Hand), Child = stack, Tag = t.Name
            };
            ToolTip.SetTip(card, t.Name == Themes.WallpaperName ? "Follows your desktop: " + WallpaperTheme.Source + "\nUpdates by itself when pywal or your desktop's accent colour changes." : selected ? "Current theme" : "Use " + t.Name);
            card.Brush(Border.BackgroundProperty, "PanelBg").Brush(Border.BorderBrushProperty, selected ? "AccentBrush" : "LineBrush");
            card.PointerReleased += (s, e) => { if (e.InitialPressMouseButton == MouseButton.Left) main.SetTheme((string)((Control)s!).Tag); };
            themeCards.Children.Add(card);
        }
    }

    // =====================================================================
    // Icons
    // =====================================================================
    void BuildIcons()
    {
        var p = pageIcons;
        p.Children.Add(Ui.H1("Icons"));
        p.Children.Add(Ui.Sub("Choose how files and folders look. Packs can also come from any freedesktop icon theme folder."));
        p.Children.Add(Ui.H2("Icon pack"));
        iconCards = new WrapPanel();
        p.Children.Add(iconCards);
        BuildIconCards();
        p.Children.Add(Ui.H2("Previews"));
        thumbCheck = Ui.Check(null, S.PackThumbnails);
        thumbCheck.Click += (_, _) => main.SetPackThumbnails(thumbCheck.IsChecked == true);
        p.Children.Add(Ui.SettingRow("Keep photo and video thumbnails", "With an icon pack on, pictures and videos still show a preview in Grid view.", thumbCheck));
    }

    void BuildIconCards()
    {
        iconCards.Children.Clear();
        var samples = new[] { "folder-base", "folder-images", "folder-download", "javascript", "python", "pdf", "zip" };
        foreach (var pack in IconPack.All)
        {
            var id = pack.Id;
            bool selected = S.IconPack == id;
            bool customUnset = id == IconPack.Custom && string.IsNullOrEmpty(S.CustomIconTheme);
            string problem = id == IconPack.System || customUnset ? null : IconPack.ErrorFor(id);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 10), Height = 30, ClipToBounds = true };
            if (id == IconPack.System)
            {
                foreach (var names in new[] { new[] { "folder" }, new[] { "folder-pictures", "folder" }, new[] { "folder-download", "folder" },
                                              Mime.IconNames("application/javascript").ToArray(), Mime.IconNames("text/x-python").ToArray(),
                                              Mime.IconNames("application/pdf").ToArray(), Mime.IconNames("application/zip").ToArray() })
                {
                    var img = IconService.Themed(names, 48);
                    if (img != null) row.Children.Add(new Image { Source = img, Width = 24, Height = 24, Margin = new Thickness(0, 0, 5, 0) });
                }
            }
            else if (!customUnset && problem == null)
            {
                foreach (var icon in samples)
                {
                    var src = IconPack.RenderFrom(id, icon, 48) ?? IconPack.RenderFrom(id, icon.StartsWith("folder") ? "folder-base" : "document", 48);
                    row.Children.Add(new Image { Source = src, Width = 24, Height = 24, Margin = new Thickness(0, 0, 5, 0) });
                }
            }
            else
            {
                row.Children.Add(new TextBlock { Text = "", Classes = { "icon" }, FontSize = 20, Margin = new Thickness(0, 0, 8, 0), Foreground = new SolidColorBrush(Color.FromRgb(0xF9, 0xD8, 0x6B)) });
            }
            var stack = new StackPanel();
            stack.Children.Add(row);
            var nameRow = new DockPanel();
            if (selected)
            {
                var tick = Ui.Glyph("", 12, "AccentBrush");
                DockPanel.SetDock(tick, Dock.Right);
                nameRow.Children.Add(tick);
            }
            var title = id == IconPack.Custom && !customUnset ? "Freedesktop: " + System.IO.Path.GetFileName(S.CustomIconTheme.TrimEnd('/'))
                      : id == IconPack.System ? $"System ({SystemIcons.ThemeName ?? SystemIcons.ConfiguredTheme()})" : pack.Name;
            nameRow.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(nameRow);
            var text = problem != null ? (id == IconPack.Custom ? problem : "Not installed — its icon file is missing from the icons folder.")
                     : id == IconPack.Custom && !customUnset ? "Click to choose a different theme folder" : pack.Description;
            stack.Children.Add(Ui.Sub(text));

            var card = new Border
            {
                Width = 240, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(12), CornerRadius = new CornerRadius(11),
                BorderThickness = new Thickness(selected ? 2 : 1), Cursor = new Cursor(StandardCursorType.Hand), Child = stack, Tag = id,
                Opacity = problem != null && id != IconPack.Custom ? 0.55 : 1
            };
            card.Brush(Border.BackgroundProperty, "PanelBg").Brush(Border.BorderBrushProperty, selected ? "AccentBrush" : "LineBrush");
            card.PointerReleased += async (s, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left) return;
                var tag = (string)((Control)s!).Tag;
                if (tag == IconPack.Custom) await ChooseIconTheme();
                else if (tag == IconPack.System || IconPack.ErrorFor(tag) == null) main.SetIconPack(tag);
                BuildIconCards();
            };
            iconCards.Children.Add(card);
        }
    }

    async System.Threading.Tasks.Task ChooseIconTheme()
    {
        var start = string.IsNullOrEmpty(S.CustomIconTheme) ? System.IO.Path.Combine(Xdg.Data, "icons") : S.CustomIconTheme;
        var opts = new FolderPickerOpenOptions { Title = "Choose an icon theme folder (the one with index.theme)", AllowMultiple = false };
        try { if (Directory.Exists(start)) opts.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(start); } catch { }
        var r = await StorageProvider.OpenFolderPickerAsync(opts);
        var dir = r.Count > 0 ? r[0].TryGetLocalPath() : null;
        if (dir == null) return;
        Cursor = new Cursor(StandardCursorType.Wait);
        var err = main.SetCustomIconTheme(dir);
        Cursor = null;
        if (err != null) await Dialog.Info(this, "Icon theme", err);
    }

    // =====================================================================
    // General
    // =====================================================================
    void BuildGeneral()
    {
        var p = pageGeneral;
        p.Children.Add(Ui.H1("General"));
        p.Children.Add(Ui.H2("Browsing"));
        var hidden = Ui.Check(null, S.ShowHidden);
        hidden.Click += (_, _) => main.SetShowHidden(hidden.IsChecked == true);
        p.Children.Add(Ui.SettingRow("Show hidden items", "Shortcut: Ctrl+H", hidden));
        var restore = Ui.Check(null, S.RestoreTabs);
        restore.Click += (_, _) => { S.RestoreTabs = restore.IsChecked == true; main.SaveSettingsSoon(); };
        p.Children.Add(Ui.SettingRow("Reopen my tabs at startup", "Otherwise Orbit starts in your Home folder.", restore));

        p.Children.Add(Ui.H2("Sidebar"));
        p.Children.Add(Ui.Sub("Choose which sections appear in the sidebar. Places is always shown.", new Thickness(0, -4, 0, 10)));
        sidebarRows = new StackPanel();
        foreach (var (id, label) in MainWindow.SidebarSections)
        {
            var check = Ui.Check(null, main.IsSectionVisible(id));
            var sectionId = id;
            check.Click += (_, _) => main.SetSectionVisible(sectionId, check.IsChecked == true);
            var dock = new DockPanel();
            DockPanel.SetDock(check, Dock.Right);
            dock.Children.Add(check);
            dock.Children.Add(new TextBlock { Text = "Show " + label, VerticalAlignment = VerticalAlignment.Center });
            sidebarRows.Children.Add(Ui.Row(dock, new Thickness(14, 9), new Thickness(0, 0, 0, 6)));
        }
        p.Children.Add(sidebarRows);

        p.Children.Add(Ui.H2("View"));
        var vg = Ui.Seg("Grid", "view", S.ViewMode == "grid", "grid");
        var vd = Ui.Seg("Details", "view", S.ViewMode == "details", "details");
        var vc = Ui.Seg("Columns", "view", S.ViewMode == "columns", "columns");
        var vt = Ui.Seg("Tree", "view", S.ViewMode == "tree", "tree");
        foreach (var r in new[] { vg, vd, vc, vt }) r.Click += (s, _) => main.SetViewMode((string)((Control)s!).Tag);
        p.Children.Add(Ui.SettingRow("Layout", null, SegIn(vg, vd, vc, vt)));
        var sn = Ui.Seg("Name", "sort", S.SortBy == "name", "name");
        var sd = Ui.Seg("Date", "sort", S.SortBy == "date", "date");
        var ss = Ui.Seg("Size", "sort", S.SortBy == "size", "size");
        var st = Ui.Seg("Type", "sort", S.SortBy == "type", "type");
        foreach (var r in new[] { sn, sd, ss, st }) r.Click += (s, _) => main.SetSort((string)((Control)s!).Tag, S.SortDesc);
        p.Children.Add(Ui.SettingRow("Sort by", null, SegIn(sn, sd, ss, st)));
        var desc = Ui.Check(null, S.SortDesc);
        desc.Click += (_, _) => main.SetSort(S.SortBy, desc.IsChecked == true);
        p.Children.Add(Ui.SettingRow("Sort descending", null, desc));
    }

    static Border SegIn(params RadioButton[] b)
    {
        var bar = Ui.SegBar(b);
        bar.Brush(Border.BackgroundProperty, "ContentBg");
        return bar;
    }

    // =====================================================================
    // Network
    // =====================================================================
    void BuildNetwork()
    {
        var p = pageNetwork;
        p.Children.Add(Ui.H1("Network"));
        p.Children.Add(Ui.Sub("Saved servers live in the sidebar's Network section — right-click one to edit, disconnect or remove it."));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), Spacing = 10 };
        var connect = Ui.Dlg("Connect to server…");
        connect.Click += (_, _) => { Close(); main.OpenConnectDialog(); };
        var redetect = Ui.Dlg("Re-detect cloud folders");
        redetect.Click += (_, _) => main.RedetectNetwork();
        buttons.Children.Add(connect);
        buttons.Children.Add(redetect);
        p.Children.Add(buttons);
        p.Children.Add(Ui.H2("Trusted SSH servers"));
        p.Children.Add(Ui.Sub("Orbit remembers each server's identity the first time you connect and warns you if it changes. Forget a server if it was reinstalled.", new Thickness(0, 0, 0, 10)));
        knownHostsPanel = new StackPanel();
        p.Children.Add(knownHostsPanel);
        BuildKnownHosts();
    }

    void BuildKnownHosts()
    {
        if (knownHostsPanel == null) return;
        knownHostsPanel.Children.Clear();
        if (S.KnownHosts.Count == 0)
        {
            knownHostsPanel.Children.Add(Ui.Sub("None yet — they appear here after your first SFTP connection."));
            return;
        }
        foreach (var kv in S.KnownHosts.OrderBy(k => k.Key))
        {
            var host = kv.Key;
            var dock = new DockPanel();
            var forget = Ui.Dlg("Forget");
            forget.MinWidth = 0;
            forget.Click += (_, _) =>
            {
                S.KnownHosts.Remove(host);
                main.SaveSettingsSoon();
                BuildKnownHosts();
            };
            forget.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(forget, Dock.Right);
            dock.Children.Add(forget);
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = host.Trim('[').Replace("]:", " : ") });
            var fp = new TextBlock { Text = kv.Value, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis }.Brush(TextBlock.ForegroundProperty, "MutedBrush");
            fp.FontFamily = (FontFamily)Application.Current!.FindResource("MonoFont")!;
            info.Children.Add(fp);
            dock.Children.Add(info);
            knownHostsPanel.Children.Add(Ui.Row(dock));
        }
    }

    // =====================================================================
    // Shortcuts (click, then press keys)
    // =====================================================================
    string capturingId;

    void BuildShortcuts()
    {
        var p = pageShortcuts;
        var head = new DockPanel();
        var reset = Ui.Dlg("Reset all");
        reset.VerticalAlignment = VerticalAlignment.Center;
        reset.Click += (_, _) =>
        {
            capturingId = null;
            MainWindow.ResetShortcuts();
            main.SaveSettingsSoon();
            BuildShortcutRows();
            keyNote.Text = "All shortcuts are back to their defaults.";
            keyNote.IsVisible = true;
        };
        DockPanel.SetDock(reset, Dock.Right);
        head.Children.Add(reset);
        head.Children.Add(Ui.H1("Keyboard shortcuts"));
        p.Children.Add(head);
        p.Children.Add(Ui.Sub("Click a shortcut, then press the new keys. Esc cancels, Backspace removes it."));
        keyNote = Ui.Sub("", new Thickness(0, 6, 0, 0)).Brush(TextBlock.ForegroundProperty, "AccentBrush");
        keyNote.IsVisible = false;
        p.Children.Add(keyNote);
        shortcutRows = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        p.Children.Add(shortcutRows);
        BuildShortcutRows();
        p.Children.Add(Ui.H2("Fixed"));
        p.Children.Add(Ui.Sub("Built in, so they always work the same way.", new Thickness(0, -4, 0, 8)));
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;
        foreach (var (label, keys) in MainWindow.FixedShortcuts)
        {
            var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var box = new Border { MinWidth = 170, Height = 30, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(8, 0),
                Child = new TextBlock { Text = keys, FontFamily = mono, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }.Brush(TextBlock.ForegroundProperty, "MutedBrush") };
            box.Brush(Border.BackgroundProperty, "ContentBg").Brush(Border.BorderBrushProperty, "LineBrush");
            DockPanel.SetDock(box, Dock.Right);
            dock.Children.Add(box);
            dock.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            p.Children.Add(dock);
        }
    }

    void BuildShortcutRows()
    {
        shortcutRows.Children.Clear();
        string group = null;
        var mono = (FontFamily)Application.Current!.FindResource("MonoFont")!;
        foreach (var s in MainWindow.Shortcuts)
        {
            if (s.Group != group)
            {
                group = s.Group;
                shortcutRows.Children.Add(new TextBlock { Text = group, FontWeight = FontWeight.SemiBold, FontSize = 13, Margin = new Thickness(0, 16, 0, 8) }.Brush(TextBlock.ForegroundProperty, "SectionBrush"));
            }
            var gesture = MainWindow.GestureFor(s.Id);
            bool custom = AppSettings.Current.Keymap.ContainsKey(s.Id);
            bool capturing = capturingId == s.Id;
            var btn = new Button
            {
                Classes = { "dlg" }, MinWidth = 170, Padding = new Thickness(12, 5), Tag = s.Id, Focusable = false,
                Content = capturing ? new TextBlock { Text = "Press keys…", FontStyle = FontStyle.Italic }
                                    : new TextBlock { Text = MainWindow.PrettyGesture(gesture), FontFamily = mono, FontSize = 12.5 }
            };
            ToolTip.SetTip(btn, custom ? $"Changed from {MainWindow.PrettyGesture(s.Default)} — click to change" : "Click, then press the new keys");
            if (custom || capturing) btn.Brush(Button.BorderBrushProperty, "AccentBrush");
            btn.Click += (sender, _) =>
            {
                capturingId = (string)((Control)sender!).Tag;
                keyNote.IsVisible = false;
                BuildShortcutRows();
                Focus();
            };
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(btn, Dock.Right);
            row.Children.Add(btn);
            row.Children.Add(new TextBlock { Text = s.Label, VerticalAlignment = VerticalAlignment.Center });
            shortcutRows.Children.Add(row);
        }
    }

    void Settings_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (capturingId == null)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            return;
        }
        e.Handled = true;
        var key = e.Key;
        var m = e.KeyModifiers;
        if (key == Key.Escape && m == KeyModifiers.None) { capturingId = null; BuildShortcutRows(); return; }
        string gesture;
        if ((key == Key.Back || key == Key.Delete) && m == KeyModifiers.None && capturingId != "delete") gesture = "";
        else
        {
            gesture = MainWindow.GestureFrom(key, m);
            if (gesture == null) return; // only a modifier so far — keep waiting
        }
        var label = MainWindow.Shortcuts.First(s => s.Id == capturingId).Label;
        var displaced = MainWindow.SetGesture(capturingId, gesture);
        main.SaveSettingsSoon();
        capturingId = null;
        BuildShortcutRows();
        keyNote.Text = gesture.Length == 0
            ? $"“{label}” no longer has a shortcut."
            : $"“{label}” is now {MainWindow.PrettyGesture(gesture)}." + (displaced != null ? $" It was used by “{displaced}”, which now has none." : "");
        keyNote.IsVisible = true;
    }

    // =====================================================================
    // About
    // =====================================================================
    void BuildAbout()
    {
        var p = pageAbout;
        // animated logo: a planet orbiting the Orbit mark
        var logo = new Grid { Width = 104, Height = 104, Margin = new Thickness(0, 0, 24, 0), VerticalAlignment = VerticalAlignment.Center };
        var glow = new Ellipse { Width = 70, Height = 70, Opacity = 0.18, Effect = new BlurEffect { Radius = 22 } }.Brush(Shape.FillProperty, "AccentBrush");
        logo.Children.Add(glow);
        logo.Children.Add(new Ellipse { Width = 96, Height = 96, StrokeThickness = 1.5, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 2, 3 } }.Brush(Shape.StrokeProperty, "LineBrush"));
        var mark = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(13), Child = new Ellipse { Width = 16, Height = 16 }.Brush(Shape.FillProperty, "PanelBg") };
        logo.Children.Add(mark.Brush(Border.BackgroundProperty, "AccentBrush"));
        var orbit = new Canvas { Width = 104, Height = 104, RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative) };
        var spin = new RotateTransform();
        orbit.RenderTransform = spin;
        var moon = new Ellipse { Width = 14, Height = 14, StrokeThickness = 2 }.Brush(Shape.FillProperty, "SelBorderBrush").Brush(Shape.StrokeProperty, "PanelBg");
        Canvas.SetLeft(moon, 45); Canvas.SetTop(moon, -1);
        orbit.Children.Add(moon);
        logo.Children.Add(orbit);
        var clock = Stopwatch.StartNew();
        var anim = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
        {
            if (!pageAbout.IsVisible) return;
            double s = clock.Elapsed.TotalSeconds;
            spin.Angle = s / 7 * 360 % 360;
            glow.Opacity = 0.10 + 0.22 * (0.5 - 0.5 * Math.Cos(s / 2.4 * Math.PI));
        });
        Closed += (_, _) => anim.Stop();

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = "Orbit", FontSize = 26, FontWeight = FontWeight.SemiBold });
        text.Children.Add(Ui.Sub($"Version {AppInfo.VersionText}  ·  .NET {Environment.Version.ToString(2)}  ·  {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}  ·  Linux"));
        text.Children.Add(Ui.Sub("A fast, modern file manager for Linux.", new Thickness(0, 2, 0, 0)));
        var hero = new DockPanel();
        hero.Children.Add(logo);
        hero.Children.Add(text);
        p.Children.Add(Ui.Row(hero, new Thickness(24, 22), new Thickness(0, 4, 0, 8)));

        p.Children.Add(Ui.H2("Updates"));
        updateBtn = Ui.Dlg("Check for updates");
        updateTitle = new TextBlock { Text = "Check whether a newer Orbit is available" };
        updateText = Ui.Sub("Looks at the latest release on GitHub.");
        var udock = new DockPanel();
        updateBtn.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(updateBtn, Dock.Right);
        udock.Children.Add(updateBtn);
        var ut = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        ut.Children.Add(updateTitle);
        ut.Children.Add(updateText);
        udock.Children.Add(ut);
        p.Children.Add(Ui.Row(udock));
        updateBtn.Click += async (_, _) =>
        {
            if (updateDownloadUrl != null) { AppInfo.OpenUrl(updateDownloadUrl); return; }
            updateBtn.IsEnabled = false;
            updateBtn.Content = "Checking…";
            var r = await AppInfo.CheckForUpdateAsync();
            updateBtn.IsEnabled = true;
            updateTitle.Text = r.UpdateAvailable ? "Update available" : "Check whether a newer Orbit is available";
            updateText.Text = r.Message;
            if (r.UpdateAvailable && r.DownloadUrl != null)
            {
                updateDownloadUrl = r.DownloadUrl;
                updateBtn.Content = "Download " + r.LatestVersion;
            }
            else updateBtn.Content = "Check again";
        };

        p.Children.Add(Ui.H2("Project"));
        var links = new WrapPanel();
        Button Link(string glyph, string label, Action click)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            if (glyph != null) sp.Children.Add(Ui.Glyph(glyph, 13, null, new Thickness(0, 0, 8, 0)));
            sp.Children.Add(new TextBlock { Text = label });
            var b = Ui.Dlg(null);
            b.Content = sp;
            b.Margin = new Thickness(0, 0, 10, 10);
            b.Click += (_, _) => click();
            links.Children.Add(b);
            return b;
        }
        if (AppInfo.HasRepo)
        {
            Link("", "GitHub repository", () => AppInfo.OpenUrl(AppInfo.RepoUrl));
            Link("", "Report a bug", () => AppInfo.OpenUrl(AppInfo.RepoUrl.TrimEnd('/') + "/issues/new"));
        }
        if (AppInfo.CoffeeUrl.Length > 0)
        {
            var coffee = Link(null, "☕  Buy me a coffee", () => AppInfo.OpenUrl(AppInfo.CoffeeUrl));
            coffee.Background = new SolidColorBrush(Color.Parse("#FFDD00"));
            coffee.BorderBrush = new SolidColorBrush(Color.Parse("#FFDD00"));
            coffee.Foreground = new SolidColorBrush(Color.Parse("#1B1B1B"));
            coffee.FontWeight = FontWeight.SemiBold;
        }
        Link(null, "Open settings folder", () =>
        {
            Directory.CreateDirectory(AppSettings.Dir);
            try { Shell.OpenInFileManager(AppSettings.Dir); } catch { }
        });
        p.Children.Add(links);

        p.Children.Add(Ui.H2("Credits"));
        var credits = Ui.Sub("SSH.NET — SFTP support (MIT)\n" +
                             "SharpCompress — archives (MIT)\n" +
                             "Avalonia and Svg.Skia — interface and SVG rendering (MIT)\n" +
                             "Material Icon Theme by Philipp Kief and contributors — file icons (MIT)\n" +
                             "Fluent UI System Icons by Microsoft — interface icons (MIT)\n" +
                             "Catppuccin, Tokyo Night, Rosé Pine and Nord — colour palettes (MIT)");
        credits.LineHeight = 20;
        p.Children.Add(credits);
    }
}
