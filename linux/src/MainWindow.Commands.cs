using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

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
        ("images", "Pictures", "", new[] { ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".avif", ".tif", ".tiff", ".ico", ".svg", ".psd", ".raw", ".cr2", ".nef", ".arw", ".dng", ".xcf", ".kra" }),
        ("videos", "Videos", "", new[] { ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".3gp", ".ogv" }),
        ("music", "Music", "", new[] { ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aiff", ".mid" }),
        ("archives", "Archives", "", new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".iso", ".cab", ".deb", ".rpm" }),
        ("code", "Code", "", new[] { ".cs", ".js", ".mjs", ".ts", ".tsx", ".jsx", ".py", ".java", ".kt", ".cpp", ".cc", ".c", ".h", ".hpp", ".go", ".rs", ".rb", ".php", ".swift", ".dart", ".lua", ".html", ".htm", ".css", ".scss", ".json", ".xml", ".xaml", ".yaml", ".yml", ".toml", ".sh", ".ps1", ".bat", ".cmd", ".sql", ".vue", ".svelte", ".csproj", ".sln" }),
        ("apps", "Apps and shortcuts", "", new[] { ".desktop", ".appimage", ".flatpakref", ".run", ".sh", ".exe", ".msi" }),
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
        RefreshView();
        UpdateStatus();
        UpdateFilterButton();
    }

    void ResetFilters()
    {
        if (!FiltersActive) return;
        filterKind = filterDate = null;
        RefreshView();
        UpdateFilterButton();
    }

    void UpdateFilterButton()
    {
        var parts = new List<string>();
        if (filterKind != null) parts.Add(Kinds.First(k => k.Id == filterKind).Label);
        if (filterDate != null) parts.Add(DateFilters.First(d => d.Id == filterDate).Label);
        CmdFilterText.Text = parts.Count == 0 ? "Filter" : string.Join(" · ", parts);
        if (parts.Count == 0) CmdFilter.Bind(ForegroundProperty, CmdFilter.GetResourceObservable("TextBrush"));
        else CmdFilter.Bind(ForegroundProperty, CmdFilter.GetResourceObservable("AccentBrush"));
    }

    // ---------- enabled state ----------
    void UpdateCommandBar()
    {
        if (CmdCut == null || current == null) return;
        var sel = ActiveList.SelectedItems;
        int n = sel?.Count ?? 0;
        bool tag = IsTag(current.Path);
        bool anyRemote = false;
        if (sel != null) foreach (FileItem f in sel) if (f.IsRemote) { anyRemote = true; break; }

        CmdNew.IsEnabled = !tag;
        CmdCut.IsEnabled = n > 0 && !anyRemote;
        CmdCopy.IsEnabled = n > 0;
        CmdPaste.IsEnabled = !tag && clipboardHasFiles;
        CmdRename.IsEnabled = n == 1;
        CmdShare.IsEnabled = n > 0 && !anyRemote;
        CmdDelete.IsEnabled = n > 0;
        CmdProps.IsEnabled = !tag || n > 0;
        ToolTip.SetTip(CmdShare, anyRemote ? "Download server files first to share them" : "Share");
        CmdViewGlyph.Text = ViewGlyph(S.ViewMode);
    }

    // ---------- clipboard watcher (keeps Paste enabled/disabled correctly) ----------
    bool clipboardHasFiles;
    DispatcherTimer clipTimer;

    void StartClipboardWatch()
    {
        // X11 has no "clipboard changed" event that reaches Avalonia, so look again while Orbit is in front
        clipTimer = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) => { if (IsActive) RefreshClipboardState(); });
        Activated += (_, _) => RefreshClipboardState();
        RefreshClipboardState();
    }

    bool clipBusy;

    async void RefreshClipboardState()
    {
        if (clipBusy) return;
        clipBusy = true;
        try
        {
            bool has = await CanPaste();
            if (has != clipboardHasFiles) { clipboardHasFiles = has; UpdateCommandBar(); }
        }
        finally { clipBusy = false; }
    }

    // ---------- buttons ----------
    void CmdNew_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MI("Folder", "", NewFolder, "Ctrl+Shift+N"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Text document", "", () => CreateNew(false, "New Text Document", ".txt")));
        menu.Items.Add(MI("Markdown document", "", () => CreateNew(false, "New Document", ".md")));
        menu.Items.Add(MI("JSON file", "", () => CreateNew(false, "new", ".json")));
        menu.Items.Add(MI("HTML page", "", () => CreateNew(false, "index", ".html")));
        menu.Items.Add(MI("Shell script", "", () => CreateScript()));
        OpenMenu(menu, (Control)sender, true);
    }

    /// <summary>A new shell script that is already executable and starts with a shebang.</summary>
    void CreateScript()
    {
        if (current == null || IsTag(current.Path) || IsRemote(current.Path) || Archives.IsArc(current.Path)) { CreateNew(false, "script", ".sh"); return; }
        var name = UniqueName(current.Path, "script", ".sh");
        var p = Path.Combine(current.Path, name);
        try
        {
            File.WriteAllText(p, "#!/usr/bin/env bash\nset -euo pipefail\n\n");
            File.SetUnixFileMode(p, File.GetUnixFileMode(p) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception ex) { Toast(ex.Message); return; }
        var f = FileItem.TryFromPath(p);
        if (f == null) return;
        if (!index.ContainsKey(p)) { items.Add(f); index[p] = f; InsertSorted(f); }
        ActiveList.SelectedItem = f;
        BeginRename(f);
        UpdateStatus();
    }

    void CmdCut_Click(object sender, RoutedEventArgs e) => CopyToClipboard(true);
    void CmdCopy_Click(object sender, RoutedEventArgs e) => CopyToClipboard(false);
    void CmdPaste_Click(object sender, RoutedEventArgs e) => Paste(current?.Path);
    void CmdRename_Click(object sender, RoutedEventArgs e) => BeginRenameSelected();
    void CmdDelete_Click(object sender, RoutedEventArgs e) => DeleteSelected((mods & Avalonia.Input.KeyModifiers.Shift) != 0);
    void CmdShare_Click(object sender, RoutedEventArgs e) => ShareSelected((Control)sender);
    void CmdProps_Click(object sender, RoutedEventArgs e) => ShowProperties();

    void CmdSort_Click(object sender, RoutedEventArgs e) => OpenMenu(SortMenu(), (Control)sender, true);

    void CmdView_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var (id, label, glyph) in ViewModes)
        {
            var mode = id;
            var shortcut = PrettyGesture(GestureFor(mode + "View"));
            var mi = MI(label + (mode switch { "grid" => " (thumbnails, colour tags)", "details" => " (metadata, sorting)", "columns" => " (Miller columns)", _ => " (folder hierarchy)" }),
                        glyph, () => { SetView(mode); UpdateCommandBar(); }, shortcut == "Not set" ? null : shortcut);
            mi.IsTicked = S.ViewMode == mode;
            menu.Items.Add(mi);
        }
        var split = MI("Split view (second pane)", "", ToggleSplit, PrettyGesture(GestureFor("split")));
        split.IsTicked = SplitOpen;
        split.TipText = "Tip: drag a tab onto the right half of the window to split";
        menu.Items.Add(new Separator());
        menu.Items.Add(split);
        menu.Items.Add(new Separator());
        var hidden = MI("Hidden items", "", ToggleHidden, "Ctrl+H");
        hidden.IsTicked = S.ShowHidden;
        menu.Items.Add(hidden);
        if (IconPack.IsActive)
        {
            var thumbs = MI("Photo and video thumbnails", "", () => SetPackThumbnails(!S.PackThumbnails));
            thumbs.IsTicked = S.PackThumbnails;
            menu.Items.Add(thumbs);
        }
        var side = MI("Sidebar", "", () => ToggleSidebar_Click(null, null));
        side.IsTicked = !S.SidebarCollapsed;
        menu.Items.Add(side);
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Preview selected", "", TogglePreview, "Space", ActiveList.SelectedItems?.Count > 0));
        menu.Items.Add(MI("Icons and theme…", "", () => OpenSettings(null)));
        OpenMenu(menu, (Control)sender, true);
    }

    void CmdFilter_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var kind = MI("Kind", "", null);
        foreach (var k in Kinds)
        {
            var id = k.Id;
            var mi = MI(k.Label, k.Glyph, () => SetFilter(filterKind == id ? null : id, filterDate));
            mi.IsTicked = filterKind == id;
            kind.Items.Add(mi);
        }
        menu.Items.Add(kind);
        var date = MI("Date modified", "", null);
        foreach (var d in DateFilters)
        {
            var id = d.Id;
            var mi = MI(d.Label, null, () => SetFilter(filterKind, filterDate == id ? null : id));
            mi.IsTicked = filterDate == id;
            date.Items.Add(mi);
        }
        menu.Items.Add(date);
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Filter by name…", "", OpenSearch, "Ctrl+F"));
        menu.Items.Add(MI("Clear filters", "", () => SetFilter(null, null), null, FiltersActive));
        OpenMenu(menu, (Control)sender, true);
    }

    void CmdMore_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
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
        menu.Items.Add(MI("Open in File Manager", "", () => Shell.OpenInFileManager(current.Path), null, local));
        AddToolItems(menu);
        menu.Items.Add(MI("Properties", "", ShowProperties, "Alt+Enter", local || Selected().Any(f => !f.IsRemote)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Settings", "", () => OpenSettings(null), "Ctrl+,"));
        OpenMenu(menu, (Control)sender, true);
    }

    static void InvertSelection(ListBox list)
    {
        var selected = new HashSet<object>(list.SelectedItems.Cast<object>());
        list.Selection.BeginBatchUpdate();
        list.Selection.Clear();
        for (int i = 0; i < list.ItemCount; i++)
            if (!selected.Contains(list.Items[i])) list.Selection.Select(i);
        list.Selection.EndBatchUpdate();
    }

    // ---------- Share (email, KDE Connect) ----------
    void ShareSelected(Control anchor)
    {
        var paths = Selected().Where(f => !f.IsRemote && !f.InArchive && !f.IsRecycled).Select(f => f.FullPath).ToList();
        if (paths.Count == 0) return;
        var files = paths.Where(File.Exists).ToList();
        var menu = new ContextMenu();
        menu.Items.Add(MI("Send by email…", "", () =>
        {
            var psi = new ProcessStartInfo("xdg-email") { UseShellExecute = false };
            foreach (var f in files) { psi.ArgumentList.Add("--attach"); psi.ArgumentList.Add(f); }
            if (files.Count == 1) { psi.ArgumentList.Add("--subject"); psi.ArgumentList.Add(Path.GetFileName(files[0])); }
            Process.Start(psi);
        }, null, files.Count > 0 && IconService.Which("xdg-email") != null));
        if (IconService.Which("kdeconnect-cli") is { } kdc)
        {
            var devices = KdeConnectDevices(kdc);
            if (devices.Count == 0) menu.Items.Add(MI("Send to a phone (KDE Connect) — no device paired", "", null, null, false));
            foreach (var (id, name) in devices)
                menu.Items.Add(MI($"Send to {name}", "", () =>
                {
                    foreach (var p in paths)
                    {
                        var psi = new ProcessStartInfo(kdc) { UseShellExecute = false };
                        foreach (var a in new[] { "-d", id, "--share", p }) psi.ArgumentList.Add(a);
                        Process.Start(psi);
                    }
                    Toast(paths.Count == 1 ? $"Sending “{Path.GetFileName(paths[0])}” to {name}" : $"Sending {paths.Count} items to {name}");
                }));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Copy as links", "", () => CopyPaths(paths.Select(IconService.FileUri))));
        OpenMenu(menu, anchor, true);
    }

    static List<(string id, string name)> KdeConnectDevices(string kdc)
    {
        var list = new List<(string, string)>();
        try
        {
            var psi = new ProcessStartInfo(kdc) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "--list-available", "--id-name-only" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int sp = line.IndexOf(' ');
                if (sp > 0) list.Add((line[..sp], line[(sp + 1)..].Trim()));
            }
        }
        catch { }
        return list;
    }
}
