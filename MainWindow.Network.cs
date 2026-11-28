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
using System.Windows.Threading;
using Microsoft.Win32;
using Renci.SshNet.Common;
using IOPath = System.IO.Path;

namespace Orbit;

/// <summary>Network section: SMB / WebDAV (mounted by Windows), SFTP (in-app), and cloud sync folders.</summary>
public partial class MainWindow
{
    readonly Dictionary<string, FileSystemWatcher> editWatchers = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> ignoreEditsUntil = new(StringComparer.OrdinalIgnoreCase);

    // =====================================================================
    // Sidebar
    // =====================================================================
    void RebuildNetwork()
    {
        Network.Clear();
        foreach (var c in CloudFolders.Detect())
        {
            var badge = c.Name.StartsWith("Google") ? "DRIVE" : c.Name.StartsWith("Dropbox") ? "DROPBOX" : c.Name.StartsWith("iCloud") ? "ICLOUD" : "ONEDRIVE";
            Network.Add(new SideItem { Name = c.Name, Path = Norm(c.Path), Glyph = c.Glyph, Kind = "cloud", ToolTipText = c.Path, Badge = badge, IsConnected = true });
        }
        foreach (var s in S.Servers)
        {
            bool cloud = s.Protocol is "gdrive" or "dropbox";
            var mounted = cloud ? CloudMount.MountedPath(s.Host) : null;
            Network.Add(new SideItem
            {
                Name = s.DisplayName,
                Path = cloud ? (mounted != null ? Norm(mounted) : "cloud:" + s.Host) : Norm(s.Location),
                Glyph = s.Glyph,
                Kind = "server",
                Server = s,
                Badge = s.Badge,
                IsConnected = cloud ? mounted != null : s.Protocol == "sftp" && Sftp.IsConnected(s.Location),
                ToolTipText = cloud
                    ? (mounted != null ? $"{s.DisplayName} · mounted as {mounted.TrimEnd('\\')}" : $"{s.DisplayName} · click to sign in and mount")
                    : $"{s.Protocol.ToUpperInvariant()} · {s.Location}"
            });
        }
        Network.Add(new SideItem { Name = "Connect to server…", Path = "connect:", Glyph = "\uE710", Kind = "connect", ToolTipText = "SMB / Samba, SFTP (SSH), WebDAV, Google Drive or Dropbox" });
        MarkActive();
    }

    void BuildServerMenu(ContextMenu menu, SideItem s)
    {
        if (s.Kind == "connect")
        {
            menu.Items.Add(MI("Connect to server…", "\uE710", () => ShowConnectDialog(null)));
            return;
        }
        var e = s.Server;
        menu.Items.Add(MI("Connect", "\uE8E5", () => _ = ConnectServer(e, false, null)));
        menu.Items.Add(MI("Open in new tab", "\uE8A7", () => _ = ConnectServer(e, true, null)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Edit…", "\uE70F", () => ShowConnectDialog(e)));
        if (e.Protocol == "sftp") menu.Items.Add(MI("Open SSH terminal", "\uE756", () => OpenSsh(e.Location)));
        menu.Items.Add(MI("Copy address", "\uE8C8", () => SetClipboardText(e.Location)));
        menu.Items.Add(MI("Disconnect", "\uE711", () => DisconnectServer(e)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Remove from sidebar", "\uE74D", () =>
        {
            S.Servers.Remove(e);
            RebuildNetwork();
            SaveSoon();
        }));
    }

    void ShowConnectDialog(ServerEntry edit)
    {
        var dlg = new ConnectDialog(this, edit?.Clone());
        if (dlg.ShowDialog() != true) return;
        if (dlg.OpenPath != null) { Navigate(dlg.OpenPath); return; }
        if (dlg.Result == null) return;
        var s = dlg.Result;
        bool cloud = s.Protocol is "gdrive" or "dropbox";
        if (edit != null)
        {
            int i = S.Servers.IndexOf(edit);
            if (i >= 0) S.Servers[i] = s; else S.Servers.Add(s);
        }
        else if (dlg.Save && !S.Servers.Any(x => cloud ? x.Protocol == s.Protocol && Same(x.Host, s.Host) : Same(x.Location, s.Location)))
        {
            S.Servers.Add(s);
        }
        RebuildNetwork();
        SaveSoon();
        _ = ConnectServer(s, false, string.IsNullOrEmpty(dlg.Password) ? null : dlg.Password);
    }

    // =====================================================================
    // Connecting
    // =====================================================================
    async Task ConnectServer(ServerEntry s, bool newTab, string password)
    {
        if (s == null) return;
        if (s.Protocol is "gdrive" or "dropbox") { await ConnectCloud(s, newTab); return; }
        Toast($"Connecting to {s.Host}…");
        if (s.Protocol == "sftp")
        {
            var err = await ConnectSftp(s, password);
            if (err == "") return;
            if (err != null) { Toast(err); return; }
            string target = s.Location;
            if (string.IsNullOrWhiteSpace(s.Path))
                try { target = await Task.Run(() => Sftp.HomeOf(s.Location)); } catch { }
            Go(target, newTab);
        }
        else
        {
            var err = await NetMount.Connect(s, Hwnd);
            if (err == "") return;
            if (err != null)
            {
                // A common mix-up: the machine has no Windows file sharing at all, but it does run SSH. Offer to switch.
                if (s.Protocol == "smb" && !await PortOpen(s.Host, 445) && !await PortOpen(s.Host, 139) && await PortOpen(s.Host, 22))
                {
                    var r = MessageBox.Show(this,
                        $"{s.Host} doesn't share files with Windows file sharing (SMB) — but it does accept SSH.\n\n" +
                        "Connect with SFTP (SSH) instead? You'll sign in with your user name and password on that machine.",
                        "Connect to " + s.Host, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
                    if (r == MessageBoxResult.Yes)
                    {
                        s.Protocol = "sftp";
                        s.Port = 0;
                        if (S.Servers.Contains(s)) { SaveSoon(); RebuildNetwork(); }
                        await ConnectServer(s, newTab, password);
                    }
                    return;
                }
                Toast("Couldn't connect: " + err);
                return;
            }
            Go(s.Location, newTab);
        }
    }

    static async Task<bool> PortOpen(string host, int port)
    {
        try
        {
            using var c = new System.Net.Sockets.TcpClient();
            var connect = c.ConnectAsync(host, port);
            return await Task.WhenAny(connect, Task.Delay(1500)) == connect && c.Connected;
        }
        catch { return false; }
    }

    void Go(string path, bool newTab)
    {
        if (newTab) AddTab(path, true);
        else Navigate(path);
    }

    /// <summary>Returns null when connected, "" if the user cancelled, otherwise an error message.</summary>
    async Task<string> ConnectSftp(ServerEntry s, string password)
    {
        if (Sftp.IsConnected(s.Location)) return null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                var pw = password;
                await Task.Run(() => Sftp.Connect(s, pw, TrustHost));
                return null;
            }
            catch (SshAuthenticationException)
            {
                var user = string.IsNullOrWhiteSpace(s.User) ? Environment.UserName : s.User;
                var msg = password == null
                    ? $"Sign in to {s.Host}."
                    : $"The server didn't accept that for {user}@{s.Host}. Check the user name and password.";
                var dlg = new ConnectDialog(this, "Sign in to " + s.Host, msg, user);
                if (dlg.ShowDialog() != true) return "";
                password = dlg.Password;
                if (!string.IsNullOrEmpty(dlg.SignInUser) && dlg.SignInUser != user)
                {
                    s.User = dlg.SignInUser; // remembered for saved servers
                    if (S.Servers.Contains(s)) { SaveSoon(); RebuildNetwork(); }
                }
            }
            catch (Exception ex)
            {
                return Sftp.Describe(ex);
            }
        }
        return "Too many failed sign-in attempts.";
    }

    Task<string> EnsureSftp(string url)
    {
        if (Sftp.IsConnected(url)) return Task.FromResult<string>(null);
        var l = Sftp.Parse(url);
        var entry = S.Servers.FirstOrDefault(s => s.Protocol == "sftp" && Same(s.Host, l.Host)
                                                 && (s.Port > 0 ? s.Port : 22) == l.Port
                                                 && Same(string.IsNullOrWhiteSpace(s.User) ? Environment.UserName : s.User, l.User))
                    ?? new ServerEntry { Protocol = "sftp", Host = l.Host, Port = l.Port, User = l.User };
        return ConnectSftp(entry, null);
    }

    /// <summary>SSH host-key check (trust on first use, loud warning if it ever changes). Runs on a worker thread.</summary>
    bool TrustHost(string host, string fingerprint)
    {
        return Dispatcher.Invoke(() =>
        {
            if (S.KnownHosts.TryGetValue(host, out var known))
            {
                if (known == fingerprint) return true;
                var r = MessageBox.Show(this,
                    $"WARNING: the identity of {host} has changed.\n\n" +
                    $"Saved:\n{known}\n\nNow:\n{fingerprint}\n\n" +
                    "This happens if the server was reinstalled — or if someone is intercepting your connection. " +
                    "Only continue if you know why it changed.\n\nTrust the new key?",
                    "Server identity changed", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (r != MessageBoxResult.Yes) return false;
            }
            else
            {
                var r = MessageBox.Show(this,
                    $"This is the first time Orbit connects to {host}.\n\nServer key:\n{fingerprint}\n\n" +
                    "If you want to be sure, compare it with the server's key (on the server: ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub).\n\n" +
                    "Trust this server?",
                    "Verify server identity", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
                if (r != MessageBoxResult.Yes) return false;
            }
            S.KnownHosts[host] = fingerprint;
            SaveSoon();
            return true;
        });
    }

    /// <summary>Typed or bookmarked \\server\share path that Windows can't open yet: ask Windows to connect (it prompts for credentials).</summary>
    async Task ConnectUncAndGo(string unc, string select, bool history)
    {
        var parts = unc.TrimStart('\\').Split('\\', 2);
        if (parts.Length < 2 || parts[1].Length == 0)
        {
            Toast("Include the share name, e.g. \\\\server\\share");
            return;
        }
        Toast($"Connecting to {parts[0]}…");
        var entry = new ServerEntry { Protocol = "smb", Host = parts[0], Path = parts[1] };
        var err = await NetMount.Connect(entry, Hwnd);
        if (err == "") return;
        if (err != null) { Toast("Couldn't connect: " + err); return; }
        Navigate(unc, select, history, triedConnect: true);
    }

    /// <summary>Google Drive / Dropbox through rclone: sign in once in the browser, then mount as a drive letter.</summary>
    async Task ConnectCloud(ServerEntry s, bool newTab)
    {
        var root = CloudMount.MountedPath(s.Host);
        if (root == null)
        {
            var rclone = CloudMount.FindRclone();
            if (rclone == null || !CloudMount.WinFspInstalled())
            {
                Toast($"Mounting {s.DisplayName} needs rclone and WinFsp — see Connect to server › {s.DisplayName}.");
                return;
            }
            Toast($"Signing in to {s.DisplayName} — finish in your browser if it opens…");
            var err = await CloudMount.EnsureRemoteAsync(rclone, s.Host, s.Protocol);
            if (err != null) { Toast(err); return; }
            Toast($"Mounting {s.DisplayName}…");
            string mountErr;
            (root, mountErr) = await CloudMount.MountAsync(rclone, s.Host, s.DisplayName);
            if (root == null) { Toast(mountErr ?? "Couldn't mount the drive."); return; }
            s.Path = root;
            SaveSoon();
            RebuildNetwork();
            Toast($"{s.DisplayName} is mounted as {root.TrimEnd('\\')} while Orbit is open");
        }
        Go(root, newTab);
    }

    void DisconnectServer(ServerEntry e)
    {
        if (e.Protocol is "gdrive" or "dropbox")
        {
            var mountedAt = CloudMount.MountedPath(e.Host);
            CloudMount.Unmount(e.Host);
            if (mountedAt != null && current?.Path != null && current.Path.StartsWith(mountedAt, StringComparison.OrdinalIgnoreCase)) Navigate(Home);
            RebuildNetwork();
            Toast($"Unmounted {e.DisplayName}");
            return;
        }
        if (e.Protocol == "sftp") Sftp.Disconnect(e.Location);
        else NetMount.Disconnect(e);
        var prefix = e.Protocol == "sftp" ? Sftp.Make(Sftp.Parse(e.Location).User, e.Host, e.Port > 0 ? e.Port : 22, "/") : e.Location;
        if (current?.Path != null && current.Path.StartsWith(prefix.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase))
            Navigate(Home);
        Toast($"Disconnected from {e.Host}");
    }

    void OpenSsh(string url)
    {
        var l = Sftp.Parse(url);
        var args = $"ssh -p {l.Port} {l.User}@{l.Host}";
        try { Process.Start(new ProcessStartInfo("wt.exe", args) { UseShellExecute = true }); }
        catch
        {
            Try(() => Process.Start(new ProcessStartInfo("ssh.exe", $"-p {l.Port} {l.User}@{l.Host}") { UseShellExecute = true }));
        }
    }

    // =====================================================================
    // Remote file actions
    // =====================================================================
    async void OpenRemoteFile(FileItem f)
    {
        var local = Sftp.TempPathFor(f.FullPath);
        Toast($"Downloading “{f.Name}”…");
        ignoreEditsUntil[local] = DateTime.UtcNow.AddSeconds(30);
        try { await Task.Run(() => Sftp.Download(f.FullPath, local)); }
        catch (Exception ex) { Toast("Couldn't open: " + Sftp.Describe(ex)); return; }
        ignoreEditsUntil[local] = DateTime.UtcNow.AddSeconds(2);
        WatchForUpload(local, f.FullPath, f.Name);
        Try(() => Shell.Open(local));
    }

    /// <summary>When a downloaded server file is saved locally, upload it back automatically.</summary>
    void WatchForUpload(string local, string url, string name)
    {
        if (editWatchers.ContainsKey(local)) return;
        try
        {
            var w = new FileSystemWatcher(IOPath.GetDirectoryName(local)!, IOPath.GetFileName(local))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            var debounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            debounce.Tick += async (_, _) =>
            {
                debounce.Stop();
                if (ignoreEditsUntil.TryGetValue(local, out var until) && DateTime.UtcNow < until) return;
                if (!File.Exists(local)) return;
                try
                {
                    await Task.Run(() => Sftp.UploadFileTo(local, url));
                    Toast($"Saved “{name}” back to {Sftp.Parse(url).Host}");
                    if (current != null && Same(Sftp.ParentOf(url), current.Path)) LoadCurrent(keepSelection: true);
                }
                catch (Exception ex) { Toast($"Couldn't save “{name}” to the server: {Sftp.Describe(ex)}"); }
            };
            void Kick() => Dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });
            w.Changed += (_, _) => Kick();
            w.Created += (_, _) => Kick();
            w.Renamed += (_, _) => Kick();
            w.EnableRaisingEvents = true;
            editWatchers[local] = w;
        }
        catch { }
    }

    async void UploadTo(string[] files, string remoteDir)
    {
        if (files == null || files.Length == 0) return;
        if (Same(remoteDir, current?.Path))
        {
            var clashes = files.Select(f => IOPath.GetFileName(f.TrimEnd('\\')))
                               .Where(n => index.ContainsKey(Sftp.Combine(remoteDir, n))).ToList();
            if (clashes.Count > 0)
            {
                var what = clashes.Count == 1 ? $"“{clashes[0]}” already exists" : $"{clashes.Count} items already exist";
                if (MessageBox.Show(this, $"{what} on the server. Replace?", "Upload", MessageBoxButton.YesNo,
                        MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            }
        }
        Toast(files.Length == 1 ? $"Uploading “{IOPath.GetFileName(files[0])}”…" : $"Uploading {files.Length} items…");
        try
        {
            await Task.Run(() => { foreach (var f in files) Sftp.Upload(f, remoteDir); });
            Toast(files.Length == 1 ? "Upload complete" : $"Uploaded {files.Length} items");
        }
        catch (Exception ex) { Toast("Upload failed: " + Sftp.Describe(ex)); }
        if (current != null && Same(current.Path, remoteDir)) LoadCurrent(keepSelection: true);
    }

    async void DownloadRemote(List<FileItem> sel, string targetDir = null)
    {
        targetDir ??= Native.KnownFolder(Native.FolderDownloads) ?? IOPath.Combine(Home, "Downloads");
        Toast(sel.Count == 1 ? $"Downloading “{sel[0].Name}”…" : $"Downloading {sel.Count} items…");
        var done = new List<string>();
        try
        {
            await Task.Run(() =>
            {
                foreach (var f in sel)
                {
                    var name = Sftp.SafeLocalName(f.Name);
                    var dest = IOPath.Combine(targetDir, name);
                    for (int i = 2; File.Exists(dest) || Directory.Exists(dest); i++)
                        dest = IOPath.Combine(targetDir, $"{IOPath.GetFileNameWithoutExtension(name)} ({i}){IOPath.GetExtension(name)}");
                    Sftp.Download(f.FullPath, dest);
                    done.Add(dest);
                }
            });
            Toast(done.Count == 1 ? $"Saved to {Display(targetDir)}: {IOPath.GetFileName(done[0])}" : $"Saved {done.Count} items to {Display(targetDir)}");
        }
        catch (Exception ex) { Toast("Download failed: " + Sftp.Describe(ex)); }
    }

    async void CopyRemoteToClipboard(List<FileItem> sel)
    {
        var dir = IOPath.Combine(IOPath.GetTempPath(), "Orbit", "clipboard", Guid.NewGuid().ToString("N")[..8]);
        Toast("Downloading so you can paste it anywhere…");
        var paths = new List<string>();
        try
        {
            await Task.Run(() =>
            {
                foreach (var f in sel)
                {
                    var dest = IOPath.Combine(dir, Sftp.SafeLocalName(f.Name));
                    Sftp.Download(f.FullPath, dest);
                    paths.Add(dest);
                }
            });
        }
        catch (Exception ex) { Toast("Copy failed: " + Sftp.Describe(ex)); return; }
        var sc = new StringCollection();
        sc.AddRange(paths.ToArray());
        if (TryClipboard(() => Clipboard.SetFileDropList(sc)))
            Toast(paths.Count == 1 ? $"Copied “{sel[0].Name}”" : $"Copied {paths.Count} items");
    }

    void CreateNewRemote(bool folder, string baseName = null, string ext = ".txt")
    {
        var taken = new HashSet<string>(items.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
        var name = folder ? UniqueName(taken, baseName ?? "New folder", "") : UniqueName(taken, baseName ?? "New Text Document", ext);
        var url = Sftp.Combine(current.Path, name);
        try
        {
            if (folder) Sftp.CreateDirectory(url);
            else Sftp.CreateFile(url);
        }
        catch (Exception ex) { Toast(Sftp.Describe(ex)); return; }
        var f = FileItem.Remote(url, name, folder, 0, DateTime.Now);
        items.Add(f);
        index[url] = f;
        ClearSearch(false);
        ActiveList.SelectedItem = f;
        BeginRename(f);
        UpdateStatus();
    }

    void CommitRemoteRename(FileItem f, string newName)
    {
        if (newName.Contains('/')) { Toast("A name on the server can't contain “/”."); return; }
        var parent = Sftp.ParentOf(f.FullPath);
        if (parent == null) return;
        var src = f.FullPath;
        var dest = Sftp.Combine(parent, newName);
        if (index.TryGetValue(dest, out var other) && !ReferenceEquals(other, f))
        {
            Toast($"“{newName}” already exists here");
            return;
        }
        try { Sftp.Rename(src, dest); }
        catch (Exception ex) { Toast("Couldn't rename: " + Sftp.Describe(ex)); return; }
        index.Remove(src);
        f.Rebase(newName, dest);
        index[dest] = f;
        MovePathReferences(src, dest);
        try { var ecv = (System.ComponentModel.IEditableCollectionView)view; ecv.EditItem(f); ecv.CommitEdit(); } catch { }
        ActiveList.SelectedItem = f;
        ActiveList.ScrollIntoView(f);
    }

    async void DeleteRemote(List<FileItem> sel)
    {
        var what = sel.Count == 1 ? $"“{sel[0].Name}”" : $"{sel.Count} items";
        var folders = sel.Any(f => f.IsDir) ? " Folders are deleted with everything inside them." : "";
        if (MessageBox.Show(this, $"Delete {what} from the server?{folders}\n\nServers have no Recycle Bin, so this can't be undone.",
                "Delete from server", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        Toast($"Deleting {what}…");
        try
        {
            await Task.Run(() => { foreach (var f in sel) Sftp.Delete(f.FullPath); });
            Toast($"Deleted {what}");
        }
        catch (Exception ex) { Toast("Delete failed: " + Sftp.Describe(ex)); }
        LoadCurrent(keepSelection: true);
    }

    async void ShowRemoteSubfolders(UIElement anchor, string url)
    {
        List<FileItem> list;
        try { list = await Task.Run(() => Sftp.List(url)); }
        catch (Exception ex) { Toast(Sftp.Describe(ex)); return; }
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom, MaxHeight = 520 };
        var dirs = list.Where(f => f.IsDir && (S.ShowHidden || !f.IsHidden)).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).Take(400).ToList();
        if (dirs.Count == 0) menu.Items.Add(MI("No subfolders", null, null, enabled: false));
        foreach (var d in dirs)
        {
            var target = d.FullPath;
            menu.Items.Add(MI(d.Name, "\uE8B7", () => Navigate(target)));
        }
        menu.IsOpen = true;
    }

    void ShowRemoteMenu(ListBox list, UIElement anchor, List<FileItem> sel)
    {
        var menu = new ContextMenu();
        var here = current.Path;
        if (sel.Count == 0)
        {
            var nw = MI("New", "\uE710", null);
            nw.Items.Add(MI("Folder", "\uE8F4", NewFolder, "Ctrl+Shift+N"));
            nw.Items.Add(MI("Text document", "\uE8A5", NewTextFile));
            menu.Items.Add(nw);
            menu.Items.Add(MI("Upload files…", "\uE898", () =>
            {
                var dlg = new OpenFileDialog { Multiselect = true, Title = "Upload to " + Display(here) };
                if (dlg.ShowDialog(this) == true) UploadTo(dlg.FileNames, here);
            }));
            menu.Items.Add(MI("Paste (upload)", "\uE77F", () => Paste(here), "Ctrl+V", CanPaste()));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Refresh", "\uE72C", () => LoadCurrent(keepSelection: true), "F5"));
            var hid = MI("Show hidden items", "\uE7B3", ToggleHidden, "Ctrl+H");
            hid.IsChecked = S.ShowHidden;
            menu.Items.Add(hid);
            menu.Items.Add(MI("Select all", "\uE8B3", () => ActiveList.SelectAll(), "Ctrl+A"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Copy address", "\uE8C8", () => SetClipboardText(here)));
            menu.Items.Add(MI("Open SSH terminal", "\uE756", () => OpenSsh(here)));
            menu.Items.Add(MI("Disconnect", "\uE711", () =>
            {
                Sftp.Disconnect(here);
                Navigate(Home);
            }));
        }
        else
        {
            var first = sel[0];
            bool allDirs = sel.All(f => f.IsDir);
            menu.Items.Add(MI("Open", "\uE8E5", () => OpenItems(sel), "Enter"));
            if (allDirs) menu.Items.Add(MI(sel.Count == 1 ? "Open in new tab" : "Open in new tabs", "\uE8A7", () => OpenItems(sel, true)));
            menu.Items.Add(MI("Download to Downloads", "\uE896", () => DownloadRemote(sel)));
            menu.Items.Add(MI("Download to…", "\uE8DA", () =>
            {
                var dlg = new OpenFolderDialog { Title = "Download to" };
                if (dlg.ShowDialog(this) == true) DownloadRemote(sel, dlg.FolderName);
            }));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Copy", "\uE8C8", () => CopyRemoteToClipboard(sel), "Ctrl+C"));
            if (sel.Count == 1 && first.IsDir)
                menu.Items.Add(MI("Paste into folder (upload)", "\uE77F", () => Paste(first.FullPath), null, CanPaste()));
            menu.Items.Add(MI("Copy address", "\uE71B", () => CopyPaths(sel.Select(f => f.FullPath)), "Ctrl+Shift+C"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Rename", "\uE8AC", () => BeginRename(first), "F2", sel.Count == 1));
            menu.Items.Add(MI("Delete from server", "\uE74D", () => DeleteRemote(sel), "Del"));
            if (sel.Count == 1 && first.IsDir)
            {
                bool marked = S.Bookmarks.Any(b => Same(b, first.FullPath));
                menu.Items.Add(new Separator());
                menu.Items.Add(MI(marked ? "Remove bookmark" : "Add bookmark", marked ? "\uE735" : "\uE734", () => ToggleBookmark(first.FullPath)));
            }
        }

        if (anchor != null) { menu.PlacementTarget = anchor; menu.Placement = PlacementMode.Bottom; }
        else { menu.PlacementTarget = list; menu.Placement = PlacementMode.MousePoint; }
        menu.IsOpen = true;
    }
}
