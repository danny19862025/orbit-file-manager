using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using IOPath = System.IO.Path;

namespace Orbit;

/// <summary>Archive browsing (open ZIP/RAR/7z/TAR like folders), extraction, compression and ISO mounting.</summary>
public partial class MainWindow
{
    const string ReadOnlyArchive = "Archives are read-only here. Extract first to change their contents.";

    static string ArchiveError(Exception ex) => ex switch
    {
        InvalidOperationException ioe when ioe.Message.Contains("password") => ioe.Message,
        System.Security.Cryptography.CryptographicException => "This archive is password-protected, which Orbit can't open yet.",
        FileNotFoundException => "The archive no longer exists.",
        IOException io => "Couldn't read the archive: " + io.Message,
        _ => "Couldn't read the archive: " + ex.Message
    };

    // ---------- breadcrumbs: folders → archive → folders inside ----------
    IEnumerable<Crumb> ArchiveCrumbs(string url)
    {
        var (archive, inner) = Archives.Parse(url);
        var folder = IOPath.GetDirectoryName(archive) ?? "";
        var root = IOPath.GetPathRoot(folder) ?? folder;
        var parts = new List<(string name, string path)> { (root.TrimEnd('\\'), root) };
        string acc = root;
        foreach (var seg in folder.Substring(Math.Min(root.Length, folder.Length)).Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            acc = IOPath.Combine(acc, seg);
            parts.Add((seg, acc));
        }
        parts.Add(("📦 " + IOPath.GetFileName(archive), Archives.Make(archive, "")));
        string innerAcc = "";
        foreach (var seg in inner.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            innerAcc = innerAcc.Length == 0 ? seg : innerAcc + "/" + seg;
            parts.Add((seg, Archives.Make(archive, innerAcc)));
        }
        for (int i = 0; i < parts.Count; i++)
            yield return new Crumb { Name = parts[i].name, Path = parts[i].path, IsLast = i == parts.Count - 1 };
    }

    /// <summary>Folder to select when going up from inside an archive's root.</summary>
    static string ArchiveSelectOnUp(string url)
    {
        var (archive, inner) = Archives.Parse(url);
        return inner.Length == 0 ? archive : url;
    }

    // ---------- opening ----------
    async void OpenArchivedFile(FileItem f)
    {
        Toast($"Extracting “{f.Name}”…");
        try
        {
            var paths = await Task.Run(() => Archives.ExtractToTemp(new[] { f.FullPath }));
            if (paths.Count > 0) Try(() => Shell.Open(paths[0]));
        }
        catch (Exception ex) { Toast(ArchiveError(ex)); }
    }

    async void MountIso(string path)
    {
        Toast($"Mounting “{IOPath.GetFileName(path)}”…");
        string letter = null;
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"(Mount-DiskImage -ImagePath '{path.Replace("'", "''")}' -PassThru | Get-Volume).DriveLetter\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            letter = output.Trim().Split('\n').LastOrDefault()?.Trim();
        }
        catch { }
        if (!string.IsNullOrEmpty(letter) && letter.Length == 1)
        {
            RefreshDrives();
            Navigate(letter + ":\\");
            Toast($"Mounted as {letter}: — right-click the drive in the sidebar to eject it");
        }
        else Try(() => Shell.Open(path)); // let Windows handle it
    }

    async void EjectDrive(string root)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"Get-Volume -DriveLetter {root[0]} | Get-DiskImage | Dismount-DiskImage | Out-Null\"")
            { UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
        }
        catch { }
        if (current?.Path != null && current.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) Navigate(Home);
        RefreshDrives();
        Toast($"Ejected {root.TrimEnd('\\')}");
    }

    // ---------- drag files out of an archive (extracts first) ----------
    async void StartArchiveDrag(ListBox list, List<FileItem> sel)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        List<string> paths;
        try { paths = await Task.Run(() => Archives.ExtractToTemp(sel.Select(f => f.FullPath))); }
        catch (Exception ex) { Mouse.OverrideCursor = null; Toast(ArchiveError(ex)); return; }
        finally { Mouse.OverrideCursor = null; }
        if (paths.Count == 0) return;
        if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            Toast("Extracted — drag again to drop the files");
            return;
        }
        try { DragDrop.DoDragDrop(list, new DataObject(DataFormats.FileDrop, paths.ToArray()), DragDropEffects.Copy); }
        catch { }
        finally { SetDropTarget(null); ShowDropFeedback(null, DragDropEffects.None, null); }
    }

    async void CopyArchivedToClipboard(List<FileItem> sel)
    {
        Toast("Extracting so you can paste it anywhere…");
        try
        {
            var paths = await Task.Run(() => Archives.ExtractToTemp(sel.Select(f => f.FullPath)));
            var sc = new StringCollection();
            sc.AddRange(paths.ToArray());
            if (TryClipboard(() => Clipboard.SetFileDropList(sc)))
                Toast(paths.Count == 1 ? $"Copied “{sel[0].Name}”" : $"Copied {paths.Count} items");
        }
        catch (Exception ex) { Toast(ArchiveError(ex)); }
    }

    // ---------- extract / compress ----------
    async void ExtractItemsTo(List<FileItem> sel, string destDir)
    {
        Toast(sel.Count == 1 ? $"Extracting “{sel[0].Name}”…" : $"Extracting {sel.Count} items…");
        try
        {
            var (archive, _) = Archives.Parse(sel[0].FullPath);
            var made = await Task.Run(() => Archives.Extract(archive, sel.Select(f => Archives.Parse(f.FullPath).inner), destDir));
            Toast($"Extracted to {Display(destDir)}");
            Navigate(destDir, made.FirstOrDefault());
        }
        catch (Exception ex) { Toast(ArchiveError(ex)); }
    }

    /// <summary>Extract whole archive files: into a new folder named after each (default) or straight into the folder.</summary>
    async void ExtractArchiveFiles(List<FileItem> archives, bool intoOwnFolder)
    {
        string last = null;
        foreach (var f in archives)
        {
            var dir = IOPath.GetDirectoryName(f.FullPath)!;
            var dest = dir;
            if (intoOwnFolder)
            {
                var baseName = f.Name;
                foreach (var ext in new[] { ".tar.gz", ".tar.bz2", ".tar.xz" })
                    if (baseName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { baseName = baseName[..^ext.Length]; break; }
                if (baseName == f.Name) baseName = IOPath.GetFileNameWithoutExtension(f.Name);
                dest = IOPath.Combine(dir, UniqueName(dir, baseName, ""));
            }
            Toast($"Extracting “{f.Name}”…");
            try
            {
                var path = f.FullPath;
                await Task.Run(() => { Directory.CreateDirectory(dest); Archives.Extract(path, new[] { "" }, dest); });
                last = dest;
            }
            catch (Exception ex) { Toast(ArchiveError(ex)); return; }
        }
        if (last != null) Toast(archives.Count == 1 ? $"Extracted to “{Display(last)}”" : $"Extracted {archives.Count} archives");
        if (last != null && intoOwnFolder && Same(IOPath.GetDirectoryName(last), current?.Path)) LoadCurrent(last);
    }

    async void CompressSelection(List<FileItem> sel)
    {
        if (sel.Count == 0 || current == null) return;
        var dir = IOPath.GetDirectoryName(sel[0].FullPath)!;
        var baseName = sel.Count == 1 ? (sel[0].IsDir ? sel[0].Name : IOPath.GetFileNameWithoutExtension(sel[0].Name)) : "Archive";
        var zip = IOPath.Combine(dir, UniqueName(dir, baseName, ".zip"));
        Toast($"Compressing into “{IOPath.GetFileName(zip)}”…");
        try
        {
            var paths = sel.Select(f => f.FullPath).ToList();
            await Task.Run(() => Archives.CompressToZip(paths, zip));
            long before = sel.Where(f => !f.IsDir).Sum(f => Math.Max(0, f.Size));
            long after = new FileInfo(zip).Length;
            Toast($"Created “{IOPath.GetFileName(zip)}” · {Fmt.Size(after)}" + (before > after && before > 0 ? $" ({100 - after * 100 / before}% smaller)" : ""));
            if (Same(dir, current.Path)) LoadCurrent(zip);
        }
        catch (Exception ex)
        {
            try { File.Delete(zip); } catch { }
            Toast("Couldn't compress: " + ex.Message);
        }
    }

    void ShowArchiveSubfolders(UIElement anchor, string url)
    {
        List<FileItem> kids;
        try { kids = Archives.List(url); }
        catch (Exception ex) { Toast(ArchiveError(ex)); return; }
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom, MaxHeight = 520 };
        var dirs = kids.Where(k => k.IsDir).OrderBy(k => k.Name, StringComparer.CurrentCultureIgnoreCase).Take(400).ToList();
        if (dirs.Count == 0) menu.Items.Add(MI("No subfolders", null, null, enabled: false));
        foreach (var d in dirs)
        {
            var target = d.FullPath;
            menu.Items.Add(MI(d.Name, "", () => Navigate(target)));
        }
        menu.IsOpen = true;
    }

    // ---------- menus ----------
    void AddArchiveItemsToMenu(ContextMenu menu, List<FileItem> sel)
    {
        var localFiles = sel.Where(f => !f.IsRemote && !f.InArchive).ToList();
        if (localFiles.Count == 0) return;
        var archives = localFiles.Where(f => !f.IsDir && Archives.IsArchiveFile(f.FullPath)).ToList();
        if (archives.Count == localFiles.Count)
        {
            if (archives.Count == 1) menu.Items.Add(MI("Open archive", "", () => Navigate(Archives.Make(archives[0].FullPath, ""))));
            var name = archives.Count == 1 ? $"Extract to “{IOPath.GetFileNameWithoutExtension(archives[0].Name)}\\”" : "Extract each to its own folder";
            menu.Items.Add(MI(name, "", () => ExtractArchiveFiles(archives, true)));
            menu.Items.Add(MI("Extract here", "", () => ExtractArchiveFiles(archives, false)));
        }
        if (localFiles.Count == 1 && Archives.IsIso(localFiles[0].FullPath))
            menu.Items.Add(MI("Mount", "", () => MountIso(localFiles[0].FullPath)));
        menu.Items.Add(MI("Compress to ZIP", "", () => CompressSelection(localFiles)));
        AddFileToolItems(menu, localFiles);
    }

    void ShowArchiveMenu(ListBox list, UIElement anchor, List<FileItem> sel)
    {
        var menu = new ContextMenu();
        var here = current.Path;
        var (archive, _) = Archives.Parse(here);
        if (sel.Count == 0)
        {
            var (files, size, packed) = Archives.Totals(here);
            menu.Items.Add(MI($"{files:N0} files · {Fmt.Size(size)} ({Fmt.Size(packed)} packed)", "", null, null, false));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Extract everything here…", "", () => ExtractItemsTo(list.Items.Cast<FileItem>().ToList(), IOPath.GetDirectoryName(archive)!)));
            menu.Items.Add(MI("Extract everything to…", "", () =>
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Extract to" };
                if (dlg.ShowDialog(this) == true) ExtractItemsTo(list.Items.Cast<FileItem>().ToList(), dlg.FolderName);
            }));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Show archive in its folder", "", () => Navigate(IOPath.GetDirectoryName(archive), archive)));
            menu.Items.Add(MI("Select all", "", () => ActiveList.SelectAll(), "Ctrl+A"));
        }
        else
        {
            var first = sel[0];
            menu.Items.Add(MI("Open", "", () => OpenItems(sel), "Enter"));
            menu.Items.Add(MI("Preview", "", TogglePreview, "Space", sel.Count == 1));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Extract to the archive's folder", "", () => ExtractItemsTo(sel, IOPath.GetDirectoryName(archive)!)));
            menu.Items.Add(MI("Extract to…", "", () =>
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Extract to" };
                if (dlg.ShowDialog(this) == true) ExtractItemsTo(sel, dlg.FolderName);
            }));
            menu.Items.Add(MI("Copy", "", () => CopyArchivedToClipboard(sel), "Ctrl+C"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Properties", "", ShowProperties, "Alt+Enter"));
            _ = first;
        }
        if (anchor != null) { menu.PlacementTarget = anchor; menu.Placement = PlacementMode.Bottom; }
        else { menu.PlacementTarget = list; menu.Placement = PlacementMode.MousePoint; }
        menu.IsOpen = true;
    }
}
