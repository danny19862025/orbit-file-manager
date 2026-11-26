using System.Windows;

namespace Orbit;

/// <summary>The Settings window talks to the main window through these methods.</summary>
public partial class MainWindow
{
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
        settingsWindow.Show(); // modeless: changes preview live in the main window
    }

    internal void SetTheme(string name) => ApplyTheme(name, S.Translucent);
    internal void SetTranslucent(bool on) => ApplyTheme(S.Theme, on);

    /// <summary>Live while the slider moves: only the colours change, so the window doesn't flicker.</summary>
    internal void SetTransparency(int percent)
    {
        S.Transparency = System.Math.Clamp(percent, 5, 90);
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
