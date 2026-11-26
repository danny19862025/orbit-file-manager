using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Renci.SshNet.Common;
using IOPath = System.IO.Path;

namespace Orbit;

/// <summary>Network section: SMB / WebDAV (mounted by GVfs), SFTP (in-app), and cloud sync folders.</summary>
public partial class MainWindow
{
    readonly Dictionary<string, FileSystemWatcher> editWatchers = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTime> ignoreEditsUntil = new(StringComparer.Ordinal);

    // =====================================================================
    // Sidebar
    // =====================================================================
    void RebuildNetwork()
    {
        Network.Clear();
        foreach (var c in CloudFolders.Detect())
        {
            var badge = c.Name.StartsWith("Google") ? "DRIVE" : c.Name.StartsWith("Dropbox") ? "DROPBOX" : c.Name.StartsWith("iCloud") ? "ICLOUD"
                      : c.Name.StartsWith("OneDrive") ? "ONEDRIVE" : c.Name.ToUpperInvariant().Split(' ')[0];
            Network.Add(new SideItem { Name = c.Name, Path = Norm(c.Path), Glyph = c.Glyph, Kind = "cloud", ToolTipText = c.Path, Badge = badge, IsConnected = true });
        }
        foreach (var s in S.Servers)
        {
            bool cloud = s.Protocol is "gdrive" or "dropbox";
            var mounted = cloud ? CloudMount.MountedPath(s.Host) : null;
            bool connected = cloud ? mounted != null : s.Protocol == "sftp" ? Sftp.IsConnected(s.Location) : NetMount.LocalPathFor(s.Location) != null;
            Network.Add(new SideItem
            {
                Name = s.DisplayName,
                Path = cloud ? (mounted != null ? Norm(mounted) : "cloud:" + s.Host) : Norm(s.Location),
                Glyph = s.Glyph,
                Kind = "server",
                Server = s,
                Badge = s.Badge,
                IsConnected = connected,
                ToolTipText = cloud
                    ? (mounted != null ? $"{s.DisplayName} · mounted at {mounted}" : $"{s.DisplayName} · click to sign in and mount")
                    : $"{s.Protocol.ToUpperInvariant()} · {s.Location}"
            });
        }
        Network.Add(new SideItem { Name = "Connect to server…", Path = "connect:", Glyph = "", Kind = "connect", ToolTipText = "SMB / Samba, SFTP (SSH), WebDAV, Google Drive or Dropbox" });
        NetworkBlock.IsVisible = Network.Count > 0;
        MarkActive();
    }

    void BuildServerMenu(ContextMenu menu, SideItem s)
    {
        if (s.Kind == "connect")
        {
            menu.Items.Add(MI("Connect to server…", "", () => ShowConnectDialog(null)));
            return;
        }
        var e = s.Server;
        menu.Items.Add(MI("Connect", "", () => _ = ConnectServer(e, false, null)));
        menu.Items.Add(MI("Open in new tab", "", () => _ = ConnectServer(e, true, null)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Edit…", "", () => ShowConnectDialog(e)));
        if (e.Protocol == "sftp") menu.Items.Add(MI("Open SSH terminal", "", () => OpenSsh(e.Location)));
        menu.Items.Add(MI("Copy address", "", () => SetClipboardText(e.Location)));
        menu.Items.Add(MI("Disconnect", "", () => DisconnectServer(e)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MI("Remove from sidebar", "", () =>
        {
            S.Servers.Remove(e);
            RebuildNetwork();
            SaveSoon();
        }));
    }

    async void ShowConnectDialog(ServerEntry edit)
    {
        var dlg = new ConnectDialog(this, edit?.Clone());
        if (await dlg.ShowDialog<bool>(this) != true) return;
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
            RebuildNetwork();
            Go(target, newTab);
        }
        else
        {
            var path = await MountNet(s.Location, s.User, password, s.Host);
            if (path == null) return;
            RebuildNetwork();
            Go(path, newTab);
        }
    }

    void Go(string path, bool newTab)
    {
        if (newTab) AddTab(path, true);
        else Navigate(path);
    }

    /// <summary>Mounts an smb:// or dav(s):// location (asking for the password if the server wants one). Returns the local folder or null.</summary>
    async Task<string> MountNet(string uri, string user, string password, string host)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var (path, err) = await NetMount.Connect(uri, user, password);
            if (path != null) return path;
            if (err != "") { Toast("Couldn't connect: " + err); return null; }
            var dlg = new ConnectDialog(this, "Sign in to " + host,
                password == null ? $"{host} needs a username and password." : $"{host} didn't accept that. Try again.", askUser: true, user: user);
            if (await dlg.ShowDialog<bool>(this) != true) return null;
            user = dlg.UserName;
            password = dlg.Password ?? "";
        }
        Toast("Too many failed sign-in attempts.");
        return null;
    }

    /// <summary>Typed or bookmarked smb:// / dav:// address: mount it, then go to the folder GVfs shows it at.</summary>
    async Task ConnectNetAndGo(string uri, string select, bool history)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == "smb" && u.AbsolutePath.Trim('/').Length == 0)
        {
            Toast("Include the share name, e.g. smb://server/share");
            return;
        }
        Toast($"Connecting to {(u?.Host ?? uri)}…");
        var user = u != null && u.UserInfo.Length > 0 ? Uri.UnescapeDataString(u.UserInfo.Split(':')[0]) : null;
        var path = await MountNet(uri, user, null, u?.Host ?? uri);
        if (path == null) return;
        RebuildNetwork();
        Navigate(path, select, history);
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
                var who = $"{(string.IsNullOrWhiteSpace(s.User) ? Environment.UserName : s.User)}@{s.Host}";
                var msg = password == null
                    ? $"Enter the password for {who}."
                    : $"The server didn't accept that for {who}. Try again.";
                var dlg = new ConnectDialog(this, "Sign in to " + s.Host, msg);
                if (await dlg.ShowDialog<bool>(this) != true) return "";
                password = dlg.Password;
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
        var entry = S.Servers.FirstOrDefault(s => s.Protocol == "sftp" && string.Equals(s.Host, l.Host, StringComparison.OrdinalIgnoreCase)
                                                 && (s.Port > 0 ? s.Port : 22) == l.Port
                                                 && Same(string.IsNullOrWhiteSpace(s.User) ? Environment.UserName : s.User, l.User))
                    ?? new ServerEntry { Protocol = "sftp", Host = l.Host, Port = l.Port, User = l.User };
        return ConnectSftp(entry, null);
    }

    /// <summary>SSH host-key check (trust on first use, loud warning if it ever changes). Runs on a worker thread.</summary>
    bool TrustHost(string host, string fingerprint)
    {
        return Dialog.OnUi(async () =>
        {
            // ~/.ssh/known_hosts entries are trusted too would need their fingerprints; Orbit keeps its own list like on Windows
            if (S.KnownHosts.TryGetValue(host, out var known))
            {
                if (known == fingerprint) return true;
                bool ok = await Dialog.Ask(this, "Server identity changed",
                    $"WARNING: the identity of {host} has changed.\n\n" +
                    $"Saved:\n{known}\n\nNow:\n{fingerprint}\n\n" +
                    "This happens if the server was reinstalled — or if someone is intercepting your connection. " +
                    "Only continue if you know why it changed.\n\nTrust the new key?", "Trust", "Cancel", warning: true, yesIsDefault: false);
                if (!ok) return false;
            }
            else
            {
                bool ok = await Dialog.Ask(this, "Verify server identity",
                    $"This is the first time Orbit connects to {host}.\n\nServer key:\n{fingerprint}\n\n" +
                    "If you want to be sure, compare it with the server's key (on the server: ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub).\n\n" +
                    "Trust this server?", "Trust", "Cancel");
                if (!ok) return false;
            }
            S.KnownHosts[host] = fingerprint;
            SaveSoon();
            return true;
        });
    }

    /// <summary>Google Drive / Dropbox through rclone: sign in once in the browser, then mount it as a folder.</summary>
    async Task ConnectCloud(ServerEntry s, bool newTab)
    {
        var root = CloudMount.MountedPath(s.Host);
        if (root == null)
        {
            var rclone = CloudMount.FindRclone();
            if (rclone == null || !CloudMount.FuseInstalled())
            {
                Toast($"Mounting {s.DisplayName} needs rclone and FUSE — see Connect to server › {s.DisplayName}.");
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
            Toast($"{s.DisplayName} is mounted at {root} while Orbit is open");
        }
        Go(root, newTab);
    }

    void DisconnectServer(ServerEntry e)
    {
        if (e.Protocol is "gdrive" or "dropbox")
        {
            var mountedAt = CloudMount.MountedPath(e.Host);
            if (mountedAt != null && current?.Path != null && current.Path.StartsWith(mountedAt, StringComparison.Ordinal)) Navigate(Home);
            StopWatcher();
            CloudMount.Unmount(e.Host);
            RebuildNetwork();
            Toast($"Unmounted {e.DisplayName}");
            return;
        }
        string prefix;
        if (e.Protocol == "sftp")
        {
            Sftp.Disconnect(e.Location);
            prefix = Sftp.Make(Sftp.Parse(e.Location).User, e.Host, e.Port > 0 ? e.Port : 22, "/");
        }
        else
        {
            prefix = NetMount.LocalPathFor(e.Location) ?? e.Location;
            if (current?.Path != null && current.Path.StartsWith(prefix.TrimEnd('/'), StringComparison.Ordinal)) Navigate(Home);
            StopWatcher();
            NetMount.Disconnect(e);
        }
        if (current?.Path != null && current.Path.StartsWith(prefix.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase))
            Navigate(Home);
        Dispatcher.UIThread.Post(RebuildNetwork, DispatcherPriority.Background);
        Toast($"Disconnected from {e.Host}");
    }

    void OpenSsh(string url)
    {
        var l = Sftp.Parse(url);
        Try(() => Shell.RunInTerminal("ssh", "-p", l.Port.ToString(), $"{l.User}@{l.Host}"));
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
            void Kick() => Dispatcher.UIThread.Post(() => { debounce.Stop(); debounce.Start(); });
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
            var clashes = files.Select(f => IOPath.GetFileName(f.TrimEnd('/')))
                               .Where(n => index.ContainsKey(Sftp.Combine(remoteDir, n))).ToList();
            if (clashes.Count > 0)
            {
                var what = clashes.Count == 1 ? $"“{clashes[0]}” already exists" : $"{clashes.Count} items already exist";
                if (!await Dialog.Ask(this, "Upload", $"{what} on the server. Replace?", "Replace", "Cancel", yesIsDefault: false)) return;
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
        targetDir ??= Xdg.UserDir("DOWNLOAD", "Downloads");
        Directory.CreateDirectory(targetDir);
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
        var dir = IOPath.Combine(IOPath.GetTempPath(), "Orbit-" + Environment.UserName, "clipboard", Guid.NewGuid().ToString("N")[..8]);
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
        if (await PutFilesOnClipboard(paths, false))
            Toast(paths.Count == 1 ? $"Copied “{sel[0].Name}”" : $"Copied {paths.Count} items");
    }

    void CreateNewRemote(bool folder, string baseName = null, string ext = ".txt")
    {
        var taken = new HashSet<string>(items.Select(i => i.Name), StringComparer.Ordinal);
        var name = folder ? UniqueName(taken, baseName ?? "New Folder", "") : UniqueName(taken, baseName ?? "New Text Document", ext);
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
        InsertSorted(f);
        ClearSearch(false);
        ActiveList.SelectedItem = f;
        BeginRename(f);
        UpdateStatus();
    }

    void CommitRemoteRename(FileItem f, string newName)
    {
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
        if (view.Remove(f)) InsertSorted(f);
        ActiveList.SelectedItem = f;
        ActiveList.ScrollIntoView(f);
    }

    async void DeleteRemote(List<FileItem> sel)
    {
        var what = sel.Count == 1 ? $"“{sel[0].Name}”" : $"{sel.Count} items";
        var folders = sel.Any(f => f.IsDir) ? " Folders are deleted with everything inside them." : "";
        if (!await Dialog.Ask(this, "Delete from server", $"Delete {what} from the server?{folders}\n\nServers have no Trash, so this can't be undone.",
                "Delete", "Cancel", warning: true, yesIsDefault: false))
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

    async void ShowRemoteSubfolders(Control anchor, string url)
    {
        List<FileItem> list;
        try { list = await Task.Run(() => Sftp.List(url)); }
        catch (Exception ex) { Toast(Sftp.Describe(ex)); return; }
        var menu = new ContextMenu { MaxHeight = 520 };
        var dirs = list.Where(f => f.IsDir && (S.ShowHidden || !f.IsHidden)).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).Take(400).ToList();
        if (dirs.Count == 0) menu.Items.Add(MI("No subfolders", null, null, enabled: false));
        foreach (var d in dirs)
        {
            var target = d.FullPath;
            menu.Items.Add(MI(d.Name, "", () => Navigate(target)));
        }
        OpenMenu(menu, anchor, true);
    }

    async void ShowRemoteMenu(ListBox list, Control anchor, List<FileItem> sel)
    {
        var menu = new ContextMenu();
        var here = current.Path;
        bool canPaste = await CanPaste();
        if (sel.Count == 0)
        {
            var nw = MI("New", "", null);
            nw.Items.Add(MI("Folder", "", NewFolder, "Ctrl+Shift+N"));
            nw.Items.Add(MI("Text document", "", NewTextFile));
            menu.Items.Add(nw);
            menu.Items.Add(MI("Upload files…", "", async () =>
            {
                var r = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true, Title = "Upload to " + Display(here) });
                var files = r.Select(x => x.TryGetLocalPath()).Where(p => p != null).ToArray();
                if (files.Length > 0) UploadTo(files, here);
            }));
            menu.Items.Add(MI("Paste (upload)", "", () => Paste(here), "Ctrl+V", canPaste));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Refresh", "", () => LoadCurrent(keepSelection: true), "F5"));
            var hid = MI("Show hidden items", "", ToggleHidden, "Ctrl+H");
            hid.IsTicked = S.ShowHidden;
            menu.Items.Add(hid);
            menu.Items.Add(MI("Select all", "", () => ActiveList.SelectAll(), "Ctrl+A"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Copy address", "", () => SetClipboardText(here)));
            menu.Items.Add(MI("Open SSH terminal", "", () => OpenSsh(here)));
            menu.Items.Add(MI("Disconnect", "", () =>
            {
                Sftp.Disconnect(here);
                Navigate(Home);
                RebuildNetwork();
            }));
        }
        else
        {
            var first = sel[0];
            bool allDirs = sel.All(f => f.IsDir);
            menu.Items.Add(MI("Open", "", () => OpenItems(sel), "Enter"));
            if (allDirs) menu.Items.Add(MI(sel.Count == 1 ? "Open in new tab" : "Open in new tabs", "", () => OpenItems(sel, true)));
            menu.Items.Add(MI("Download to Downloads", "", () => DownloadRemote(sel)));
            menu.Items.Add(MI("Download to…", "", async () =>
            {
                var dir = await PickFolder("Download to");
                if (dir != null) DownloadRemote(sel, dir);
            }));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Copy", "", () => CopyRemoteToClipboard(sel), "Ctrl+C"));
            if (sel.Count == 1 && first.IsDir)
                menu.Items.Add(MI("Paste into folder (upload)", "", () => Paste(first.FullPath), null, canPaste));
            menu.Items.Add(MI("Copy address", "", () => CopyPaths(sel.Select(f => f.FullPath)), "Ctrl+Shift+C"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MI("Rename", "", () => BeginRename(first), "F2", sel.Count == 1));
            menu.Items.Add(MI("Delete from server", "", () => DeleteRemote(sel), "Del"));
            if (sel.Count == 1 && first.IsDir)
            {
                bool marked = S.Bookmarks.Any(b => Same(b, first.FullPath));
                menu.Items.Add(new Separator());
                menu.Items.Add(MI(marked ? "Remove bookmark" : "Add bookmark", marked ? "" : "", () => ToggleBookmark(first.FullPath)));
            }
        }
        OpenMenu(menu, anchor ?? list, anchor != null);
    }
}
