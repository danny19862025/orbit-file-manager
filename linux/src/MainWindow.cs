using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IOPath = System.IO.Path;

namespace Orbit;

/// <summary>A list that can be replaced in one go (one Reset instead of thousands of Add notifications).</summary>
public sealed class BulkCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var i in items) Items.Add(i);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public partial class MainWindow : Window
{
    // ---------- bound collections ----------
    public ObservableCollection<TabState> Tabs { get; } = new();
    public ObservableCollection<SideItem> Places { get; } = new();
    public ObservableCollection<SideItem> Bookmarks { get; } = new();
    public ObservableCollection<SideItem> TagItems { get; } = new();
    public ObservableCollection<SideItem> Drives { get; } = new();
    public ObservableCollection<SideItem> Network { get; } = new();
    public ObservableCollection<SideItem> Recent { get; } = new();
    public ObservableCollection<Crumb> Crumbs { get; } = new();
    public ObservableCollection<FileItem> DropItems { get; } = new();

    static readonly string Home = Xdg.Home;
    static AppSettings S => AppSettings.Current;
    public static WindowIcon AppIcon { get; } = LoadIcon();

    static WindowIcon LoadIcon()
    {
        try { return new WindowIcon(AssetLoader.Open(new Uri("avares://orbit/Assets/orbit.png"))); } catch { return null; }
    }

    TabState current, startTab;
    List<FileItem> items = new();
    readonly BulkCollection<FileItem> view = new();
    Dictionary<string, FileItem> index = new(StringComparer.Ordinal);
    int loadVersion;
    bool loading;
    string loadError;
    string searchText = "";
    bool suppressSearch;
    FileSystemWatcher watcher;
    readonly ConcurrentQueue<(int ver, string path, bool overflow)> fsEvents = new();
    readonly DispatcherTimer fsTimer, driveTimer, toastTimer, saveTimer;
    readonly HashSet<string> cutPaths = new(StringComparer.Ordinal);
    bool drivesBusy;
    int anchorIndex = -1;
    /// <summary>Modifier keys as last seen by a key or pointer event (Avalonia has no global keyboard state).</summary>
    KeyModifiers mods;

    // drag state
    Point? dragStart;
    FileItem dragItem, pendingSingle, dropTarget;
    PointerPressedEventArgs dragPress;

    ColumnDefinition SidebarCol => RootGrid.ColumnDefinitions[0];
    ListBox ActiveList => S.ViewMode switch { "details" => DetailsList, "tree" => TreeList, "columns" => ColumnsList, _ => GridList };
    ToggleButton[] SectionToggles => new[] { TglPlaces, TglBookmarks, TglTags, TglDrives, TglNetwork, TglRecent };
    ListBox[] AllLists => new[] { GridList, DetailsList, TreeList, ColumnsList };

    string pendingSelect;
    bool pendingPreview;

    public MainWindow() : this(null) { }

    public MainWindow(string startPath, string previewPath = null)
    {
        InitializeComponent();
        DataContext = this;
        Icon = AppIcon;

        fsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => FlushFsEvents());
        driveTimer = new DispatcherTimer(TimeSpan.FromSeconds(8), DispatcherPriority.Background, (_, _) => RefreshDrives());
        FileOps.Failed += msg => Dispatcher.UIThread.Post(() => Toast(msg));
        springTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        springTimer.Tick += SpringTimer_Tick;
        toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); ToastBorder.IsVisible = false; };
        saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); CaptureState(); S.Save(); };

        RestorePlacement();
        BuildPlaces();
        RebuildNetwork();
        RebuildBookmarks();
        RebuildTags();
        RebuildRecent();
        LoadDropzone();
        RefreshDrives();
        HiddenToggle.IsChecked = S.ShowHidden;
        ApplySidebar();
        ApplySectionStates();
        ApplySidebarSections();
        BuildDetailsHeader();
        ApplyViewVisibility();
        WireEvents();

        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(startPath))
        {
            var p = startPath.Trim('"');
            if (p is "trash:" or "trash:/" or "trash:///") p = RecycleBin.Path;
            if (RecycleBin.Is(p)) { paths.Add(p); p = null; }
            else if (!Archives.IsArc(p) && !Sftp.IsSftp(p) && !NetMount.IsNetUri(p)) { try { p = IOPath.GetFullPath(p); } catch { } }
            if (p == null) { /* Trash */ }
            else if (previewPath == null && File.Exists(p) && Archives.IsArchiveFile(p))
                p = Archives.Make(p, ""); // "Open with Orbit" on an archive opens it like a folder
            if (p == null) { }
            else if (Archives.IsArc(p))
            {
                if (File.Exists(Archives.Parse(p).archive)) paths.Add(p);
            }
            else if (Sftp.IsSftp(p)) paths.Add(p);
            else if (NetMount.IsNetUri(p)) pendingNet = p;
            else
            {
                bool selectIt = File.Exists(p) || (previewPath != null && Directory.Exists(p) && IOPath.GetDirectoryName(p) != null);
                if (selectIt)
                {
                    pendingSelect = p;
                    pendingPreview = previewPath != null;
                    p = IOPath.GetDirectoryName(p);
                }
                if (Directory.Exists(p)) paths.Add(p);
            }
        }
        bool fromArgs = paths.Count > 0;
        if (!fromArgs && S.RestoreTabs)
            foreach (var p in S.OpenTabs)
                if (IsTag(p) || SafeDirExists(p)) paths.Add(p);
        if (paths.Count == 0) paths.Add(Home);
        foreach (var p in paths) Tabs.Add(new TabState { Path = Norm(p) });
        startTab = Tabs[fromArgs ? 0 : Math.Clamp(S.ActiveTab, 0, Tabs.Count - 1)];

        TabArea.SizeChanged += (_, _) => FitTabs();
        SizeChanged += (_, _) => FitTabs();
        Opened += OnOpened;
        PrepareIntro();
        Closing += OnClosing;
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) UpdateMaxState(); };
        ScalingChanged += (_, _) => { IconService.Scale = RenderScaling; };
        ApplyBackdrop();
    }

    string pendingNet;

    // =====================================================================
    // Window lifecycle
    // =====================================================================
    void OnOpened(object sender, EventArgs e)
    {
        PlayIntro();
        IconService.Scale = RenderScaling;
        SwitchTo(startTab);
        RestoreSplit();
        UpdateMaxState();
        if (PendingSpacePath != null) { var sp = PendingSpacePath; PendingSpacePath = null; Dispatcher.UIThread.Post(() => OpenSpace(sp), DispatcherPriority.Loaded); }
        if (PendingConvertPath != null) { var cp = PendingConvertPath; PendingConvertPath = null; Dispatcher.UIThread.Post(() => OpenConvertPaths(new List<string> { cp }), DispatcherPriority.Loaded); }
        WallpaperTheme.Watch(() => Dispatcher.UIThread.Post(() => { if (S.Theme == Themes.WallpaperName) ApplyTheme(S.Theme, S.Translucent); }));
        if (PendingLock is { } pl) { PendingLock = null; Dispatcher.UIThread.Post(() => OpenLock(pl.Path, pl.Locking), DispatcherPriority.Loaded); }
        if (pendingNet != null) { var u = pendingNet; pendingNet = null; Dispatcher.UIThread.Post(() => Navigate(u), DispatcherPriority.Loaded); }
        Dispatcher.UIThread.Post(() => SearchService.Start(), DispatcherPriority.ApplicationIdle);
        StartClipboardWatch();
        FitTabs();
        RunTestScript();
    }

    void OnClosing(object sender, WindowClosingEventArgs e)
    {
        CaptureState();
        S.Save();
        StopWatcher();
        StopMedia();
        Sftp.DisconnectAll();
        SearchService.Stop();
        CloudMount.UnmountAll();
    }

    // =====================================================================
    // Themes and window chrome
    // =====================================================================
    void ApplyTheme(string name, bool translucent)
    {
        S.Theme = Themes.Find(name).Name;
        S.Translucent = translucent;
        Themes.Apply(S.Theme, S.Translucent ? S.Transparency : 0);
        if (IconPack.Tinted && IsLoaded) RefreshIcons();
        ApplyBackdrop();
        UpdateStar();
        BuildDetailsHeader();
        SaveSoon();
    }

    /// <summary>Translucent mode: ask the compositor to blur what's behind the window (KWin, Hyprland, picom…).</summary>
    void ApplyBackdrop()
    {
        if (S.Translucent)
        {
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None };
            Background = Brushes.Transparent;
        }
        else
        {
            TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
            this.Bind(BackgroundProperty, this.GetResourceObservable("SidebarBg"));
        }
    }

    void UpdateMaxState()
    {
        bool max = WindowState == WindowState.Maximized || WindowState == WindowState.FullScreen;
        Root.BorderThickness = new Thickness(max ? 0 : 1);
        Grips.IsVisible = !max;
        MaxBtn.Content = max ? "" : "";
    }

    void RestorePlacement()
    {
        Width = S.Width;
        Height = S.Height;
        if (S.Left is double l && S.Top is double t && l > -50 && t > -20)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)l, (int)t);
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (S.Maximized) WindowState = WindowState.Maximized;
    }

    void CaptureState()
    {
        S.OpenTabs = Tabs.Select(t => t.Path).ToList();
        S.ActiveTab = Math.Max(0, Tabs.IndexOf(current));
        S.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal && Width > 0 && !double.IsNaN(Width))
        {
            S.Left = Position.X; S.Top = Position.Y; S.Width = Width; S.Height = Height;
        }
        if (!S.SidebarCollapsed && SidebarCol.ActualWidth >= 180) S.SidebarWidth = SidebarCol.ActualWidth;
        foreach (var t in SectionToggles) S.Sections[(string)t.Content] = t.IsChecked == true;
        S.Columns = new[] { DetailsColumns.Instance.NameWidth, DetailsColumns.Instance.DateWidth, DetailsColumns.Instance.TypeWidth, DetailsColumns.Instance.SizeWidth };
    }

    void SaveSoon() { saveTimer.Stop(); saveTimer.Start(); }

    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void FitTabs()
    {
        if (TabArea.Bounds.Width > 0) TabStrip.MaxWidth = Math.Max(80, TabArea.Bounds.Width - 46);
    }

    /// <summary>Title-bar dragging, double-click to maximize and the resize grips (Orbit draws its own frame).</summary>
    void WireChrome()
    {
        void Caption(Control c)
        {
            c.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                if (e.Source is Visual v && (v.FindAncestorOfType<Button>(true) != null || v.FindAncestorOfType<Border>(true) is { } b && b.Classes.Contains("tab"))) return;
                if (e.ClickCount == 2) { Max_Click(null, null); e.Handled = true; return; }
                BeginMoveDrag(e);
                e.Handled = true;
            };
        }
        Caption(TitleBar);
        Caption(SidebarHead);
        foreach (var g in Grips.Children.OfType<Border>())
        {
            var edge = (string)g.Tag switch
            {
                "N" => WindowEdge.North, "S" => WindowEdge.South, "W" => WindowEdge.West, "E" => WindowEdge.East,
                "NW" => WindowEdge.NorthWest, "NE" => WindowEdge.NorthEast, "SW" => WindowEdge.SouthWest, _ => WindowEdge.SouthEast
            };
            g.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && WindowState == WindowState.Normal) { BeginResizeDrag(edge, e); e.Handled = true; }
            };
        }
    }

    // =====================================================================
    // Path helpers
    // =====================================================================
    /// <summary>A virtual list of real files rather than a folder: a tag view, or the Trash.</summary>
    static bool IsTag(string p) => p != null && (p.StartsWith("tag:", StringComparison.Ordinal) || RecycleBin.Is(p));
    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);
    static bool SafeDirExists(string p) { try { return !string.IsNullOrWhiteSpace(p) && Directory.Exists(p); } catch { return false; } }

    static bool IsRemote(string p) => Sftp.IsSftp(p);
    static bool LocationExists(string p) => IsTag(p) || IsRemote(p) || Archives.IsArc(p) || SafeDirExists(p);

    static string Norm(string p)
    {
        if (string.IsNullOrWhiteSpace(p) || IsTag(p)) return p;
        p = p.Trim();
        if (p.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) p = "sftp://" + p[6..];
        if (Archives.IsArc(p) || NetMount.IsNetUri(p)) return p;
        if (Sftp.IsSftp(p)) { try { return Sftp.Normalize(p); } catch { return p; } }
        try { p = IOPath.GetFullPath(p); } catch { return p; }
        if (p.Length > 1) p = p.TrimEnd('/');
        return p;
    }

    public static string Display(string p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        if (RecycleBin.Is(p)) return "Trash";
        if (IsTag(p)) return p[4..];
        if (Archives.IsArc(p)) { try { return Archives.NameOf(p); } catch { return p; } }
        if (IsRemote(p)) { try { return Sftp.NameOf(p); } catch { return p; } }
        if (Same(p, Home)) return "Home";
        if (p == "/") return "File System";
        var gvfs = GvfsName(p);
        if (gvfs != null) return gvfs;
        var name = IOPath.GetFileName(p.TrimEnd('/'));
        return string.IsNullOrEmpty(name) ? p : name;
    }

    /// <summary>"smb-share:server=nas,share=media" → "media on nas".</summary>
    static string GvfsName(string p)
    {
        var name = IOPath.GetFileName(p);
        if (!IOPath.GetDirectoryName(p)?.EndsWith("/gvfs") ?? true) return null;
        var m = System.Text.RegularExpressions.Regex.Match(name, @"(?:server|host)=([^,]+)");
        var s = System.Text.RegularExpressions.Regex.Match(name, @"share=([^,]+)");
        if (!m.Success) return null;
        return s.Success ? $"{s.Groups[1].Value} on {m.Groups[1].Value}" : m.Groups[1].Value;
    }

    static string ParentOf(string p)
    {
        if (IsTag(p)) return null;
        if (Archives.IsArc(p)) { try { return Archives.ParentOf(p); } catch { return null; } }
        if (IsRemote(p)) { try { return Sftp.ParentOf(p); } catch { return null; } }
        if (p == "/") return null;
        try { return Directory.GetParent(p)?.FullName; } catch { return null; }
    }

    static string UniqueName(ICollection<string> taken, string baseName, string ext)
    {
        string candidate = baseName + ext;
        for (int i = 2; taken.Contains(candidate); i++) candidate = $"{baseName} ({i}){ext}";
        return candidate;
    }

    static string UniqueName(string dir, string baseName, string ext)
    {
        string candidate = baseName + ext;
        for (int i = 2; File.Exists(IOPath.Combine(dir, candidate)) || Directory.Exists(IOPath.Combine(dir, candidate)); i++)
            candidate = $"{baseName} ({i}){ext}";
        return candidate;
    }

    // =====================================================================
    // Sidebar
    // =====================================================================
    void BuildPlaces()
    {
        void Add(string name, string glyph, string path)
        {
            if (SafeDirExists(path) && (path == Home || !Places.Any(p => Same(p.Path, Norm(path)))))
                Places.Add(new SideItem { Name = name, Glyph = glyph, Path = Norm(path), Kind = "place" });
        }
        Add("Home", "", Home);
        Add("Desktop", "", Xdg.UserDir("DESKTOP", "Desktop"));
        Add("Documents", "", Xdg.UserDir("DOCUMENTS", "Documents"));
        Add("Downloads", "", Xdg.UserDir("DOWNLOAD", "Downloads"));
        Add("Pictures", "", Xdg.UserDir("PICTURES", "Pictures"));
        Add("Music", "", Xdg.UserDir("MUSIC", "Music"));
        Add("Videos", "", Xdg.UserDir("VIDEOS", "Videos"));
        Places.Add(new SideItem { Name = "Trash", Glyph = "", Path = RecycleBin.Path, Kind = "recycle", ToolTipText = "Restore or permanently delete items; drop files here to delete them" });
        foreach (var p in S.CustomPlaces)
            if (SafeDirExists(p)) Places.Add(new SideItem { Name = Display(p), Glyph = "", Path = Norm(p), Kind = "pinned", ToolTipText = p });
    }

    void RebuildBookmarks()
    {
        Bookmarks.Clear();
        foreach (var p in S.Bookmarks)
            Bookmarks.Add(new SideItem { Name = Display(p), Glyph = "", Path = p, Kind = "bookmark" });
        NoBookmarks.IsVisible = Bookmarks.Count == 0;
        MarkActive();
    }

    void RebuildRecent()
    {
        Recent.Clear();
        foreach (var p in S.Recent.Take(6))
            Recent.Add(new SideItem { Name = Display(p), Glyph = "", Path = p, Kind = "recent" });
        RecentBlock.IsVisible = Recent.Count > 0;
        MarkActive();
    }

    void RebuildTags()
    {
        TagItems.Clear();
        var used = new HashSet<string>(S.Tags.Values.SelectMany(v => v));
        foreach (var t in TagPalette.Names.Where(used.Contains))
            TagItems.Add(new SideItem { Name = t, Path = "tag:" + t, TagBrush = TagPalette.Brush(t), Kind = "tag" });
        NoTags.IsVisible = TagItems.Count == 0;
        MarkActive();
    }

    void AddRecent(string path)
    {
        S.Recent.RemoveAll(r => Same(r, path));
        S.Recent.Insert(0, path);
        if (S.Recent.Count > 12) S.Recent.RemoveRange(12, S.Recent.Count - 12);
        RebuildRecent();
        SaveSoon();
    }

    async void RefreshDrives()
    {
        if (drivesBusy) return;
        drivesBusy = true;
        try
        {
            var list = await Task.Run(Orbit.Drives.List);
            bool sameSet = list.Count == Drives.Count && list.Select(x => x.Mount).SequenceEqual(Drives.Select(d => d.Path), StringComparer.Ordinal);
            if (!sameSet)
            {
                Drives.Clear();
                foreach (var d in list)
                {
                    var glyph = d.Optical ? "" : d.Removable ? "" : d.Network ? "" : "";
                    Drives.Add(new SideItem
                    {
                        Name = d.Name,
                        Path = d.Mount,
                        Glyph = glyph,
                        Kind = "drive",
                        Device = d.Device,
                        Removable = d.Removable || d.Optical || d.Device.Contains("/loop"),
                        ShowUsage = d.Total > 0,
                        ToolTipText = string.IsNullOrEmpty(d.Label) ? $"{d.Mount} ({d.Fs})" : $"{d.Label} ({d.Mount})"
                    });
                }
                MarkActive();
            }
            for (int i = 0; i < list.Count && i < Drives.Count; i++)
            {
                Drives[i].Detail = Fmt.Size(list[i].Free) + " free";
                Drives[i].Usage = list[i].Total > 0 ? 1.0 - (double)list[i].Free / list[i].Total : 0;
            }
        }
        catch { }
        finally { drivesBusy = false; }
    }

    void MarkActive()
    {
        var p = current?.Path;
        foreach (var col in new[] { Places, Bookmarks, TagItems, Drives, Network, Recent })
            foreach (var s in col) s.IsActive = p != null && Same(s.Path, p);
    }

    void ApplySectionStates()
    {
        foreach (var t in SectionToggles)
            t.IsChecked = !S.Sections.TryGetValue((string)t.Content, out var open) || open;
    }

    void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        if (!S.SidebarCollapsed && SidebarCol.ActualWidth >= 180) S.SidebarWidth = SidebarCol.ActualWidth;
        S.SidebarCollapsed = !S.SidebarCollapsed;
        ApplySidebar();
        SaveSoon();
    }

    void ApplySidebar()
    {
        if (S.SidebarCollapsed)
        {
            SidebarCol.MinWidth = 0;
            SidebarCol.Width = new GridLength(0);
            Sidebar.IsVisible = false;
            Splitter.IsVisible = false;
            ShowSidebarBtn.IsVisible = true;
        }
        else
        {
            Sidebar.IsVisible = true;
            Splitter.IsVisible = true;
            SidebarCol.MinWidth = 180;
            SidebarCol.Width = new GridLength(S.SidebarWidth);
            ShowSidebarBtn.IsVisible = false;
        }
    }

    void SideItem_Click(object sender, RoutedEventArgs e)
    {
        if (((Control)sender).Tag is SideItem s) OpenSide(s, false);
    }

    void OpenSide(SideItem s, bool newTab)
    {
        if (s.Kind == "recycle")
        {
            if (newTab) AddTab(RecycleBin.Path, true); else Navigate(RecycleBin.Path);
            return;
        }
        if (s.Kind == "connect") { ShowConnectDialog(null); return; }
        if (s.Kind == "server") { _ = ConnectServer(s.Server, newTab, null); return; }
        if (!LocationExists(s.Path) && !NetMount.IsNetUri(s.Path))
        {
            Toast($"“{s.Name}” is not available");
            return;
        }
        if (newTab) AddTab(s.Path, true);
        else Navigate(s.Path);
    }

    void SideItem_PointerReleased(object sender, PointerReleasedEventArgs e)
    {
        if (((Control)sender).Tag is not SideItem s) return;
        if (e.InitialPressMouseButton == MouseButton.Middle && s.Kind is not ("recycle" or "connect"))
        {
            if (s.Kind == "server") _ = ConnectServer(s.Server, true, null);
            else AddTab(s.Path, false);
            e.Handled = true;
        }
        else if (e.InitialPressMouseButton == MouseButton.Right)
        {
            e.Handled = true;
            ShowSideMenu((Control)sender, s);
        }
    }

    void ShowSideMenu(Control anchor, SideItem s)
    {
        var menu = new ContextMenu();
        if (s.Kind is "server" or "connect" or "pinned")
        {
            if (s.Kind == "pinned") BuildPinnedMenu(menu, s);
            else BuildServerMenu(menu, s);
            OpenMenu(menu, anchor, null);
            return;
        }
        if (s.Kind == "recycle")
        {
            AddRecycleSideItems(menu, s);
        }
        else
        {
            menu.Items.Add(MI("Open", "", () => OpenSide(s, false)));
            menu.Items.Add(MI("Open in new tab", "", () => OpenSide(s, true)));
            AddDriveMenuItems(menu, s);
            if (!IsTag(s.Path))
            {
                menu.Items.Add(new Separator());
                bool marked = S.Bookmarks.Any(b => Same(b, s.Path));
                menu.Items.Add(MI(marked ? "Remove bookmark" : "Add bookmark", marked ? "" : "", () => ToggleBookmark(s.Path)));
                if (s.Kind == "recent")
                    menu.Items.Add(MI("Remove from Recent", "", () => { S.Recent.RemoveAll(r => Same(r, s.Path)); RebuildRecent(); SaveSoon(); }));
                menu.Items.Add(MI("Copy path", "", () => SetClipboardText(s.Path)));
                menu.Items.Add(MI("Open in Terminal", "", () => Try(() => Shell.OpenTerminal(s.Path))));
                menu.Items.Add(MI("Open in File Manager", "", () => Try(() => Shell.OpenInFileManager(s.Path))));
                menu.Items.Add(new Separator());
                menu.Items.Add(MI("Properties", "", () => ShowPropertiesFor(s.Path)));
            }
            else
            {
                var tag = s.Path[4..];
                menu.Items.Add(new Separator());
                menu.Items.Add(MI($"Remove “{tag}” from all items", "", () => RemoveTagEverywhere(tag)));
            }
        }
        OpenMenu(menu, anchor, null);
    }

    // =====================================================================
    // Tabs
    // =====================================================================
    TabState AddTab(string path, bool activate)
    {
        var t = new TabState { Path = Norm(path) };
        int i = current != null ? Tabs.IndexOf(current) + 1 : Tabs.Count;
        Tabs.Insert(Math.Clamp(i, 0, Tabs.Count), t);
        if (activate) SwitchTo(t);
        SaveSoon();
        return t;
    }

    void SwitchTo(TabState t)
    {
        if (t == null || ReferenceEquals(current, t)) return;
        CommitAnyRename();
        if (current != null) current.IsActive = false;
        current = t;
        t.IsActive = true;
        ClearSearch(false);
        LoadCurrent();
        SaveSoon();
    }

    void CloseTab(TabState t)
    {
        if (t == null) return;
        if (Tabs.Count == 1)
        {
            t.Back.Clear();
            t.Forward.Clear();
            Navigate(Home, history: false);
            return;
        }
        int i = Tabs.IndexOf(t);
        Tabs.Remove(t);
        if (ReferenceEquals(t, current))
        {
            current = null;
            SwitchTo(Tabs[Math.Min(i, Tabs.Count - 1)]);
        }
        SaveSoon();
    }

    void CycleTab(int dir)
    {
        if (Tabs.Count < 2) return;
        int i = (Tabs.IndexOf(current) + dir + Tabs.Count) % Tabs.Count;
        SwitchTo(Tabs[i]);
    }

    void NewTab_Click(object sender, RoutedEventArgs e) => AddTab(IsTag(current?.Path) ? Home : current?.Path ?? Home, true);

    void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if (((Control)sender).Tag is TabState t) CloseTab(t);
        e.Handled = true;
    }

    // =====================================================================
    // Navigation
    // =====================================================================
    void Navigate(string path, string select = null, bool history = true)
    {
        if (current == null || string.IsNullOrWhiteSpace(path)) return;
        path = Norm(path);
        if (NetMount.IsNetUri(path)) { _ = ConnectNetAndGo(path, select, history); return; }
        if (!LocationExists(path))
        {
            try
            {
                if (File.Exists(path)) { Navigate(IOPath.GetDirectoryName(path), path, history); return; }
            }
            catch { }
            Toast($"Can't find “{path}”");
            return;
        }
        CommitAnyRename();
        if (history && current.Path != null && !Same(current.Path, path))
        {
            current.Back.Push(current.Path);
            current.Forward.Clear();
        }
        current.Path = path;
        if (!IsTag(path)) AddRecent(path);
        ClearSearch(false);
        LoadCurrent(select);
        SaveSoon();
    }

    void GoBack()
    {
        if (current == null || current.Back.Count == 0) return;
        CommitAnyRename();
        var from = current.Path;
        current.Forward.Push(from);
        current.Path = current.Back.Pop();
        ClearSearch(false);
        LoadCurrent(from);
    }

    void GoForward()
    {
        if (current == null || current.Forward.Count == 0) return;
        CommitAnyRename();
        current.Back.Push(current.Path);
        current.Path = current.Forward.Pop();
        ClearSearch(false);
        LoadCurrent();
    }

    void GoUp()
    {
        if (current == null) return;
        var parent = ParentOf(current.Path);
        if (parent != null) Navigate(parent, Archives.IsArc(current.Path) ? ArchiveSelectOnUp(current.Path) : current.Path);
    }

    void Back_Click(object sender, RoutedEventArgs e) => GoBack();
    void Forward_Click(object sender, RoutedEventArgs e) => GoForward();
    void Up_Click(object sender, RoutedEventArgs e) => GoUp();
    void Refresh_Click(object sender, RoutedEventArgs e) => LoadCurrent(keepSelection: true);
    void Star_Click(object sender, RoutedEventArgs e) => ToggleBookmark(null);

    async void LoadCurrent(string select = null, bool keepSelection = false)
    {
        if (current == null) return;
        int ver = ++loadVersion;
        string path = current.Path;
        var list0 = ActiveList;
        var keep = keepSelection ? list0.SelectedItems.Cast<FileItem>().Select(f => f.FullPath).ToList() : null;
        double keepOffset = keepSelection ? (ScrollOf(list0)?.Offset.Y ?? 0) : 0;
        if (!keepSelection) ClosePreview();

        StopWatcher();
        IconService.CancelPending();
        UpdateChrome();
        loading = true;
        loadError = null;
        if (!keepSelection) ShowItems(new List<FileItem>());
        EmptyPanel.IsVisible = false;

        _ = Task.Delay(180).ContinueWith(_ => Dispatcher.UIThread.Post(() => { if (ver == loadVersion && loading) LoadingText.IsVisible = true; }));

        List<string> tagPaths = null;
        if (IsTag(path) && !RecycleBin.Is(path))
        {
            var tag = path[4..];
            tagPaths = S.Tags.Where(kv => kv.Value.Contains(tag)).Select(kv => kv.Key).ToList();
        }

        List<FileItem> result = null;
        string err = null;
        bool remote = IsRemote(path);
        if (remote && !Sftp.IsConnected(path))
        {
            LoadingText.Text = "Connecting…";
            LoadingText.IsVisible = true;
            var cerr = await EnsureSftp(path);
            if (ver != loadVersion) return;
            if (cerr != null) err = cerr.Length == 0 ? "Not signed in to this server. Press F5 to try again." : cerr;
        }
        LoadingText.Text = "Loading…";
        if (err == null)
        {
            try
            {
                result = await Task.Run(() => RecycleBin.Is(path) ? RecycleBin.List() : tagPaths != null ? FromPaths(tagPaths) : Archives.IsArc(path) ? Archives.List(path) : remote ? Sftp.List(path) : Enumerate(path));
            }
            catch (Exception ex) when (remote) { err = Sftp.Describe(ex); }
            catch (Exception ex) when (Archives.IsArc(path)) { err = ArchiveError(ex); }
            catch (UnauthorizedAccessException) { err = "You don't have permission to view this folder."; }
            catch (DirectoryNotFoundException) { err = "This folder no longer exists."; }
            catch (Exception ex) { err = ex.Message; }
        }

        if (ver != loadVersion) return; // user navigated somewhere else meanwhile

        loading = false;
        LoadingText.IsVisible = false;
        loadError = err;
        result ??= new List<FileItem>();
        foreach (var f in result)
        {
            ApplyTags(f);
            if (cutPaths.Contains(f.FullPath)) f.IsCut = true;
        }
        ShowItems(result);
        if (err == null && !IsTag(path) && !remote && !Archives.IsArc(path)) StartWatcher(path, ver);

        var list = ActiveList;
        if (keep != null && keep.Count > 0)
        {
            foreach (var p in keep)
                if (index.TryGetValue(p, out var f) && view.Contains(f)) list.SelectedItems.Add(f);
        }
        if (select != null && index.TryGetValue(select, out var s) && ListContains(list, s))
        {
            list.SelectedItem = s;
            list.ScrollIntoView(s);
        }
        else if (keepSelection && keepOffset > 0)
        {
            Dispatcher.UIThread.Post(() => { if (ScrollOf(list) is { } sv) sv.Offset = new Vector(sv.Offset.X, keepOffset); }, DispatcherPriority.Loaded);
        }
        UpdateStatus();
        FocusList();

        // file passed on the command line: select it (and preview it with --preview)
        if (pendingSelect != null)
        {
            var want = pendingSelect;
            bool show = pendingPreview;
            pendingSelect = null;
            pendingPreview = false;
            if (index.TryGetValue(want, out var target))
            {
                list.SelectedItem = target;
                list.ScrollIntoView(target);
                if (show) Dispatcher.UIThread.Post(OpenPreview, DispatcherPriority.Loaded);
            }
        }
    }

    static bool ListContains(ListBox l, FileItem f) => l.ItemsSource is System.Collections.IList il && il.Contains(f);

    static List<FileItem> Enumerate(string path)
    {
        var di = new DirectoryInfo(path);
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false
        };
        var list = new List<FileItem>();
        foreach (var fsi in di.EnumerateFileSystemInfos("*", opts))
        {
            try { list.Add(FileItem.From(fsi)); } catch { }
        }
        return list;
    }

    static List<FileItem> FromPaths(IEnumerable<string> paths)
    {
        var list = new List<FileItem>();
        foreach (var p in paths)
        {
            var f = FileItem.TryFromPath(p);
            if (f != null) list.Add(f);
        }
        return list;
    }

    void ShowItems(List<FileItem> list)
    {
        items = list;
        index = new Dictionary<string, FileItem>(StringComparer.Ordinal);
        foreach (var f in list) index[f.FullPath] = f;
        view.ReplaceAll(Sorted(items.Where(FilterItem)));
        BindActiveView();
        anchorIndex = -1;
    }

    IEnumerable<FileItem> Sorted(IEnumerable<FileItem> src)
    {
        var l = src.ToList();
        l.Sort(new ItemComparer(S.SortBy, S.SortDesc));
        return l;
    }

    /// <summary>Re-applies filter and sort to the current folder, keeping the selection.</summary>
    void RefreshView()
    {
        var list = ActiveList;
        var keep = list.SelectedItems.Cast<FileItem>().ToList();
        view.ReplaceAll(Sorted(items.Where(FilterItem)));
        if (S.ViewMode != "tree")
            foreach (var f in keep) if (view.Contains(f)) list.SelectedItems.Add(f);
        AfterViewChanged();
    }

    /// <summary>Puts one new item into the visible list at its sorted position.</summary>
    void InsertSorted(FileItem f)
    {
        if (!FilterItem(f)) return;
        var cmp = new ItemComparer(S.SortBy, S.SortDesc);
        int lo = 0, hi = view.Count;
        while (lo < hi) { int mid = (lo + hi) / 2; if (cmp.Compare(view[mid], f) < 0) lo = mid + 1; else hi = mid; }
        view.Insert(lo, f);
    }

    bool FilterItem(FileItem f)
    {
        if (!S.ShowHidden && f.IsHidden && !f.IsRenaming) return false;
        if (!PassesFilters(f)) return false;
        if (searchText.Length > 0 && f.Name.IndexOf(searchText, StringComparison.CurrentCultureIgnoreCase) < 0) return false;
        return true;
    }

    void UpdateChrome()
    {
        var p = current.Path;
        Title = Display(p) + " — Orbit";
        BackBtn.IsEnabled = current.Back.Count > 0;
        FwdBtn.IsEnabled = current.Forward.Count > 0;
        UpBtn.IsEnabled = ParentOf(p) != null;
        UpdateStar();
        MarkActive();
        BuildDetailsHeader(); // the Trash shows Date deleted / Original location

        Crumbs.Clear();
        if (RecycleBin.Is(p)) Crumbs.Add(new Crumb { Name = "Trash", Path = p, IsLast = true });
        else if (IsTag(p))
        {
            Crumbs.Add(new Crumb { Name = "Tags", Path = null });
            Crumbs.Add(new Crumb { Name = p[4..], Path = p, IsLast = true });
        }
        else if (Archives.IsArc(p)) { foreach (var c in ArchiveCrumbs(p)) Crumbs.Add(c); }
        else if (IsRemote(p))
        {
            var l = Sftp.Parse(p);
            var parts = new List<(string name, string path)> { ($"{l.User}@{l.Host}", Sftp.Make(l.User, l.Host, l.Port, "/")) };
            string acc = "";
            foreach (var seg in l.Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                acc += "/" + seg;
                parts.Add((seg, Sftp.Make(l.User, l.Host, l.Port, acc)));
            }
            for (int i = 0; i < parts.Count; i++)
                Crumbs.Add(new Crumb { Name = parts[i].name, Path = parts[i].path, IsLast = i == parts.Count - 1 });
        }
        else foreach (var c in LocalCrumbs(p)) Crumbs.Add(c);
        Dispatcher.UIThread.Post(() => CrumbScroll.Offset = new Vector(double.MaxValue, 0), DispatcherPriority.Loaded);
    }

    /// <summary>Breadcrumbs for a local folder: inside Home they start at "Home", otherwise at "/".</summary>
    static IEnumerable<Crumb> LocalCrumbs(string p, bool lastIsLast = true)
    {
        var parts = new List<(string name, string path)>();
        string rest;
        if (Same(p, Home) || p.StartsWith(Home + "/", StringComparison.Ordinal))
        {
            parts.Add(("Home", Home));
            rest = p[Home.Length..];
        }
        else
        {
            parts.Add(("/", "/"));
            rest = p;
        }
        string acc = parts[0].path;
        foreach (var seg in rest.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            acc = acc == "/" ? "/" + seg : acc + "/" + seg;
            parts.Add((GvfsName(acc) ?? seg, acc));
        }
        for (int i = 0; i < parts.Count; i++)
            yield return new Crumb { Name = parts[i].name, Path = parts[i].path, IsLast = lastIsLast && i == parts.Count - 1 };
    }

    void UpdateStar()
    {
        bool marked = current != null && S.Bookmarks.Any(b => Same(b, current.Path));
        StarBtn.Content = marked ? "" : "";
        if (marked) StarBtn.Bind(ForegroundProperty, StarBtn.GetResourceObservable("AccentBrush"));
        else StarBtn.Bind(ForegroundProperty, StarBtn.GetResourceObservable("TextBrush"));
        StarBtn.IsEnabled = current != null && !IsTag(current.Path);
        ToolTip.SetTip(StarBtn, marked ? "Remove bookmark (Ctrl+D)" : "Bookmark this folder (Ctrl+D)");
    }

    void UpdateStatus()
    {
        int dirs = 0, files = 0;
        foreach (var f in view) { if (f.IsDir) dirs++; else files++; }
        FolderCount.Text = dirs.ToString("N0");
        FileCount.Text = files.ToString("N0");
        UpdateSelText();

        string msg = null, glyph = "";
        if (loadError != null) { msg = loadError; glyph = ""; }
        else if (!loading && items.Count == 0)
            msg = RecycleBin.Is(current?.Path) ? "The Trash is empty" : IsTag(current?.Path) ? "Nothing has this tag yet" : "This folder is empty";
        else if (!loading && dirs + files == 0)
        {
            if (searchText.Length > 0 || FiltersActive) { msg = searchText.Length > 0 ? $"No items match “{searchText}”" : "Nothing here matches the filter — Filter › Clear filters"; glyph = ""; }
            else { msg = "All items here are hidden.\nPress Ctrl+H or the eye button to show them."; glyph = ""; }
        }
        EmptyText.Text = msg ?? "";
        EmptyGlyph.Text = glyph;
        EmptyPanel.IsVisible = msg != null;
    }

    void UpdateSelText()
    {
        UpdateCommandBar();
        var sel = ActiveList.SelectedItems;
        int n = sel?.Count ?? 0;
        if (n == 0) { SelText.IsVisible = false; return; }
        long size = 0;
        foreach (FileItem f in sel) if (!f.IsDir) size += f.Size;
        SelText.Text = (n == 1 ? "1 selected" : $"{n:N0} selected") + (size > 0 ? " · " + Fmt.Size(size) : "");
        SelText.IsVisible = true;
    }

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, ActiveList)) return;
        var list = (ListBox)sender;
        if ((mods & KeyModifiers.Shift) == 0 && list.SelectedItems?.Count == 1)
            anchorIndex = list.SelectedIndex;
        UpdateSelText();
        PreviewFollowSelection();
        UpdateColumnPreview();
    }

    // =====================================================================
    // File system watcher (incremental, keeps selection & scroll)
    // =====================================================================
    void StartWatcher(string path, int ver)
    {
        try
        {
            var w = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size |
                               NotifyFilters.LastWrite | NotifyFilters.Attributes
            };
            FileSystemEventHandler h = (_, e) => fsEvents.Enqueue((ver, e.FullPath, false));
            w.Created += h;
            w.Changed += h;
            w.Deleted += h;
            w.Renamed += (_, e) => { fsEvents.Enqueue((ver, e.OldFullPath, false)); fsEvents.Enqueue((ver, e.FullPath, false)); };
            w.Error += (_, _) => fsEvents.Enqueue((ver, null, true));
            w.EnableRaisingEvents = true;
            watcher = w;
        }
        catch { }
    }

    void StopWatcher()
    {
        var w = watcher;
        watcher = null;
        if (w == null) return;
        try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
    }

    void FlushFsEvents()
    {
        if (fsEvents.IsEmpty || current == null) return;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        bool overflow = false;
        while (fsEvents.TryDequeue(out var ev))
        {
            if (ev.ver != loadVersion) continue;
            if (ev.overflow) overflow = true;
            else if (ev.path != null) paths.Add(ev.path);
        }
        if (loading) return;
        if (overflow || paths.Count > 400)
        {
            if (!SafeDirExists(current.Path)) GoToExistingParent();
            else LoadCurrent(keepSelection: true);
            return;
        }
        if (paths.Count == 0) return;

        foreach (var p in paths)
        {
            if (p.EndsWith(".orbit-part", StringComparison.Ordinal)) continue; // a copy in progress
            string dir;
            try { dir = IOPath.GetDirectoryName(p); } catch { continue; }
            if (!Same(dir, current.Path)) continue;

            FileSystemInfo info = null;
            try
            {
                if (Directory.Exists(p)) info = new DirectoryInfo(p);
                else if (File.Exists(p)) info = new FileInfo(p);
                else if (new FileInfo(p).LinkTarget != null) info = new FileInfo(p);
            }
            catch { }

            index.TryGetValue(p, out var existing);
            if (info == null)
            {
                if (existing != null && !existing.IsRenaming)
                {
                    items.Remove(existing);
                    view.Remove(existing);
                    index.Remove(p);
                }
                continue;
            }

            FileItem fresh;
            try { fresh = FileItem.From(info); } catch { continue; }

            if (existing != null)
            {
                if (existing.IsRenaming) continue;
                bool wasVisible = view.Contains(existing);
                existing.UpdateFrom(fresh);
                if (wasVisible != FilterItem(existing)) { if (wasVisible) view.Remove(existing); else InsertSorted(existing); }
            }
            else
            {
                ApplyTags(fresh);
                if (cutPaths.Contains(fresh.FullPath)) fresh.IsCut = true;
                items.Add(fresh);
                index[fresh.FullPath] = fresh;
                InsertSorted(fresh);
            }
        }
        AfterViewChanged();
        UpdateStatus();
    }

    void GoToExistingParent()
    {
        var p = current.Path;
        while (p != null && !SafeDirExists(p)) p = ParentOf(p);
        Navigate(p ?? Home);
        Toast("The folder you were in was removed or renamed.");
    }

    // =====================================================================
    // Opening things
    // =====================================================================
    List<FileItem> Selected() => ActiveList.SelectedItems?.Cast<FileItem>().ToList() ?? new List<FileItem>();

    async void OpenItems(List<FileItem> sel, bool newTab = false)
    {
        if (sel.Count == 0) return;
        if (sel.Any(f => f.IsRecycled)) { Toast(RecycledHint); return; }
        if (sel.Count == 1 && sel[0].IsDir && !newTab)
        {
            Navigate(sel[0].FullPath);
            return;
        }
        var files = sel.Where(f => !f.IsDir).ToList();
        if (files.Count > 15 && !await Dialog.Ask(this, "Orbit", $"Open {files.Count} files at once?"))
            return;
        var dirs = sel.Where(f => f.IsDir).ToList();
        for (int i = 0; i < dirs.Count; i++) AddTab(dirs[i].FullPath, i == dirs.Count - 1 && files.Count == 0);
        foreach (var f in files)
        {
            if (f.InArchive) OpenArchivedFile(f);
            else if (!f.IsRemote && Archives.IsArchiveFile(f.FullPath)) Navigate(Archives.Make(f.FullPath, ""));
            else if (!f.IsRemote && Archives.IsIso(f.FullPath)) MountIso(f.FullPath);
            else if (!f.IsRemote && FolderLock.IsLockFile(f.FullPath)) OpenLock(f.FullPath, false);
            else if (f.IsRemote) OpenRemoteFile(f);
            else Try(() => Shell.Open(f.FullPath));
        }
    }

    // =====================================================================
    // Misc helpers
    // =====================================================================
    public void Toast(string msg)
    {
        ToastText.Text = msg;
        ToastBorder.IsVisible = true;
        toastTimer.Stop();
        toastTimer.Start();
    }

    void Try(Action a)
    {
        try { a(); }
        catch (Exception ex) { Toast(ex.Message); }
    }

    /// <summary>The list item (row / tile) under an event source, or null for empty space.</summary>
    static FileItem ItemFromSource(ItemsControl list, object src)
    {
        for (var v = src as Visual; v != null; v = v.GetVisualParent())
        {
            if (v is ListBoxItem lbi) return ReferenceEquals(lbi.FindAncestorOfType<ItemsControl>(), list) || list == null ? lbi.DataContext as FileItem : null;
            if (ReferenceEquals(v, list)) return null;
        }
        return null;
    }

    static ScrollViewer ScrollOf(Control c) => c?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    static T FindAncestor<T>(object src) where T : class
    {
        for (var v = src as Visual; v != null; v = v.GetVisualParent())
            if (v is T t) return t;
        return null;
    }
}
