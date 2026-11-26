using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Orbit;

/// <summary>Sidebar extras: pin folders to Places by drag &amp; drop, and show/hide whole sections. Also the Settings window's bridge.</summary>
public partial class MainWindow
{
    public static readonly (string Id, string Label)[] SidebarSections =
    {
        ("Bookmarks", "Bookmarks"), ("Tags", "Tags"), ("Drives", "Drives"), ("Network", "Network"), ("Recent", "Recent"), ("Dropzone", "Dropzone"),
    };

    // ---------- section visibility ----------
    void ApplySidebarSections()
    {
        var map = new Dictionary<string, Control>
        {
            ["Bookmarks"] = SecBookmarks, ["Tags"] = SecTags, ["Drives"] = SecDrives,
            ["Network"] = SecNetwork, ["Recent"] = SecRecent, ["Dropzone"] = DropzoneBorder,
        };
        foreach (var kv in map)
            kv.Value.IsVisible = !S.HiddenSections.Contains(kv.Key);
    }

    internal bool IsSectionVisible(string id) => !S.HiddenSections.Contains(id);

    internal void SetSectionVisible(string id, bool visible)
    {
        S.HiddenSections.RemoveAll(x => x == id);
        if (!visible) S.HiddenSections.Add(id);
        ApplySidebarSections();
        SaveSoon();
    }

    // ---------- pinned places ----------
    bool IsPinned(string path) => S.CustomPlaces.Any(p => Same(p, path));

    void RebuildPlaces()
    {
        Places.Clear();
        BuildPlaces();
        MarkActive();
    }

    void TogglePin(string path)
    {
        if (IsPinned(path)) UnpinPlace(path);
        else PinPlace(path);
    }

    void PinPlace(string path)
    {
        if (!SafeDirExists(path) || IsRemote(path) || IsTag(path)) { Toast("Only folders on this computer can be added to Places"); return; }
        path = Norm(path);
        if (IsPinned(path) || Places.Any(p => Same(p.Path, path))) { Toast($"“{Display(path)}” is already in Places"); return; }
        S.CustomPlaces.Add(path);
        RebuildPlaces();
        SaveSoon();
        Toast($"Added “{Display(path)}” to Places");
    }

    void UnpinPlace(string path)
    {
        S.CustomPlaces.RemoveAll(p => Same(p, path));
        RebuildPlaces();
        SaveSoon();
    }

    void MovePinned(string path, int delta)
    {
        int i = S.CustomPlaces.FindIndex(p => Same(p, path));
        int j = i + delta;
        if (i < 0 || j < 0 || j >= S.CustomPlaces.Count) return;
        (S.CustomPlaces[i], S.CustomPlaces[j]) = (S.CustomPlaces[j], S.CustomPlaces[i]);
        RebuildPlaces();
        SaveSoon();
    }

    void BuildPinnedMenu(ContextMenu menu, SideItem s)
    {
        int i = S.CustomPlaces.FindIndex(p => Same(p, s.Path));
        menu.Items.Add(MI("Open", "", () => OpenSide(s, false)));
        menu.Items.Add(MI("Open in new tab", "", () => OpenSide(s, true)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Move up", "", () => MovePinned(s.Path, -1), null, i > 0));
        menu.Items.Add(MI("Move down", "", () => MovePinned(s.Path, 1), null, i >= 0 && i < S.CustomPlaces.Count - 1));
        menu.Items.Add(MI("Copy path", "", () => SetClipboardText(s.Path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Remove from Places", "", () => UnpinPlace(s.Path)));
    }

    // =====================================================================
    // Settings window bridge
    // =====================================================================
    SettingsWindow settingsWindow;

    void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings(null);

    internal void OpenSettings(string page)
    {
        if (settingsWindow != null)
        {
            settingsWindow.Activate();
            return;
        }
        settingsWindow = new SettingsWindow(this, page);
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show(this); // modeless: changes preview live in the main window
    }

    internal void SetTheme(string name) => ApplyTheme(name, S.Translucent);
    internal void SetTranslucent(bool on) => ApplyTheme(S.Theme, on);

    /// <summary>Live while the slider moves: only the colours change, so the window doesn't flicker.</summary>
    internal void SetTransparency(int percent)
    {
        S.Transparency = Math.Clamp(percent, 5, 90);
        if (!S.Translucent) { ApplyTheme(S.Theme, true); return; }
        Themes.Apply(S.Theme, S.Transparency);
        SaveSoon();
    }

    internal void SetShowHidden(bool on)
    {
        HiddenToggle.IsChecked = on;
        HiddenToggle_Click(null, null);
    }

    internal void SetViewMode(string mode) => SetView(mode);

    internal void SetSort(string by, bool desc)
    {
        S.SortBy = by;
        S.SortDesc = desc;
        ApplySort();
    }

    internal void SetIconPack(string id)
    {
        if (S.IconPack == id) return;
        S.IconPack = id;
        RefreshIcons();
    }

    internal void SetPackThumbnails(bool on)
    {
        S.PackThumbnails = on;
        RefreshIcons();
    }

    void RefreshIcons()
    {
        IconService.ClearAll();
        foreach (var f in items) f.ResetIcons();
        foreach (var f in treeRows) f.ResetIcons();
        foreach (var f in DropItems) f.ResetIcons();
        SaveSoon();
    }

    /// <summary>Use a freedesktop icon theme folder. Returns an error message, or null when it worked.</summary>
    internal string SetCustomIconTheme(string dir)
    {
        var old = S.CustomIconTheme;
        S.CustomIconTheme = dir;
        IconPack.Unload(IconPack.Custom);
        if (IconPack.ErrorFor(IconPack.Custom) is { } err)
        {
            S.CustomIconTheme = old;
            IconPack.Unload(IconPack.Custom);
            return err;
        }
        S.IconPack = IconPack.Custom;
        RefreshIcons();
        return null;
    }

    internal void OpenConnectDialog() => ShowConnectDialog(null);
    internal void RedetectNetwork() { RebuildNetwork(); Toast("Cloud folders refreshed"); }
    internal void SaveSettingsSoon() => SaveSoon();
}
