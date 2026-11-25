using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Orbit;

/// <summary>Windows 11-style command bar: New · Cut Copy Paste Rename Share Delete · Sort View Filter · More.</summary>
public partial class MainWindow
{
    // ---------- filters ----------
    string filterKind;   // null = all
    string filterDate;   // null = any time

    bool FiltersActive => filterKind != null || filterDate != null;

    static readonly (string Id, string Label, string Glyph, string[] Exts)[] Kinds =
    {
        ("folders", "Folders", "", null),
        ("documents", "Documents", "", new[] { ".pdf", ".doc", ".docx", ".odt", ".rtf", ".txt", ".md", ".xls", ".xlsx", ".csv", ".ods", ".ppt", ".pptx", ".odp", ".epub", ".pages", ".numbers", ".key" }),
        ("images", "Pictures", "", new[] { ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".avif", ".tif", ".tiff", ".ico", ".svg", ".psd", ".raw", ".cr2", ".nef", ".arw", ".dng" }),
        ("videos", "Videos", "", new[] { ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp" }),
        ("music", "Music", "", new[] { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aiff", ".mid" }),
        ("archives", "Archives", "", new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".iso", ".cab" }),
        ("code", "Code", "", new[] { ".cs", ".js", ".mjs", ".ts", ".tsx", ".jsx", ".py", ".java", ".kt", ".cpp", ".cc", ".c", ".h", ".hpp", ".go", ".rs", ".rb", ".php", ".swift", ".dart", ".lua", ".html", ".htm", ".css", ".scss", ".json", ".xml", ".xaml", ".yaml", ".yml", ".toml", ".sh", ".ps1", ".bat", ".cmd", ".sql", ".vue", ".svelte", ".csproj", ".sln" }),
        ("apps", "Apps and shortcuts", "", new[] { ".exe", ".msi", ".lnk", ".url", ".appx", ".msix", ".appinstaller" }),
    };

    static readonly (string Id, string Label)[] DateFilters =
    {
        ("today", "Today"), ("yesterday", "Yesterday"), ("week", "Last 7 days"), ("month", "Last 30 days"), ("year", "This year"), ("older", "Older than a year"),
    };

    bool PassesFilters(FileItem f)
    {
        if (filterKind != null)
        {
            if (filterKind == "folders") { if (!f.IsDir) return false; }
            else
            {
                if (f.IsDir) return false;
                var exts = Kinds.First(k => k.Id == filterKind).Exts;
                if (!exts.Contains(f.Ext, StringComparer.OrdinalIgnoreCase)) return false;
            }
        }
        if (filterDate != null)
        {
            var d = f.Modified;
            var today = DateTime.Today;
            bool ok = filterDate switch
            {
                "today" => d >= today,
                "yesterday" => d >= today.AddDays(-1) && d < today,
                "week" => d >= today.AddDays(-6),
                "month" => d >= today.AddDays(-29),
                "year" => d.Year == today.Year,
                "older" => d < today.AddYears(-1),
                _ => true
            };
            if (!ok) return false;
        }
        return true;
    }

    void SetFilter(string kind, string date)
    {
        filterKind = kind;
        filterDate = date;
        view?.Refresh(); AfterViewChanged();
        UpdateStatus();
        UpdateFilterButton();
    }

    void ResetFilters()
    {
        if (!FiltersActive) return;
        filterKind = filterDate = null;
        view?.Refresh(); AfterViewChanged();
        UpdateFilterButton();
    }

    void UpdateFilterButton()
    {
        var parts = new List<string>();
        if (filterKind != null) parts.Add(Kinds.First(k => k.Id == filterKind).Label);
        if (filterDate != null) parts.Add(DateFilters.First(d => d.Id == filterDate).Label);
        CmdFilterText.Text = parts.Count == 0 ? "Filter" : string.Join(" · ", parts);
        if (parts.Count == 0) CmdFilter.ClearValue(ForegroundProperty);
        else CmdFilter.SetResourceReference(ForegroundProperty, "AccentBrush");
    }

    // ---------- enabled state ----------
    void UpdateCommandBar()
    {
        if (CmdCut == null || current == null) return;
        var sel = ActiveList.SelectedItems;
        int n = sel.Count;
        bool tag = IsTag(current.Path), remote = IsRemote(current.Path);
        bool anyRemote = false;
        foreach (FileItem f in sel) if (f.IsRemote) { anyRemote = true; break; }

        CmdNew.IsEnabled = !tag;
        CmdCut.IsEnabled = n > 0 && !anyRemote;
        CmdCopy.IsEnabled = n > 0;
        CmdPaste.IsEnabled = !tag && clipboardHasFiles;
        CmdRename.IsEnabled = n == 1;
        CmdShare.IsEnabled = n > 0 && !anyRemote;
        CmdDelete.IsEnabled = n > 0;
        CmdProps.IsEnabled = !tag || n > 0;
        CmdShare.ToolTip = anyRemote ? "Download server files first to share them" : "Share";
        CmdViewGlyph.Text = ViewGlyph(S.ViewMode);
        _ = remote;
    }

    // ---------- clipboard watcher (keeps Paste enabled/disabled correctly) ----------
    bool clipboardHasFiles;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool AddClipboardFormatListener(IntPtr hwnd);

    void InitClipboardListener()
    {
        try
        {
            var src = HwndSource.FromHwnd(Hwnd);
            src?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == 0x031D) // WM_CLIPBOARDUPDATE
                    Dispatcher.BeginInvoke(RefreshClipboardState);
                return IntPtr.Zero;
            });
            AddClipboardFormatListener(Hwnd);
        }
        catch { }
        RefreshClipboardState();
    }

    void RefreshClipboardState()
    {
        clipboardHasFiles = CanPaste();
        UpdateCommandBar();
    }

    // ---------- buttons ----------
    void CmdNew_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        menu.Items.Add(MI("Folder", "", NewFolder, "Ctrl+Shift+N"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Text document", "", () => CreateNew(false, "New Text Document", ".txt")));
        menu.Items.Add(MI("Markdown document", "", () => CreateNew(false, "New Document", ".md")));
        menu.Items.Add(MI("JSON file", "", () => CreateNew(false, "new", ".json")));
        menu.Items.Add(MI("HTML page", "", () => CreateNew(false, "index", ".html")));
        menu.Items.Add(MI("PowerShell script", "", () => CreateNew(false, "script", ".ps1")));
        menu.IsOpen = true;
    }

    void CmdCut_Click(object sender, RoutedEventArgs e) => CopyToClipboard(true);
    void CmdCopy_Click(object sender, RoutedEventArgs e) => CopyToClipboard(false);
    void CmdPaste_Click(object sender, RoutedEventArgs e) => Paste(current?.Path);
    void CmdRename_Click(object sender, RoutedEventArgs e) => BeginRenameSelected();
    void CmdDelete_Click(object sender, RoutedEventArgs e) => DeleteSelected((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0);
    void CmdShare_Click(object sender, RoutedEventArgs e) => ShareSelected();
    void CmdProps_Click(object sender, RoutedEventArgs e) => ShowProperties();

    void CmdSort_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
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

    void CmdView_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        foreach (var (id, label, glyph) in ViewModes)
        {
            var mode = id;
            var shortcut = MainWindow.PrettyGesture(GestureFor(mode + "View"));
            var mi = MI(label + (mode switch { "grid" => " (thumbnails, colour tags)", "details" => " (metadata, sorting)", "columns" => " (Miller columns)", _ => " (folder hierarchy)" }),
                        glyph, () => { SetView(mode); UpdateCommandBar(); }, shortcut == "Not set" ? null : shortcut);
            mi.IsChecked = S.ViewMode == mode;
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var hidden = MI("Hidden items", "", ToggleHidden, "Ctrl+H");
        hidden.IsChecked = S.ShowHidden;
        menu.Items.Add(hidden);
        if (IconPack.IsActive)
        {
            var thumbs = MI("Photo and video thumbnails", "", () => SetPackThumbnails(!S.PackThumbnails));
            thumbs.IsChecked = S.PackThumbnails;
            menu.Items.Add(thumbs);
        }
        var side = MI("Sidebar", "", () => ToggleSidebar_Click(null, null));
        side.IsChecked = !S.SidebarCollapsed;
        menu.Items.Add(side);
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Preview selected", "", TogglePreview, "Space", ActiveList.SelectedItems.Count > 0));
        menu.Items.Add(MI("Icons and theme…", "", () => OpenSettings(null)));
        menu.IsOpen = true;
    }

    void CmdFilter_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        var kind = MI("Kind", "", null);
        foreach (var k in Kinds)
        {
            var id = k.Id;
            var mi = MI(k.Label, k.Glyph, () => SetFilter(filterKind == id ? null : id, filterDate));
            mi.IsChecked = filterKind == id;
            kind.Items.Add(mi);
        }
        menu.Items.Add(kind);
        var date = MI("Date modified", "", null);
        foreach (var d in DateFilters)
        {
            var id = d.Id;
            var mi = MI(d.Label, null, () => SetFilter(filterKind, filterDate == id ? null : id));
            mi.IsChecked = filterDate == id;
            date.Items.Add(mi);
        }
        menu.Items.Add(date);
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Filter by name…", "", OpenSearch, "Ctrl+F"));
        menu.Items.Add(MI("Clear filters", "", () => SetFilter(null, null), null, FiltersActive));
        menu.IsOpen = true;
    }

    void CmdMore_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        var list = ActiveList;
        bool local = current != null && !IsTag(current.Path) && !IsRemote(current.Path);
        menu.Items.Add(MI("Select all", "", () => list.SelectAll(), "Ctrl+A"));
        menu.Items.Add(MI("Select none", "", () => list.UnselectAll(), "Esc"));
        menu.Items.Add(MI("Invert selection", "", () => InvertSelection(list)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Copy path", "", () => CopyPaths(Selected().Select(f => f.FullPath).DefaultIfEmpty(current?.Path)), "Ctrl+Shift+C"));
        menu.Items.Add(MI("Add to Dropzone", "", () => AddToDropzone(Selected().Select(f => f.FullPath)), null, Selected().Count > 0 && local));
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Open in Terminal", "", () => Shell.OpenTerminal(current.Path), null, local));
        menu.Items.Add(MI("Open in File Explorer", "", () => Shell.OpenInExplorer(current.Path), null, local));
        menu.Items.Add(MI("Properties", "", ShowProperties, "Alt+Enter", local || Selected().Any(f => !f.IsRemote)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Settings", "", () => OpenSettings(null), "Ctrl+,"));
        menu.IsOpen = true;
    }

    static void InvertSelection(ListBox list)
    {
        var selected = new HashSet<object>(list.SelectedItems.Cast<object>());
        list.SelectedItems.Clear();
        foreach (var item in list.Items)
            if (!selected.Contains(item)) list.SelectedItems.Add(item);
    }

    // ---------- Share (Windows share sheet) ----------
    List<string> sharePaths;
    bool shareHooked;

    void ShareSelected()
    {
        var paths = Selected().Where(f => !f.IsRemote).Select(f => f.FullPath).ToList();
        if (paths.Count == 0) return;
        try
        {
            var dtm = DataTransferManagerInterop.GetForWindow(Hwnd);
            if (!shareHooked)
            {
                dtm.DataRequested += OnShareRequested;
                shareHooked = true;
            }
            sharePaths = paths;
            DataTransferManagerInterop.ShowShareUIForWindow(Hwnd);
        }
        catch (Exception ex)
        {
            AppSettings.LogError(ex);
            Toast("Couldn't open the Share window: " + ex.Message);
        }
    }

    async void OnShareRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        var request = args.Request;
        var deferral = request.GetDeferral();
        try
        {
            var paths = sharePaths ?? new List<string>();
            var storage = new List<IStorageItem>();
            foreach (var p in paths)
            {
                if (Directory.Exists(p)) storage.Add(await StorageFolder.GetFolderFromPathAsync(p));
                else if (File.Exists(p)) storage.Add(await StorageFile.GetFileFromPathAsync(p));
            }
            if (storage.Count == 0) { request.FailWithDisplayText("Nothing to share."); return; }
            request.Data.Properties.Title = storage.Count == 1 ? storage[0].Name : $"{storage.Count} items";
            request.Data.Properties.Description = "Shared from Orbit";
            request.Data.SetStorageItems(storage);
        }
        catch (Exception ex)
        {
            request.FailWithDisplayText(ex.Message);
        }
        finally
        {
            deferral.Complete();
        }
    }
}
