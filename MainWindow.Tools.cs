using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Orbit;

/// <summary>Entry points for the extra tools: disk space map, convert, locked folders.</summary>
public partial class MainWindow
{
    internal static string PendingSpacePath;

    internal void OpenSpace(string path)
    {
        if (string.IsNullOrEmpty(path) || IsTag(path) || IsRemote(path) || Archives.IsArc(path)) path = Home;
        if (!SafeDirExists(path)) { Toast("That folder isn't available"); return; }
        new SpaceWindow(this, path).Show();
    }

    /// <summary>Navigate to a path from another window: folders open, files are selected in their folder.</summary>
    internal void ShowInOrbit(string path)
    {
        if (Directory.Exists(path)) Navigate(path);
        else if (File.Exists(path)) Navigate(Path.GetDirectoryName(path), path);
    }

    internal void ToastFromSpace(string msg) => Toast(msg);

    /// <summary>Tools section of the ⋯ menu.</summary>
    void AddToolItems(ContextMenu menu)
    {
        bool local = current != null && !IsTag(current.Path) && !IsRemote(current.Path) && !Archives.IsArc(current.Path);
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Analyze disk space…", "", () => OpenSpace(current?.Path), null, local));
    }

    void AddDriveMenuItems(ContextMenu menu, SideItem s)
    {
        if (s.Kind != "drive") return;
        menu.Items.Add(MI("Analyze disk space", "", () => OpenSpace(s.Path)));
        if (s.Glyph == "" || s.Glyph == "")
            menu.Items.Add(MI("Eject", "", () => EjectDrive(s.Path)));
    }
}
