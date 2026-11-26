using System;
using System.Collections.Generic;
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

    internal static string PendingConvertPath;

    /// <summary>Tools section of the ⋯ menu.</summary>
    void AddToolItems(ContextMenu menu)
    {
        bool local = current != null && !IsTag(current.Path) && !IsRemote(current.Path) && !Archives.IsArc(current.Path);
        var sel = Selected().Where(f => !f.IsRemote && !f.InArchive).ToList();
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Analyze disk space…", "", () => OpenSpace(current?.Path), null, local));
        menu.Items.Add(MI("Convert selected…", "", () => OpenConvert(sel), null, ConvertibleGroup(sel).Count > 0));
    }

    /// <summary>The selected files that can be converted together (same kind as the first convertible one).</summary>
    static List<string> ConvertibleGroup(IEnumerable<FileItem> sel)
    {
        var files = sel.Where(f => !f.IsDir && !f.IsRemote && !f.InArchive).Select(f => f.FullPath).ToList();
        var kind = files.Select(Converter.KindOf).FirstOrDefault(k => k != null);
        return kind == null ? new List<string>() : files.Where(p => Converter.KindOf(p) == kind).ToList();
    }

    internal void OpenConvert(IEnumerable<FileItem> sel) => OpenConvertPaths(ConvertibleGroup(sel));

    internal void OpenConvertPaths(List<string> paths)
    {
        if (paths.Count == 0) { Toast("Select pictures, videos or audio files to convert"); return; }
        new ConvertWindow(this, paths).Show();
    }

    /// <summary>File tools for the right-click menu (local files).</summary>
    void AddFileToolItems(ContextMenu menu, List<FileItem> localFiles)
    {
        var group = ConvertibleGroup(localFiles);
        if (group.Count > 0)
            menu.Items.Add(MI(group.Count == 1 ? "Convert…" : $"Convert {group.Count} files…", "", () => OpenConvertPaths(group)));
    }

    void AddDriveMenuItems(ContextMenu menu, SideItem s)
    {
        if (s.Kind != "drive") return;
        menu.Items.Add(MI("Analyze disk space", "", () => OpenSpace(s.Path)));
        if (s.Glyph == "" || s.Glyph == "")
            menu.Items.Add(MI("Eject", "", () => EjectDrive(s.Path)));
    }
}
