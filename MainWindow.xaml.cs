using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shell;
using System.Windows.Threading;
using IOPath = System.IO.Path;

namespace Orbit;

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

    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static AppSettings S => AppSettings.Current;

    TabState current, startTab;
    ObservableCollection<FileItem> items = new();
    Dictionary<string, FileItem> index = new(StringComparer.OrdinalIgnoreCase);
    ListCollectionView view;
    int loadVersion;
    bool loading;
    string loadError;
    string searchText = "";
    bool suppressSearch;
    FileSystemWatcher watcher;
    readonly ConcurrentQueue<(int ver, string path, bool overflow)> fsEvents = new();
    readonly DispatcherTimer fsTimer, driveTimer, toastTimer, saveTimer;
    readonly HashSet<string> cutPaths = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<GridViewColumn, string> headerKeys = new();
    bool drivesBusy;
    int anchorIndex = -1;

    // drag state
    Point? dragStart;
    FileItem dragItem, pendingSingle, dropTarget;
    Point? dzStart;
    FileItem dzItem;
    bool dzDragging;

    IntPtr Hwnd => new WindowInteropHelper(this).Handle;
    ListBox ActiveList => S.ViewMode switch { "details" => DetailsList, "tree" => TreeList, "columns" => ColumnsList, _ => GridList };
    ToggleButton[] SectionToggles => new[] { TglPlaces, TglBookmarks, TglTags, TglDrives, TglNetwork, TglRecent };

    string pendingSelect;
    bool pendingPreview;

    public MainWindow(string startPath, string previewPath = null)
    {
        InitializeComponent();
        DataContext = this;

        fsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => FlushFsEvents(), Dispatcher);
        driveTimer = new DispatcherTimer(TimeSpan.FromSeconds(8), DispatcherPriority.Background, (_, _) => RefreshDrives(), Dispatcher);
        FileOps.Failed += msg => Dispatcher.BeginInvoke(() => Toast(msg));
        springTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        springTimer.Tick += SpringTimer_Tick;
        toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); ToastBorder.Visibility = Visibility.Collapsed; };
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
        SetHeaders();
        ApplyViewVisibility();

        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(startPath))
        {
            var p = startPath.Trim('"');
            if (p.Equals("shell:RecycleBinFolder", StringComparison.OrdinalIgnoreCase)) p = RecycleBin.Path;
            if (RecycleBin.Is(p)) { paths.Add(p); p = null; }
            else if (!Archives.IsArc(p)) { try { p = IOPath.GetFullPath(p); } catch { } }
            if (p == null) { /* Recycle Bin */ }
            else if (previewPath == null && File.Exists(p) && Archives.IsArchiveFile(p))
                p = Archives.Make(p, ""); // "Open with Orbit" on an archive opens it like a folder
            if (p == null) { }
            else if (Archives.IsArc(p))
            {
                if (File.Exists(Archives.Parse(p).archive)) paths.Add(p);
            }
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
        Loaded += OnLoaded;
        PrepareIntro();
        Closing += OnClosing;
        StateChanged += (_, _) => UpdateMaxState();
        SourceInitialized += (_, _) => { ApplyDwm(); UpdateMaxState(); };
        DpiChanged += (_, e) => IconService.Scale = e.NewDpi.DpiScaleX;
    }

    // =====================================================================
    // Window lifecycle
    // =====================================================================
    void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlayIntro();
        IconService.Scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        SwitchTo(startTab);
        RestoreSplit();
        if (PendingSpacePath != null) { var sp = PendingSpacePath; PendingSpacePath = null; Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => OpenSpace(sp)); }
        if (PendingConvertPath != null) { var cp = PendingConvertPath; PendingConvertPath = null; Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => OpenConvertPaths(new List<string> { cp })); }
        WallpaperTheme.Watch(() => Dispatcher.BeginInvoke(() => { if (S.Theme == Themes.WallpaperName) ApplyTheme(S.Theme, S.Translucent); }));
        if (PendingLock is { } pl) { PendingLock = null; Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => OpenLock(pl.Path, pl.Locking)); }
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => SearchService.Start());
        FitTabs();
    }

    void OnClosing(object sender, CancelEventArgs e)
    {
        CaptureState();
        S.Save();
        StopWatcher();
        Sftp.DisconnectAll();
        SearchService.Stop();
        CloudMount.UnmountAll();
    }

    void ApplyDwm()
    {
        try
        {
            int round = 2; // DWMWCP_ROUND
            Native.DwmSetWindowAttribute(Hwnd, 33, ref round, sizeof(int));
        }
        catch { }
        ApplyBackdrop();
        InitClipboardListener();
    }

    // =====================================================================
    // Themes
    // =====================================================================
    void Theme_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        foreach (var t in Themes.All.Append(WallpaperTheme.Build()))
        {
            var def = t;
            var mi = MI(def.Name, null, () => ApplyTheme(def.Name, S.Translucent));
            mi.Icon = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = Themes.Swatch(def),
                                   BorderBrush = (Brush)FindResource("LineBrush"), BorderThickness = new Thickness(1) };
            mi.IsChecked = Themes.Current.Name == def.Name;
            mi.ToolTip = def.Description;
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var glass = MI("Translucent window (Mica)", "", () => ApplyTheme(S.Theme, !S.Translucent));
        glass.IsChecked = S.Translucent;
        if (S.Translucent) glass.Header = $"Translucent window (Mica) · {S.Transparency}%";
        glass.ToolTip = "Lets the Windows 11 acrylic backdrop blur through the sidebar and toolbar";
        menu.Items.Add(glass);
        menu.IsOpen = true;
    }

    void ApplyTheme(string name, bool translucent)
    {
        S.Theme = Themes.Find(name).Name;
        S.Translucent = translucent;
        Themes.Apply(S.Theme, S.Translucent ? S.Transparency : 0);
        if (IconPack.Tinted && IsLoaded) RefreshIcons();
        ApplyBackdrop();
        UpdateStar();
        SaveSoon();
    }

    void ApplyBackdrop()
    {
        var hwnd = Hwnd;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int dark = Themes.Current.IsLight ? 0 : 1;
            Native.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // immersive dark mode

            var chrome = new WindowChrome
            {
                CaptionHeight = 40,
                ResizeBorderThickness = new Thickness(6),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
                GlassFrameThickness = S.Translucent ? new Thickness(-1) : new Thickness(0)
            };
            WindowChrome.SetWindowChrome(this, chrome);

            var src = HwndSource.FromHwnd(hwnd);
            if (S.Translucent)
            {
                Background = Brushes.Transparent;
                if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
                int backdrop = 3; // DWMSBT_TRANSIENTWINDOW (acrylic)
                Native.DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int));
            }
            else
            {
                int backdrop = 1; // DWMSBT_NONE
                Native.DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int));
                SetResourceReference(BackgroundProperty, "SidebarBg");
                if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = ((SolidColorBrush)FindResource("SidebarBg")).Color;
            }
        }
        catch { }
    }

    void UpdateMaxState()
    {
        if (WindowState == WindowState.Maximized)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            uint d = (uint)Math.Round(96 * dpi.DpiScaleX);
            int fx = 8, fy = 8;
            try
            {
                fx = Native.GetSystemMetricsForDpi(32, d) + Native.GetSystemMetricsForDpi(92, d);
                fy = Native.GetSystemMetricsForDpi(33, d) + Native.GetSystemMetricsForDpi(92, d);
            }
            catch { }
            Root.Margin = new Thickness(fx / dpi.DpiScaleX, fy / dpi.DpiScaleY, fx / dpi.DpiScaleX, fy / dpi.DpiScaleY);
            Root.BorderThickness = new Thickness(0);
            MaxBtn.Content = "";
        }
        else
        {
            Root.Margin = new Thickness(0);
            Root.BorderThickness = new Thickness(1);
            MaxBtn.Content = "";
        }
    }

    void RestorePlacement()
    {
        Width = S.Width;
        Height = S.Height;
        if (S.Left is double l && S.Top is double t &&
            l > SystemParameters.VirtualScreenLeft - 50 && t > SystemParameters.VirtualScreenTop - 20 &&
            l + 200 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            t + 100 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l;
            Top = t;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (S.Maximized) WindowState = WindowState.Maximized;
    }

    void CaptureState()
    {
        S.OpenTabs = Tabs.Select(t => t.Path).ToList();
        S.ActiveTab = Math.Max(0, Tabs.IndexOf(current));
        S.Maximized = WindowState == WindowState.Maximized;
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!b.IsEmpty && !double.IsNaN(b.Left) && !double.IsInfinity(b.Width))
        {
            S.Left = b.Left; S.Top = b.Top; S.Width = b.Width; S.Height = b.Height;
        }
        if (!S.SidebarCollapsed && SidebarCol.ActualWidth >= 180) S.SidebarWidth = SidebarCol.ActualWidth;
        foreach (var t in SectionToggles) S.Sections[(string)t.Content] = t.IsChecked == true;
    }

    void SaveSoon() { saveTimer.Stop(); saveTimer.Start(); }

    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void FitTabs()
    {
        if (TabArea.ActualWidth > 0) TabStrip.MaxWidth = Math.Max(80, TabArea.ActualWidth - 46);
    }

    // =====================================================================
    // Path helpers
    // =====================================================================
    /// <summary>A virtual list of real files rather than a folder: a tag view, or the Recycle Bin.</summary>
    static bool IsTag(string p) => p != null && (p.StartsWith("tag:", StringComparison.Ordinal) || RecycleBin.Is(p));
    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    static bool SafeDirExists(string p) { try { return !string.IsNullOrWhiteSpace(p) && Directory.Exists(p); } catch { return false; } }

    static bool IsRemote(string p) => Sftp.IsSftp(p);
    static bool LocationExists(string p) => IsTag(p) || IsRemote(p) || Archives.IsArc(p) || SafeDirExists(p);

    static string Norm(string p)
    {
        if (string.IsNullOrWhiteSpace(p) || IsTag(p)) return p;
        p = p.Trim();
        if (p.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) p = "sftp://" + p[6..];
        if (Archives.IsArc(p)) return p;
        if (Sftp.IsSftp(p)) { try { return Sftp.Normalize(p); } catch { return p; } }
        if (p.Length == 2 && p[1] == ':') p += "\\";
        try { p = IOPath.GetFullPath(p); } catch { return p; }
        var root = IOPath.GetPathRoot(p) ?? "";
        if (p.Length > root.Length) p = p.TrimEnd('\\', '/');
        return p;
    }

    public static string Display(string p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        if (RecycleBin.Is(p)) return "Recycle Bin";
        if (IsTag(p)) return p[4..];
        if (Archives.IsArc(p)) { try { return Archives.NameOf(p); } catch { return p; } }
        if (IsRemote(p)) { try { return Sftp.NameOf(p); } catch { return p; } }
        if (Same(p, Home)) return "Home";
        var name = IOPath.GetFileName(p.TrimEnd('\\'));
        return string.IsNullOrEmpty(name) ? p.TrimEnd('\\') : name;
    }

    static string ParentOf(string p)
    {
        if (IsTag(p)) return null;
        if (Archives.IsArc(p)) { try { return Archives.ParentOf(p); } catch { return null; } }
        if (IsRemote(p)) { try { return Sftp.ParentOf(p); } catch { return null; } }
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
            if (SafeDirExists(path)) Places.Add(new SideItem { Name = name, Glyph = glyph, Path = Norm(path), Kind = "place" });
        }
        Add("Home", "", Home);
        Add("Desktop", "", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add("Documents", "", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add("Downloads", "", Native.KnownFolder(Native.FolderDownloads) ?? IOPath.Combine(Home, "Downloads"));
        Add("Pictures", "", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Add("Music", "", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        Add("Videos", "", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        Places.Add(new SideItem { Name = "Recycle Bin", Glyph = "", Path = RecycleBin.Path, Kind = "recycle", ToolTipText = "Restore or permanently delete items; drop files here to delete them" });
        foreach (var p in S.CustomPlaces)
            if (SafeDirExists(p)) Places.Add(new SideItem { Name = Display(p), Glyph = "", Path = Norm(p), Kind = "pinned", ToolTipText = p });
    }


    void RebuildBookmarks()
    {
        Bookmarks.Clear();
        foreach (var p in S.Bookmarks)
            Bookmarks.Add(new SideItem { Name = Display(p), Glyph = "", Path = p, Kind = "bookmark" });
        MarkActive();
    }

    void RebuildRecent()
    {
        Recent.Clear();
        foreach (var p in S.Recent.Take(6))
            Recent.Add(new SideItem { Name = Display(p), Glyph = "", Path = p, Kind = "recent" });
        MarkActive();
    }

    void RebuildTags()
    {
        TagItems.Clear();
        var used = new HashSet<string>(S.Tags.Values.SelectMany(v => v));
        foreach (var t in TagPalette.Names.Where(used.Contains))
            TagItems.Add(new SideItem { Name = t, Path = "tag:" + t, TagBrush = TagPalette.Brush(t), Kind = "tag" });
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
            var list = await Task.Run(() =>
            {
                var r = new List<(string name, string label, long free, long total, DriveType type)>();
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!d.IsReady) continue;
                        r.Add((d.Name, d.VolumeLabel, d.AvailableFreeSpace, d.TotalSize, d.DriveType));
                    }
                    catch { }
                }
                return r;
            });

            bool sameSet = list.Count == Drives.Count && list.Select(x => x.name).SequenceEqual(Drives.Select(d => d.Path), StringComparer.OrdinalIgnoreCase);
            if (!sameSet)
            {
                Drives.Clear();
                foreach (var d in list)
                {
                    var glyph = d.type switch { DriveType.Removable => "", DriveType.Network => "", DriveType.CDRom => "", _ => "" };
                    Drives.Add(new SideItem
                    {
                        Name = d.name,
                        Path = d.name,
                        Glyph = glyph,
                        Kind = "drive",
                        ShowUsage = d.total > 0,
                        ToolTipText = string.IsNullOrEmpty(d.label) ? d.name : $"{d.label} ({d.name.TrimEnd('\\')})"
                    });
                }
                MarkActive();
            }
            for (int i = 0; i < list.Count && i < Drives.Count; i++)
            {
                Drives[i].Detail = Fmt.Size(list[i].free) + " free";
                Drives[i].Usage = list[i].total > 0 ? 1.0 - (double)list[i].free / list[i].total : 0;
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
            Sidebar.Visibility = Visibility.Collapsed;
            Splitter.Visibility = Visibility.Collapsed;
            ShowSidebarBtn.Visibility = Visibility.Visible;
        }
        else
        {
            Sidebar.Visibility = Visibility.Visible;
            Splitter.Visibility = Visibility.Visible;
            SidebarCol.MinWidth = 180;
            SidebarCol.Width = new GridLength(S.SidebarWidth);
            ShowSidebarBtn.Visibility = Visibility.Collapsed;
        }
    }

    void SideItem_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is SideItem s) OpenSide(s, false);
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
        if (!LocationExists(s.Path) && !s.Path.StartsWith(@"\\"))
        {
            Toast($"“{s.Name}” is not available");
            return;
        }
        if (newTab) AddTab(s.Path, true);
        else Navigate(s.Path);
    }

    void SideItem_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && ((FrameworkElement)sender).Tag is SideItem s && s.Kind is not ("recycle" or "connect"))
        {
            if (s.Kind == "server") _ = ConnectServer(s.Server, true, null);
            else AddTab(s.Path, false);
            e.Handled = true;
        }
    }

    void SideItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not SideItem s) return;
        e.Handled = true;
        var menu = new ContextMenu();
        if (s.Kind is "server" or "connect" or "pinned")
        {
            if (s.Kind == "pinned") BuildPinnedMenu(menu, s);
            else BuildServerMenu(menu, s);
            menu.PlacementTarget = (UIElement)sender;
            menu.IsOpen = true;
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
                menu.Items.Add(MI("Open in Explorer", "", () => Try(() => Shell.OpenInExplorer(s.Path))));
                menu.Items.Add(new Separator());
                menu.Items.Add(MI("Properties", "", () => ShowPropertiesFor(s.Path)));
            }
            else
            {
                var tag = s.Path[4..];
                menu.Items.Add(new Separator());
                menu.Items.Add(MI($"Remove “{tag}” from all items", "", () => RemoveTagEverywhere(tag)));
            }
        }
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }

    void SideItem_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (((FrameworkElement)sender).Tag is SideItem r && r.Kind == "recycle" && TryGetDropFiles(e.Data, out _))
            e.Effects = DragDropEffects.Move;
        else if (((FrameworkElement)sender).Tag is SideItem s && !IsTag(s.Path) && s.Kind != "recycle" && TryGetDropFiles(e.Data, out var files))
            e.Effects = ChooseEffect(e, files, s.Path);
        e.Handled = true;
    }

    void SideItem_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (((FrameworkElement)sender).Tag is SideItem r && r.Kind == "recycle" && TryGetDropFiles(e.Data, out var binned))
            RecycleDropped(binned);
        else if (((FrameworkElement)sender).Tag is SideItem s && !IsTag(s.Path) && s.Kind != "recycle" && TryGetDropFiles(e.Data, out var files))
            DoTransfer(files, s.Path, ChooseEffect(e, files, s.Path));
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

    void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is TabState t)
        {
            tabDragStart = e.GetPosition(this);
            SwitchTo(t);
        }
    }

    void Tab_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && ((FrameworkElement)sender).Tag is TabState t)
        {
            CloseTab(t);
            e.Handled = true;
        }
    }

    void TabClose_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is TabState t) CloseTab(t);
        e.Handled = true;
    }

    // =====================================================================
    // Navigation
    // =====================================================================
    void Navigate(string path, string select = null, bool history = true, bool triedConnect = false)
    {
        if (current == null || string.IsNullOrWhiteSpace(path)) return;
        path = Norm(path);
        if (!LocationExists(path))
        {
            if (path.StartsWith(@"\\") && !triedConnect)
            {
                _ = ConnectUncAndGo(path, select, history);
                return;
            }
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
        double keepOffset = keepSelection ? (FindChild<ScrollViewer>(list0)?.VerticalOffset ?? 0) : 0;
        if (!keepSelection) ClosePreview();

        StopWatcher();
        IconService.CancelPending();
        UpdateChrome();
        loading = true;
        loadError = null;
        if (!keepSelection) ShowItems(new List<FileItem>());
        EmptyPanel.Visibility = Visibility.Collapsed;

        _ = Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(180);
            if (ver == loadVersion && loading) LoadingText.Visibility = Visibility.Visible;
        });

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
            LoadingText.Visibility = Visibility.Visible;
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
        LoadingText.Visibility = Visibility.Collapsed;
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
                if (index.TryGetValue(p, out var f)) list.SelectedItems.Add(f);
        }
        if (select != null && index.TryGetValue(select, out var s))
        {
            list.SelectedItem = s;
            list.ScrollIntoView(s);
        }
        else if (keepSelection && keepOffset > 0)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FindChild<ScrollViewer>(list)?.ScrollToVerticalOffset(keepOffset));
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
                if (show) _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, OpenPreview);
            }
        }
    }

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
        items = new ObservableCollection<FileItem>(list);
        index = new Dictionary<string, FileItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in list) index[f.FullPath] = f;
        view = new ListCollectionView(items);
        using (view.DeferRefresh())
        {
            view.CustomSort = new ItemComparer(S.SortBy, S.SortDesc);
            view.Filter = FilterItem;
        }
        BindActiveView();
        anchorIndex = -1;
    }

    bool FilterItem(object o)
    {
        var f = (FileItem)o;
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
        SetHeaders(); // the Recycle Bin shows Date deleted / Original location

        Crumbs.Clear();
        if (RecycleBin.Is(p)) Crumbs.Add(new Crumb { Name = "Recycle Bin", Path = p, IsLast = true });
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
        else
        {
            var root = IOPath.GetPathRoot(p) ?? p;
            var parts = new List<(string name, string path)> { (root.TrimEnd('\\'), root) };
            string acc = root;
            foreach (var seg in p.Substring(Math.Min(root.Length, p.Length)).Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                acc = IOPath.Combine(acc, seg);
                parts.Add((seg, acc));
            }
            for (int i = 0; i < parts.Count; i++)
                Crumbs.Add(new Crumb { Name = parts[i].name, Path = parts[i].path, IsLast = i == parts.Count - 1 });
        }
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => CrumbScroll.ScrollToRightEnd());
    }

    void UpdateStar()
    {
        bool marked = current != null && S.Bookmarks.Any(b => Same(b, current.Path));
        StarBtn.Content = marked ? "" : "";
        StarBtn.Foreground = marked ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("TextBrush");
        StarBtn.IsEnabled = current != null && !IsTag(current.Path);
        StarBtn.ToolTip = marked ? "Remove bookmark (Ctrl+D)" : "Bookmark this folder (Ctrl+D)";
    }

    void UpdateStatus()
    {
        int dirs = 0, files = 0;
        if (view != null)
            foreach (FileItem f in view) { if (f.IsDir) dirs++; else files++; }
        FolderCount.Text = dirs.ToString("N0");
        FileCount.Text = files.ToString("N0");
        UpdateSelText();

        string msg = null, glyph = "";
        if (loadError != null) { msg = loadError; glyph = ""; }
        else if (!loading && items.Count == 0)
            msg = RecycleBin.Is(current?.Path) ? "The Recycle Bin is empty" : IsTag(current?.Path) ? "Nothing has this tag yet" : "This folder is empty";
        else if (!loading && dirs + files == 0)
        {
            if (searchText.Length > 0 || FiltersActive) { msg = searchText.Length > 0 ? $"No items match “{searchText}”" : "Nothing here matches the filter — Filter › Clear filters"; glyph = ""; }
            else { msg = "All items here are hidden.\nPress Ctrl+H or the eye button to show them."; glyph = ""; }
        }
        EmptyText.Text = msg ?? "";
        EmptyGlyph.Text = glyph;
        EmptyPanel.Visibility = msg == null ? Visibility.Collapsed : Visibility.Visible;
    }

    void UpdateSelText()
    {
        UpdateCommandBar();
        var sel = ActiveList.SelectedItems;
        int n = sel.Count;
        if (n == 0) { SelText.Visibility = Visibility.Collapsed; return; }
        long size = 0;
        foreach (FileItem f in sel) if (!f.IsDir) size += f.Size;
        SelText.Text = (n == 1 ? "1 selected" : $"{n:N0} selected") + (size > 0 ? " · " + Fmt.Size(size) : "");
        SelText.Visibility = Visibility.Visible;
    }

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, ActiveList)) return;
        var list = (ListBox)sender;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0 && list.SelectedItems.Count == 1)
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
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

        var ecv = (IEditableCollectionView)view;
        foreach (var p in paths)
        {
            string dir;
            try { dir = IOPath.GetDirectoryName(p); } catch { continue; }
            if (!Same(dir, current.Path)) continue;

            FileSystemInfo info = null;
            try
            {
                if (Directory.Exists(p)) info = new DirectoryInfo(p);
                else if (File.Exists(p)) info = new FileInfo(p);
            }
            catch { }

            index.TryGetValue(p, out var existing);
            if (info == null)
            {
                if (existing != null && !existing.IsRenaming)
                {
                    items.Remove(existing);
                    index.Remove(p);
                }
                continue;
            }

            FileItem fresh;
            try { fresh = FileItem.From(info); } catch { continue; }

            if (existing != null)
            {
                if (existing.IsRenaming) continue;
                existing.UpdateFrom(fresh);
                try { ecv.EditItem(existing); ecv.CommitEdit(); } catch { }
            }
            else
            {
                ApplyTags(fresh);
                if (cutPaths.Contains(fresh.FullPath)) fresh.IsCut = true;
                items.Add(fresh);
                index[fresh.FullPath] = fresh;
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
    List<FileItem> Selected() => ActiveList.SelectedItems.Cast<FileItem>().ToList();

    void OpenItems(List<FileItem> sel, bool newTab = false)
    {
        if (sel.Count == 0) return;
        if (sel.Any(f => f.IsRecycled)) { Toast(RecycledHint); return; }
        if (sel.Count == 1 && sel[0].IsDir && !newTab)
        {
            Navigate(sel[0].FullPath);
            return;
        }
        var files = sel.Where(f => !f.IsDir).ToList();
        if (files.Count > 15 &&
            MessageBox.Show(this, $"Open {files.Count} files at once?", "Orbit", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
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

    void Item_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (FindAncestor<TextBox>(e.OriginalSource) != null) return;
        if (((FrameworkElement)sender).DataContext is not FileItem f || f.IsRenaming) return;
        e.Handled = true;
        if (S.ViewMode == "tree" && f.IsDir && Keyboard.Modifiers == ModifierKeys.None) { ToggleTreeRow(f); return; }
        var sel = Selected();
        OpenItems(sel.Contains(f) ? sel : new List<FileItem> { f });
    }

    void Item_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && ((FrameworkElement)sender).DataContext is FileItem f)
        {
            e.Handled = true;
            if (f.IsDir) { AddTab(f.FullPath, false); Toast($"Opened “{f.Name}” in a new tab"); }
            else Try(() => Shell.Open(f.FullPath));
        }
    }

    // =====================================================================
    // List mouse handling: click-to-clear, drag out, multi-select drag
    // =====================================================================
    void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        dragStart = null;
        dragItem = null;
        pendingSingle = null;
        if (FindAncestor<ScrollBar>(e.OriginalSource) != null || FindAncestor<GridViewColumnHeader>(e.OriginalSource) != null) return;
        if (FindAncestor<TextBox>(e.OriginalSource) != null) return;

        var item = ItemFromSource(list, e.OriginalSource);
        if (item == null)
        {
            if (e.ClickCount == 1)
            {
                CommitAnyRename();
                if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0) list.UnselectAll();
                list.Focus();
                BeginMarquee(list, e);
            }
            return;
        }
        dragStart = e.GetPosition(list);
        dragItem = item;
        if (e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None && list.SelectedItems.Count > 1 && list.SelectedItems.Contains(item))
        {
            pendingSingle = item; // keep the multi-selection so it can be dragged; collapse on mouse up
            e.Handled = true;
            list.Focus();
        }
    }

    void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (marqueeList != null) { UpdateMarquee(e); return; }
        if (dragStart == null || dragItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var list = (ListBox)sender;
        var pos = e.GetPosition(list);
        if (Math.Abs(pos.X - dragStart.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - dragStart.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (dragItem.IsRenaming) return;
        if (dragItem.IsRecycled) { dragStart = null; dragItem = null; return; }
        if (dragItem.InArchive)
        {
            var arcSel = list.SelectedItems.Contains(dragItem) ? Selected() : new List<FileItem> { dragItem };
            dragStart = null; dragItem = null; pendingSingle = null;
            StartArchiveDrag(list, arcSel);
            return;
        }
        if (dragItem.IsRemote)
        {
            dragStart = null;
            Toast("Server files can't be dragged out yet. Right-click › Download, or Copy and paste them.");
            return;
        }

        var paths = list.SelectedItems.Contains(dragItem)
            ? Selected().Select(f => f.FullPath).ToArray()
            : new[] { dragItem.FullPath };
        dragStart = null;
        dragItem = null;
        pendingSingle = null;
        StartFileDrag(list, paths);
    }

    void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (marqueeList != null) { EndMarquee(); e.Handled = true; return; }
        if (pendingSingle != null)
        {
            var list = (ListBox)sender;
            list.SelectedItem = pendingSingle;
            (list.ItemContainerGenerator.ContainerFromItem(pendingSingle) as ListBoxItem)?.Focus();
        }
        pendingSingle = null;
        dragStart = null;
        dragItem = null;
    }

    void List_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var list = (ListBox)sender;
        if (FindAncestor<ScrollBar>(e.OriginalSource) != null || FindAncestor<GridViewColumnHeader>(e.OriginalSource) != null) return;
        if (FindAncestor<TextBox>(e.OriginalSource) != null) return;
        CommitAnyRename();
        var item = ItemFromSource(list, e.OriginalSource);
        if (item == null) list.UnselectAll();
        else if (!list.SelectedItems.Contains(item)) list.SelectedItem = item;
        e.Handled = true;
        ShowItemMenu(list, null);
    }

    void GridList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) return;
        var list = GridList;
        int count = list.Items.Count;
        if (count == 0) return;
        var panel = FindChild<VirtualizingWrapPanel>(list);
        int cols = panel?.Columns ?? 1;
        int rows = panel?.VisibleRows ?? 4;

        int cur = -1;
        if (Keyboard.FocusedElement is ListBoxItem lbi) cur = list.ItemContainerGenerator.IndexFromContainer(lbi);
        if (cur < 0) cur = list.SelectedIndex;

        int next;
        switch (e.Key)
        {
            case Key.Left: next = cur < 0 ? 0 : cur - 1; break;
            case Key.Right: next = cur < 0 ? 0 : cur + 1; break;
            case Key.Up: next = cur < 0 ? 0 : (cur - cols >= 0 ? cur - cols : cur); break;
            case Key.Down: next = cur < 0 ? 0 : (cur + cols < count ? cur + cols : (cur / cols < (count - 1) / cols ? count - 1 : cur)); break;
            case Key.Home: next = 0; break;
            case Key.End: next = count - 1; break;
            case Key.PageUp: next = Math.Max(0, (cur < 0 ? 0 : cur) - cols * rows); break;
            case Key.PageDown: next = Math.Min(count - 1, (cur < 0 ? 0 : cur) + cols * rows); break;
            default: return;
        }
        next = Math.Clamp(next, 0, count - 1);
        e.Handled = true;

        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (shift)
        {
            if (anchorIndex < 0 || anchorIndex >= count) anchorIndex = Math.Max(cur, 0);
            int a = Math.Min(anchorIndex, next), b = Math.Max(anchorIndex, next);
            list.SelectedItems.Clear();
            for (int i = a; i <= b; i++) list.SelectedItems.Add(list.Items[i]);
        }
        else if (!ctrl)
        {
            list.SelectedItem = list.Items[next];
            anchorIndex = next;
        }
        FocusIndex(list, next);
    }

    static void FocusIndex(ListBox list, int i)
    {
        if (i < 0 || i >= list.Items.Count) return;
        list.ScrollIntoView(list.Items[i]);
        list.UpdateLayout();
        (list.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem)?.Focus();
    }

    void FocusList()
    {
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (AddressBox.IsKeyboardFocusWithin || SearchBox.IsKeyboardFocusWithin) return;
            if (Keyboard.FocusedElement is TextBox) return;
            var l = ActiveList;
            var sel = l.SelectedItem;
            if (sel != null)
            {
                l.ScrollIntoView(sel);
                l.UpdateLayout();
                if (l.ItemContainerGenerator.ContainerFromItem(sel) is ListBoxItem c) { c.Focus(); return; }
            }
            l.Focus();
        });
    }

    // =====================================================================
    // Drag & drop into the content area
    // =====================================================================
    static bool TryGetDropFiles(IDataObject data, out string[] files)
    {
        files = null;
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop)) files = data.GetData(DataFormats.FileDrop) as string[];
        }
        catch { }
        return files != null && files.Length > 0;
    }

    DragDropEffects ChooseEffect(DragEventArgs e, string[] files, string target)
    {
        var allowed = e.AllowedEffects;
        DragDropEffects want;
        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0) want = DragDropEffects.Copy;
        else if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0) want = DragDropEffects.Move;
        else if (dzDragging) want = DragDropEffects.Copy;
        else
        {
            string troot = IOPath.GetPathRoot(target);
            bool sameVolume = files.All(f => Same(IOPath.GetPathRoot(f), troot));
            want = sameVolume ? DragDropEffects.Move : DragDropEffects.Copy;
        }
        if ((allowed & want) == 0)
            want = (allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy
                 : (allowed & DragDropEffects.Move) != 0 ? DragDropEffects.Move
                 : DragDropEffects.None;

        // Moving things into the folder they're already in, or a folder into itself, does nothing.
        if (want == DragDropEffects.Move && files.All(f => Same(SafeParent(f), target))) return DragDropEffects.None;
        if (files.Any(f => Same(f, target) || target.StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            return DragDropEffects.None;
        return want;
    }

    static string SafeParent(string p) { try { return IOPath.GetDirectoryName(p); } catch { return null; } }

    string DropTargetFor(DragEventArgs e, string[] files, out FileItem over)
    {
        var hit = ItemFromSource(ActiveList, e.OriginalSource);
        over = null;
        if (hit != null && hit.IsDir && !files.Any(f => Same(f, hit.FullPath))) { over = hit; return hit.FullPath; }
        return IsTag(current?.Path) || Archives.IsArc(current?.Path) ? null : current?.Path;
    }

    void Content_DragOver(object sender, DragEventArgs e)
    {
        if (TabDragOverContent(e)) return;
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (!TryGetDropFiles(e.Data, out var files)) { SetDropTarget(null); ShowDropFeedback(null, DragDropEffects.None, null); return; }
        var target = DropTargetFor(e, files, out var over);
        SetDropTarget(over);
        if (target != null) e.Effects = ChooseEffect(e, files, target);
        ShowDropFeedback(target, e.Effects, over);
        AutoScrollDuringDrag(e);
    }

    void Content_DragLeave(object sender, DragEventArgs e)
    {
        SplitDropZone.Visibility = Visibility.Collapsed;
        // DragLeave also fires when moving between child elements; only clear when the pointer really left.
        var p = e.GetPosition(ContentHost);
        if (p.X >= 0 && p.Y >= 0 && p.X < ContentHost.ActualWidth && p.Y < ContentHost.ActualHeight) return;
        SetDropTarget(null);
        ShowDropFeedback(null, DragDropEffects.None, null);
    }

    void Content_Drop(object sender, DragEventArgs e)
    {
        if (TabDropOnContent(e)) return;
        e.Handled = true;
        SetDropTarget(null);
        ShowDropFeedback(null, DragDropEffects.None, null);
        if (!TryGetDropFiles(e.Data, out var files)) return;
        var target = DropTargetFor(e, files, out _);
        if (target == null) return;
        var eff = ChooseEffect(e, files, target);
        e.Effects = eff;
        DoTransfer(files, target, eff);
    }

    void DoTransfer(string[] files, string target, DragDropEffects eff)
    {
        if (IsRemote(target)) { if (eff != DragDropEffects.None) UploadTo(files, target); return; }
        if (eff == DragDropEffects.Move) FileOps.Move(files, target, Hwnd);
        else if (eff == DragDropEffects.Copy)
            FileOps.Copy(files, target, Hwnd, files.All(f => Same(SafeParent(f), target)));
    }

    void SetDropTarget(FileItem f)
    {
        if (ReferenceEquals(dropTarget, f)) return;
        if (dropTarget != null) dropTarget.IsDropTarget = false;
        dropTarget = f;
        if (f != null) f.IsDropTarget = true;
    }

    // =====================================================================
    // Keyboard
    // =====================================================================
    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var m = Keyboard.Modifiers;
        bool ctrl = (m & ModifierKeys.Control) != 0, shift = (m & ModifierKeys.Shift) != 0, alt = (m & ModifierKeys.Alt) != 0;
        bool inText = Keyboard.FocusedElement is TextBox;
        bool inList = ActiveList.IsKeyboardFocusWithin && !inText;

        // Find anything palette: while open, keys belong to it.
        if (findOpen)
        {
            if (key == Key.Escape) { CloseFind(); e.Handled = true; }
            return;
        }

        // Quick preview: Space / Esc close it; arrow keys keep working in the list behind it.
        if (previewOpen && (key == Key.Escape || (key == Key.Space && m == ModifierKeys.None && !inText)))
        {
            ClosePreview();
            e.Handled = true;
            return;
        }
        if (previewOpen && !inList && !inText && key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
            FocusList();

        // Keys for the split pane when it has focus
        if (SplitList.IsKeyboardFocusWithin && !inText && HandleSplitKey(key, m))
        {
            e.Handled = true;
            return;
        }

        // Reassignable shortcuts (Settings › Shortcuts)
        if (RunShortcut(key, m, inText, inList))
        {
            e.Handled = true;
            return;
        }

        // Fixed keys
        bool handled = true;
        if (alt && key == Key.D) BeginEditAddress();
        else if (ctrl && shift && key == Key.F) OpenFind();
        else if (key == Key.F3) OpenSearch();
        else if (!inText)
        {
            if (key == Key.Back) GoBack();
            else if (key == Key.Enter && inList) OpenItems(Selected());
            else if (key == Key.Apps || (shift && key == Key.F10)) ShowItemMenu(ActiveList, Keyboard.FocusedElement as UIElement);
            else if (ctrl && key == Key.A && !inList) { ActiveList.SelectAll(); ActiveList.Focus(); }
            else if (key == Key.Escape)
            {
                if (searchText.Length > 0) ClearSearch(true);
                else ActiveList.UnselectAll();
            }
            else handled = false;
        }
        else handled = false;

        if (handled) e.Handled = true;
    }

    void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1) { GoBack(); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.XButton2) { GoForward(); e.Handled = true; }
    }

    // =====================================================================
    // Address bar & breadcrumbs
    // =====================================================================
    void AddressArea_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource) != null || AddressBox.IsVisible) return;
        BeginEditAddress();
        e.Handled = true;
    }

    void BeginEditAddress()
    {
        if (current == null) return;
        AddressBox.Text = IsTag(current.Path) ? "" : current.Path;
        CrumbScroll.Visibility = Visibility.Hidden;
        AddressBox.Visibility = Visibility.Visible;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { AddressBox.Focus(); Keyboard.Focus(AddressBox); AddressBox.SelectAll(); });
    }

    void EndEditAddress()
    {
        AddressBox.Visibility = Visibility.Collapsed;
        CrumbScroll.Visibility = Visibility.Visible;
    }

    void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var text = AddressBox.Text;
            EndEditAddress();
            GoToAddress(text);
            FocusList();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndEditAddress();
            FocusList();
            e.Handled = true;
        }
    }

    void AddressBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndEditAddress();

    void GoToAddress(string text)
    {
        var t = Environment.ExpandEnvironmentVariables((text ?? "").Trim().Trim('"'));
        if (t.Length == 0) return;
        if (Sftp.IsSftp(t) || t.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) { Navigate(t); return; }
        if (t.StartsWith("smb://", StringComparison.OrdinalIgnoreCase)) t = @"\\" + t[6..].Replace('/', '\\');
        if (t.StartsWith(@"\\")) { Navigate(t); return; }
        if (t.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || t.Contains("://"))
        {
            Try(() => Shell.Open(t));
            return;
        }
        if (t == "~") t = Home;
        else if (t.StartsWith("~\\") || t.StartsWith("~/")) t = IOPath.Combine(Home, t[2..]);
        try
        {
            if (!IOPath.IsPathRooted(t) && current != null && !IsTag(current.Path)) t = IOPath.Combine(current.Path, t);
        }
        catch { Toast($"“{text}” isn't a valid path"); return; }
        Navigate(t);
    }

    void Crumb_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is Crumb c && c.Path != null && !Same(c.Path, current?.Path))
        {
            var child = current?.Path;
            Navigate(c.Path, child != null && !IsTag(child) ? ChildToward(c.Path, child) : null);
        }
    }

    static string ChildToward(string ancestor, string descendant)
    {
        var rest = descendant.Substring(Math.Min(descendant.Length, ancestor.TrimEnd('\\').Length)).TrimStart('\\');
        var first = rest.Split('\\')[0];
        return first.Length == 0 ? null : IOPath.Combine(ancestor, first);
    }

    void CrumbChevron_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not Crumb c || c.Path == null || IsTag(c.Path)) return;
        if (IsRemote(c.Path)) { ShowRemoteSubfolders((UIElement)sender, c.Path); return; }
        if (Archives.IsArc(c.Path)) { ShowArchiveSubfolders((UIElement)sender, c.Path); return; }
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom, MaxHeight = 520 };
        List<string> dirs;
        try
        {
            var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
            dirs = new DirectoryInfo(c.Path).EnumerateDirectories("*", opts)
                .Where(d => S.ShowHidden || (d.Attributes & FileAttributes.Hidden) == 0)
                .Select(d => d.FullName)
                .ToList();
            dirs.Sort((a, b) => Native.StrCmpLogicalW(IOPath.GetFileName(a), IOPath.GetFileName(b)));
        }
        catch (Exception ex) { Toast(ex.Message); return; }
        if (dirs.Count == 0) menu.Items.Add(MI("No subfolders", null, null, enabled: false));
        foreach (var d in dirs.Take(400))
        {
            var target = d;
            menu.Items.Add(MI(IOPath.GetFileName(d), "", () => Navigate(target)));
        }
        menu.IsOpen = true;
    }

    // =====================================================================
    // Search / filter
    // =====================================================================
    void SearchBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Visibility == Visibility.Visible && searchText.Length == 0) { SearchBox.Visibility = Visibility.Collapsed; return; }
        OpenSearch();
    }

    void OpenSearch()
    {
        SearchBox.Visibility = Visibility.Visible;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { SearchBox.Focus(); Keyboard.Focus(SearchBox); SearchBox.SelectAll(); });
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (suppressSearch) return;
        searchText = SearchBox.Text.Trim();
        view?.Refresh(); AfterViewChanged();
        UpdateStatus();
    }

    void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { ClearSearch(true); e.Handled = true; }
        else if (e.Key == Key.Enter || e.Key == Key.Down)
        {
            var l = ActiveList;
            if (l.Items.Count > 0) { l.SelectedIndex = 0; FocusIndex(l, 0); }
            e.Handled = true;
        }
    }

    void SearchBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (SearchBox.Text.Length == 0) SearchBox.Visibility = Visibility.Collapsed;
    }

    void ClearSearch(bool focusList)
    {
        ResetFilters();
        bool had = searchText.Length > 0;
        suppressSearch = true;
        SearchBox.Text = "";
        suppressSearch = false;
        searchText = "";
        if (!SearchBox.IsKeyboardFocusWithin) SearchBox.Visibility = Visibility.Collapsed;
        if (had) { view?.Refresh(); AfterViewChanged(); UpdateStatus(); }
        if (focusList) { SearchBox.Visibility = Visibility.Collapsed; FocusList(); }
    }

    // =====================================================================
    // View / sort / hidden
    // =====================================================================
    void HiddenToggle_Click(object sender, RoutedEventArgs e)
    {
        S.ShowHidden = HiddenToggle.IsChecked == true;
        view?.Refresh(); AfterViewChanged();
        UpdateStatus();
        SaveSoon();
    }

    void ToggleHidden()
    {
        HiddenToggle.IsChecked = !(HiddenToggle.IsChecked == true);
        HiddenToggle_Click(null, null);
        Toast(S.ShowHidden ? "Showing hidden items" : "Hiding hidden items");
    }


    void Sort_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Top };
        foreach (var (key, label) in new[] { ("name", "Name"), ("date", "Date modified"), ("size", "Size"), ("type", "Type") })
        {
            var k = key;
            var mi = MI(label, null, () => { S.SortBy = k; ApplySort(); });
            mi.IsChecked = S.SortBy == k;
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var asc = MI("Ascending", "", () => { S.SortDesc = false; ApplySort(); });
        asc.IsChecked = !S.SortDesc;
        var desc = MI("Descending", "", () => { S.SortDesc = true; ApplySort(); });
        desc.IsChecked = S.SortDesc;
        menu.Items.Add(asc);
        menu.Items.Add(desc);
        menu.IsOpen = true;
    }

    void ApplySort()
    {
        if (view != null) view.CustomSort = new ItemComparer(S.SortBy, S.SortDesc);
        AfterViewChanged();
        SetHeaders();
        SaveSoon();
    }

    void SetHeaders()
    {
        headerKeys.Clear();
        bool bin = RecycleBin.Is(current?.Path);
        foreach (var (col, key, text) in new[] { (ColName, "name", "Name"), (ColDate, "date", bin ? "Date deleted" : "Date modified"), (ColType, "type", bin ? "Original location" : "Type"), (ColSize, "size", "Size") })
        {
            headerKeys[col] = key;
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = text });
            if (S.SortBy == key)
                sp.Children.Add(new TextBlock
                {
                    Text = S.SortDesc ? "" : "",
                    FontFamily = (FontFamily)FindResource("IconFont"),
                    FontSize = 9,
                    Margin = new Thickness(6, 1, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("AccentBrush")
                });
            col.Header = sp;
        }
    }

    void Header_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader h && h.Column != null && headerKeys.TryGetValue(h.Column, out var k))
        {
            if (S.SortBy == k) S.SortDesc = !S.SortDesc;
            else { S.SortBy = k; S.SortDesc = k is "date" or "size"; }
            ApplySort();
        }
    }

    // =====================================================================
    // Clipboard
    // =====================================================================
    static bool TryClipboard(Action a)
    {
        for (int i = 0; i < 6; i++)
        {
            try { a(); return true; }
            catch (ExternalException) { Thread.Sleep(40); }
            catch { return false; }
        }
        return false;
    }

    void SetClipboardText(string text)
    {
        if (TryClipboard(() => Clipboard.SetText(text))) Toast("Path copied");
    }

    void CopyPaths(IEnumerable<string> paths)
    {
        var list = paths.Where(p => p != null && !IsTag(p)).ToList();
        if (list.Count == 0) return;
        if (TryClipboard(() => Clipboard.SetText(string.Join(Environment.NewLine, list))))
            Toast(list.Count == 1 ? "Path copied" : $"{list.Count} paths copied");
    }

    void CopyToClipboard(bool cut)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        if (sel.Any(f => f.IsRecycled)) { Toast(RecycledHint); return; }
        if (sel.Any(f => f.InArchive)) { if (cut) Toast(ReadOnlyArchive); else CopyArchivedToClipboard(sel); return; }
        if (sel.Any(f => f.IsRemote))
        {
            if (cut) Toast("Cut isn't available for server files. Use Copy, then Delete.");
            else CopyRemoteToClipboard(sel);
            return;
        }
        var sc = new StringCollection();
        sc.AddRange(sel.Select(f => f.FullPath).ToArray());
        var data = new DataObject();
        data.SetFileDropList(sc);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
        if (!TryClipboard(() => Clipboard.SetDataObject(data, true))) { Toast("Couldn't access the clipboard"); return; }

        foreach (var p in cutPaths) if (index.TryGetValue(p, out var old)) old.IsCut = false;
        cutPaths.Clear();
        if (cut)
            foreach (var f in sel) { cutPaths.Add(f.FullPath); f.IsCut = true; }
        Toast($"{(cut ? "Cut" : "Copied")} {(sel.Count == 1 ? $"“{sel[0].Name}”" : sel.Count + " items")}");
    }

    static bool CanPaste()
    {
        bool ok = false;
        TryClipboard(() => ok = Clipboard.ContainsFileDropList());
        return ok;
    }

    void Paste(string targetDir)
    {
        if (targetDir == null || IsTag(targetDir)) return;
        if (Archives.IsArc(targetDir)) { Toast(ReadOnlyArchive); return; }
        string[] files = null;
        bool move = false;
        TryClipboard(() =>
        {
            var d = Clipboard.GetDataObject();
            if (d != null && d.GetDataPresent(DataFormats.FileDrop))
            {
                files = d.GetData(DataFormats.FileDrop) as string[];
                if (d.GetDataPresent("Preferred DropEffect") && d.GetData("Preferred DropEffect") is MemoryStream ms)
                {
                    var b = ms.ToArray();
                    if (b.Length >= 4) move = (BitConverter.ToInt32(b, 0) & 2) != 0;
                }
            }
        });
        if (files == null || files.Length == 0) { Toast("Nothing to paste"); return; }
        if (IsRemote(targetDir)) { UploadTo(files, targetDir); return; }

        if (move)
        {
            if (files.All(f => Same(SafeParent(f), targetDir))) { ClearCut(); return; }
            FileOps.Move(files, targetDir, Hwnd, () => Dispatcher.BeginInvoke(() =>
            {
                TryClipboard(Clipboard.Clear);
                ClearCut();
            }));
        }
        else
        {
            FileOps.Copy(files, targetDir, Hwnd, files.All(f => Same(SafeParent(f), targetDir)));
        }
    }

    void ClearCut()
    {
        foreach (var p in cutPaths) if (index.TryGetValue(p, out var f)) f.IsCut = false;
        cutPaths.Clear();
    }

    // =====================================================================
    // Create / rename / delete
    // =====================================================================
    void NewFolder() => CreateNew(true);
    void NewTextFile() => CreateNew(false);

    void CreateNew(bool folder, string baseName = null, string ext = ".txt")
    {
        if (current == null || IsTag(current.Path)) return;
        if (Archives.IsArc(current.Path)) { Toast(ReadOnlyArchive); return; }
        if (IsRemote(current.Path)) { CreateNewRemote(folder, baseName, ext); return; }
        string name = folder ? UniqueName(current.Path, baseName ?? "New folder", "") : UniqueName(current.Path, baseName ?? "New Text Document", ext);
        var p = IOPath.Combine(current.Path, name);
        try
        {
            if (folder) Directory.CreateDirectory(p);
            else using (File.Create(p)) { }
        }
        catch (Exception ex) { Toast(ex.Message); return; }

        var f = FileItem.TryFromPath(p);
        if (f == null) return;
        if (!index.ContainsKey(p)) { items.Add(f); index[p] = f; }
        else f = index[p];
        ClearSearch(false);
        ActiveList.SelectedItem = f;
        BeginRename(f);
        UpdateStatus();
    }

    void BeginRenameSelected()
    {
        var sel = Selected();
        if (sel.Count == 1) BeginRename(sel[0]);
    }

    void BeginRename(FileItem f)
    {
        CommitAnyRename();
        var l = ActiveList;
        l.ScrollIntoView(f);
        l.UpdateLayout();
        f.EditName = f.Name;
        f.IsRenaming = true;
    }

    void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue || sender is not TextBox tb || tb.DataContext is not FileItem f || !f.IsRenaming) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            tb.Focus();
            Keyboard.Focus(tb);
            int dot = f.IsDir ? -1 : tb.Text.LastIndexOf('.');
            if (dot > 0) tb.Select(0, dot); else tb.SelectAll();
        });
    }

    void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not FileItem f) return;
        if (e.Key == Key.Enter) { CommitRename(f); FocusList(); }
        else if (e.Key == Key.Escape) { f.IsRenaming = false; FocusList(); }
        e.Handled = true; // never let keystrokes leak to the list while typing a name
    }

    void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus == null) return; // window deactivated: keep editing
        if (sender is TextBox tb && tb.DataContext is FileItem f && f.IsRenaming) CommitRename(f);
    }

    void CommitAnyRename()
    {
        foreach (var f in items.Where(i => i.IsRenaming).ToList()) CommitRename(f);
    }

    void CommitRename(FileItem f)
    {
        if (!f.IsRenaming) return;
        f.IsRenaming = false;
        var newName = (f.EditName ?? "").Trim();
        if (newName.Length == 0 || newName == f.Name) return;
        if (newName.IndexOfAny(IOPath.GetInvalidFileNameChars()) >= 0)
        {
            Toast("A name can't contain any of these characters:  \\ / : * ? \" < > |");
            return;
        }
        if (f.IsRecycled) { Toast(RecycledHint); return; }
        if (f.InArchive) { Toast(ReadOnlyArchive); return; }
        if (f.IsRemote) { CommitRemoteRename(f, newName); return; }
        var dir = SafeParent(f.FullPath);
        if (dir == null) return;
        var src = f.FullPath;
        var dest = IOPath.Combine(dir, newName);
        bool caseOnly = Same(src, dest);
        if (!caseOnly && (File.Exists(dest) || Directory.Exists(dest)))
        {
            Toast($"“{newName}” already exists here");
            return;
        }
        try
        {
            if (f.IsDir)
            {
                if (caseOnly)
                {
                    var tmp = dest + ".orbit-" + Guid.NewGuid().ToString("N")[..6];
                    Directory.Move(src, tmp);
                    Directory.Move(tmp, dest);
                }
                else Directory.Move(src, dest);
            }
            else File.Move(src, dest);
        }
        catch (Exception ex)
        {
            Toast("Couldn't rename: " + ex.Message);
            return;
        }

        index.Remove(src);
        f.Rebase(newName, dest);
        index[dest] = f;
        MovePathReferences(src, dest);
        try { var ecv = (IEditableCollectionView)view; ecv.EditItem(f); ecv.CommitEdit(); } catch { }
        ActiveList.SelectedItem = f;
        ActiveList.ScrollIntoView(f);
    }

    void MovePathReferences(string oldPath, string newPath)
    {
        string Map(string p) =>
            Same(p, oldPath) ? newPath :
            p.StartsWith(oldPath + "\\", StringComparison.OrdinalIgnoreCase) ? newPath + p[oldPath.Length..] : null;

        bool changed = false;
        foreach (var k in S.Tags.Keys.ToList())
        {
            var m = Map(k);
            if (m != null) { var v = S.Tags[k]; S.Tags.Remove(k); S.Tags[m] = v; changed = true; }
        }
        for (int i = 0; i < S.Bookmarks.Count; i++) { var m = Map(S.Bookmarks[i]); if (m != null) { S.Bookmarks[i] = m; changed = true; } }
        for (int i = 0; i < S.Recent.Count; i++) { var m = Map(S.Recent[i]); if (m != null) { S.Recent[i] = m; changed = true; } }
        if (changed) { RebuildBookmarks(); RebuildRecent(); SaveSoon(); }
    }

    void DeleteSelected(bool permanent)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        if (sel.Any(f => f.IsRecycled)) { DeleteRecycled(sel); return; }
        if (sel.Any(f => f.InArchive)) { Toast(ReadOnlyArchive); return; }
        if (sel.Any(f => f.IsRemote)) { DeleteRemote(sel); return; }
        if (permanent)
        {
            var msg = sel.Count == 1 ? $"Permanently delete “{sel[0].Name}”?\n\nThis can't be undone." : $"Permanently delete {sel.Count} items?\n\nThis can't be undone.";
            if (MessageBox.Show(this, msg, "Delete permanently", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
        }
        // pre-select the neighbour so focus lands somewhere sensible after the item vanishes
        var l = ActiveList;
        int idx = l.Items.IndexOf(sel[0]);
        FileOps.Delete(sel.Select(f => f.FullPath), !permanent, Hwnd, () => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (l.SelectedItems.Count == 0 && l.Items.Count > 0)
            {
                int i = Math.Clamp(idx, 0, l.Items.Count - 1);
                l.SelectedIndex = i;
                FocusIndex(l, i);
            }
        }));
    }

    /// <summary>Orbit's own Properties window for the selection (or the current folder when nothing is selected).</summary>
    void ShowProperties()
    {
        var sel = Selected();
        if (sel.Count == 0)
        {
            if (current == null || IsTag(current.Path)) return;
            var here = IsRemote(current.Path)
                ? FileItem.Remote(current.Path, Display(current.Path), true, -1, default)
                : FileItem.TryFromPath(current.Path);
            if (here == null) return;
            sel = new List<FileItem> { here };
        }
        new PropertiesWindow(this, sel).Show();
    }

    /// <summary>Orbit's own Properties window for a path (themed and translucent like the rest of Orbit);
    /// the Windows dialog is one click away inside it, and is used directly for things Orbit can't describe.</summary>
    void ShowPropertiesFor(string path)
    {
        // a whole drive is better served by Windows' own dialog (used / free space chart) than by counting every file
        bool driveRoot = path != null && !IsTag(path) && !IsRemote(path) && IOPath.GetPathRoot(path)?.TrimEnd('\\') == path.TrimEnd('\\');
        var item = path == null || driveRoot || IsTag(path) || IsRemote(path) ? null : FileItem.TryFromPath(path);
        if (item != null) new PropertiesWindow(this, new List<FileItem> { item }).Show();
        else ShowWindowsProperties(path);
    }

    internal void ShowWindowsProperties(string path)
    {
        if (path != null && !IsTag(path) && !IsRemote(path)) Shell.Properties(path, Hwnd);
    }

    internal void AfterAttributesChanged() => LoadCurrent(keepSelection: true);

    // =====================================================================
    // Bookmarks & tags
    // =====================================================================
    void ToggleBookmark(string path)
    {
        path ??= current?.Path;
        if (path == null || IsTag(path)) return;
        int i = S.Bookmarks.FindIndex(b => Same(b, path));
        if (i >= 0) { S.Bookmarks.RemoveAt(i); Toast($"Removed “{Display(path)}” from bookmarks"); }
        else { S.Bookmarks.Add(path); Toast($"Bookmarked “{Display(path)}”"); }
        RebuildBookmarks();
        UpdateStar();
        SaveSoon();
    }

    void ApplyTags(FileItem f) => f.SetTags(S.Tags.TryGetValue(f.FullPath, out var l) ? l : null);

    void SetTag(List<FileItem> sel, string tag, bool on)
    {
        foreach (var f in sel)
        {
            var list = S.Tags.TryGetValue(f.FullPath, out var l) ? l.ToList() : new List<string>();
            if (on && !list.Contains(tag)) list.Add(tag);
            if (!on) list.Remove(tag);
            list = TagPalette.Names.Where(list.Contains).ToList();
            if (list.Count == 0) S.Tags.Remove(f.FullPath); else S.Tags[f.FullPath] = list;
            f.SetTags(list);
        }
        AfterTagChange();
    }

    void ClearTags(List<FileItem> sel)
    {
        foreach (var f in sel) { S.Tags.Remove(f.FullPath); f.SetTags(null); }
        AfterTagChange();
    }

    void RemoveTagEverywhere(string tag)
    {
        foreach (var k in S.Tags.Keys.ToList())
        {
            S.Tags[k].Remove(tag);
            if (S.Tags[k].Count == 0) S.Tags.Remove(k);
        }
        foreach (var f in items) ApplyTags(f);
        if (current != null && current.Path == "tag:" + tag) Navigate(Home);
        AfterTagChange();
    }

    void AfterTagChange()
    {
        RebuildTags();
        SaveSoon();
        if (IsTag(current?.Path)) LoadCurrent(keepSelection: true);
    }

    // =====================================================================
    // Context menu
    // =====================================================================
    MenuItem MI(string header, string glyph, Action act, string gesture = null, bool enabled = true)
    {
        var mi = new MenuItem
        {
            Header = new TextBlock { Text = header },
            InputGestureText = gesture ?? "",
            IsEnabled = enabled
        };
        if (glyph != null)
            mi.Icon = new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        if (act != null) mi.Click += (_, e) => { e.Handled = true; Try(act); };
        return mi;
    }

    void ShowItemMenu(ListBox list, UIElement anchor)
    {
        if (current == null) return;
        var sel = Selected();
        var menu = new ContextMenu();
        bool tagView = IsTag(current.Path);
        if (RecycleBin.Is(current.Path)) { ShowRecycleMenu(list, anchor, sel); return; }
        if (Archives.IsArc(current.Path)) { ShowArchiveMenu(list, anchor, sel); return; }
        if (IsRemote(current.Path)) { ShowRemoteMenu(list, anchor, sel); return; }

        if (sel.Count == 0)
        {
            if (!tagView)
            {
                var nw = MI("New", "", null);
                nw.Items.Add(MI("Folder", "", NewFolder, "Ctrl+Shift+N"));
                nw.Items.Add(MI("Text document", "", NewTextFile));
                menu.Items.Add(nw);
                menu.Items.Add(MI("Paste", "", () => Paste(current.Path), "Ctrl+V", CanPaste()));
                menu.Items.Add(new Separator());
            }
            menu.Items.Add(MI("Refresh", "", () => LoadCurrent(keepSelection: true), "F5"));
            var hid = MI("Show hidden items", "", ToggleHidden, "Ctrl+H");
            hid.IsChecked = S.ShowHidden;
            menu.Items.Add(hid);
            menu.Items.Add(MI("Select all", "", () => ActiveList.SelectAll(), "Ctrl+A"));
            if (!tagView)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(MI("Copy path", "", () => SetClipboardText(current.Path), "Ctrl+Shift+C"));
                menu.Items.Add(MI("Open in Terminal", "", () => Shell.OpenTerminal(current.Path)));
                menu.Items.Add(MI("Open in Explorer", "", () => Shell.OpenInExplorer(current.Path)));
                menu.Items.Add(new Separator());
                menu.Items.Add(MI("Properties", "", () => ShowPropertiesFor(current.Path), "Alt+Enter"));
            }
        }
        else
        {
            var first = sel[0];
            bool allDirs = sel.All(f => f.IsDir);
            menu.Items.Add(MI("Open", "", () => OpenItems(sel), "Enter"));
            if (allDirs) menu.Items.Add(MI(sel.Count == 1 ? "Open in new tab" : "Open in new tabs", "", () => OpenItems(sel, true)));
            if (allDirs && sel.Count == 1) menu.Items.Add(MI("Open in split view", "", () => OpenSplit(first.FullPath), "Ctrl+\\"));
            if (sel.Count == 1 && !first.IsDir) menu.Items.Add(MI("Open with…", "", () => Shell.OpenWith(first.FullPath)));
            AddArchiveItemsToMenu(menu, sel);
            if (tagView) menu.Items.Add(MI("Show in folder", "", () => Navigate(SafeParent(first.FullPath), first.FullPath)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Cut", "", () => CopyToClipboard(true), "Ctrl+X"));
            menu.Items.Add(MI("Copy", "", () => CopyToClipboard(false), "Ctrl+C"));
            if (sel.Count == 1 && first.IsDir)
                menu.Items.Add(MI("Paste into folder", "", () => Paste(first.FullPath), null, CanPaste()));
            menu.Items.Add(MI("Copy path", "", () => CopyPaths(sel.Select(f => f.FullPath)), "Ctrl+Shift+C"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Rename", "", () => BeginRename(first), "F2", sel.Count == 1));
            menu.Items.Add(MI("Delete", "", () => DeleteSelected(false), "Del"));
            menu.Items.Add(new Separator());

            var tags = MI("Tags", "", null);
            foreach (var t in TagPalette.Names)
            {
                var tag = t;
                bool all = sel.All(f => f.Tags.Contains(tag));
                var mi = MI(tag, null, () => SetTag(sel, tag, !all));
                mi.Icon = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = TagPalette.Brush(tag) };
                mi.IsChecked = all;
                tags.Items.Add(mi);
            }
            tags.Items.Add(new Separator());
            tags.Items.Add(MI("Clear tags", "", () => ClearTags(sel), null, sel.Any(f => f.Tags.Count > 0)));
            menu.Items.Add(tags);

            if (sel.Count == 1 && first.IsDir)
            {
                bool marked = S.Bookmarks.Any(b => Same(b, first.FullPath));
                menu.Items.Add(MI(marked ? "Remove bookmark" : "Add bookmark", marked ? "" : "", () => ToggleBookmark(first.FullPath)));
                menu.Items.Add(MI(IsPinned(first.FullPath) ? "Remove from Places" : "Add to Places", "", () => TogglePin(first.FullPath)));
            }
            menu.Items.Add(MI("Add to Dropzone", "", () => AddToDropzone(sel.Select(f => f.FullPath))));
            menu.Items.Add(MI("Show in Explorer", "", () => Shell.ShowInExplorer(first.FullPath)));
            if (sel.Count == 1 && first.IsDir)
                menu.Items.Add(MI("Open in Terminal", "", () => Shell.OpenTerminal(first.FullPath)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Properties", "", ShowProperties, "Alt+Enter"));
        }

        if (anchor != null)
        {
            menu.PlacementTarget = anchor;
            menu.Placement = PlacementMode.Bottom;
        }
        else
        {
            menu.PlacementTarget = list;
            menu.Placement = PlacementMode.MousePoint;
        }
        menu.IsOpen = true;
    }

    // =====================================================================
    // Dropzone
    // =====================================================================
    void LoadDropzone()
    {
        DropItems.Clear();
        foreach (var p in S.Dropzone)
        {
            var f = FileItem.TryFromPath(p);
            if (f != null) DropItems.Add(f);
        }
        S.Dropzone = DropItems.Select(f => f.FullPath).ToList();
    }

    void AddToDropzone(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var p in paths)
        {
            if (S.Dropzone.Any(x => Same(x, p))) continue;
            var f = FileItem.TryFromPath(p);
            if (f == null) continue;
            S.Dropzone.Add(f.FullPath);
            DropItems.Add(f);
            added++;
        }
        if (added > 0) Toast(added == 1 ? "Added to Dropzone" : $"Added {added} items to Dropzone");
        SaveSoon();
    }

    void PruneDropzone()
    {
        foreach (var f in DropItems.ToList())
            if (!File.Exists(f.FullPath) && !Directory.Exists(f.FullPath)) DropItems.Remove(f);
        S.Dropzone = DropItems.Select(f => f.FullPath).ToList();
        SaveSoon();
    }

    void Dz_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (dzDragging || !TryGetDropFiles(e.Data, out _)) { e.Effects = DragDropEffects.None; return; }
        e.Effects = (e.AllowedEffects & DragDropEffects.Link) != 0 ? DragDropEffects.Link : DragDropEffects.Copy;
        DropzoneBorder.BorderBrush = (Brush)FindResource("AccentBrush");
    }

    void Dz_DragLeave(object sender, DragEventArgs e) => DropzoneBorder.BorderBrush = (Brush)FindResource("LineBrush");

    void Dz_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        DropzoneBorder.BorderBrush = (Brush)FindResource("LineBrush");
        if (dzDragging) return;
        if (TryGetDropFiles(e.Data, out var files))
        {
            e.Effects = (e.AllowedEffects & DragDropEffects.Link) != 0 ? DragDropEffects.Link : DragDropEffects.Copy;
            AddToDropzone(files);
        }
    }

    void Dz_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource) != null) return;
        dzItem = ((FrameworkElement)sender).Tag as FileItem;
        dzStart = e.GetPosition(this);
        if (e.ClickCount == 2 && dzItem != null)
        {
            var f = dzItem;
            dzStart = null;
            e.Handled = true;
            if (f.IsDir) Navigate(f.FullPath);
            else if (File.Exists(f.FullPath)) Navigate(SafeParent(f.FullPath), f.FullPath);
            else PruneDropzone();
        }
    }

    void Dz_MouseMove(object sender, MouseEventArgs e) => DzTryDrag(e, dzItem == null ? null : new[] { dzItem.FullPath });

    void DzHeader_MouseDown(object sender, MouseButtonEventArgs e)
    {
        dzItem = null;
        dzStart = e.GetPosition(this);
    }

    void DzHeader_MouseMove(object sender, MouseEventArgs e) =>
        DzTryDrag(e, DropItems.Count == 0 ? null : DropItems.Select(f => f.FullPath).ToArray());

    void DzTryDrag(MouseEventArgs e, string[] paths)
    {
        if (dzStart == null || paths == null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - dzStart.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - dzStart.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dzStart = null;
        dzDragging = true;
        try { DragDrop.DoDragDrop(DropzoneBorder, new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
        catch { }
        finally { dzDragging = false; SetDropTarget(null); }
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, async () => { await Task.Delay(1500); PruneDropzone(); });
    }

    void Dz_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not FileItem f) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        menu.Items.Add(MI("Open", "", () => { if (f.IsDir) Navigate(f.FullPath); else Shell.Open(f.FullPath); }));
        menu.Items.Add(MI("Show in folder", "", () => Navigate(f.IsDir ? f.FullPath : SafeParent(f.FullPath), f.FullPath)));
        menu.Items.Add(MI("Copy here", "", () => FileOps.Copy(new[] { f.FullPath }, current.Path, Hwnd, Same(SafeParent(f.FullPath), current.Path)), null, !IsTag(current?.Path)));
        menu.Items.Add(MI("Move here", "", () => FileOps.Move(new[] { f.FullPath }, current.Path, Hwnd, () => Dispatcher.BeginInvoke(PruneDropzone)), null, !IsTag(current?.Path) && !Same(SafeParent(f.FullPath), current?.Path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Remove from Dropzone", "", () => RemoveDz(f)));
        menu.IsOpen = true;
    }

    void DzRemove_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is FileItem f) RemoveDz(f);
        e.Handled = true;
    }

    void RemoveDz(FileItem f)
    {
        DropItems.Remove(f);
        S.Dropzone.RemoveAll(p => Same(p, f.FullPath));
        SaveSoon();
    }

    void DzClear_Click(object sender, RoutedEventArgs e)
    {
        DropItems.Clear();
        S.Dropzone.Clear();
        SaveSoon();
    }

    // =====================================================================
    // Misc helpers
    // =====================================================================
    public void Toast(string msg)
    {
        ToastText.Text = msg;
        ToastBorder.Visibility = Visibility.Visible;
        toastTimer.Stop();
        toastTimer.Start();
    }

    void Try(Action a)
    {
        try { a(); }
        catch (Exception ex) { Toast(ex.Message); }
    }

    static FileItem ItemFromSource(ItemsControl list, object src)
    {
        var d = src as DependencyObject;
        while (d != null && d is not Visual && d is not Visual3D) d = LogicalTreeHelper.GetParent(d);
        if (d == null || list == null) return null;
        try
        {
            var c = ItemsControl.ContainerFromElement(list, d) as ListBoxItem;
            return c?.DataContext as FileItem;
        }
        catch { return null; }
    }

    static T FindAncestor<T>(object src) where T : DependencyObject
    {
        var d = src as DependencyObject;
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    static T FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) return null;
        int n = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(parent, i);
            if (c is T t) return t;
            var r = FindChild<T>(c);
            if (r != null) return r;
        }
        return null;
    }
}
