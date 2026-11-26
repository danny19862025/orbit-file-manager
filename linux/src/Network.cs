using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Orbit;

/// <summary>A saved server. Passwords are never stored. (Same settings format as the Windows app.)</summary>
public sealed class ServerEntry
{
    public string Name { get; set; }
    public string Protocol { get; set; } = "smb"; // smb | webdav | sftp | gdrive | dropbox
    public string Host { get; set; }
    public int Port { get; set; }
    public string User { get; set; }
    public string Path { get; set; }
    public bool Https { get; set; } = true;
    public string KeyFile { get; set; }

    [JsonIgnore]
    public string Location => Protocol switch
    {
        "gdrive" or "dropbox" => CloudMount.MountedPath(Host) ?? Path ?? "",
        "sftp" => Sftp.Make(string.IsNullOrWhiteSpace(User) ? Environment.UserName : User, Host, Port > 0 ? Port : 22,
                            string.IsNullOrWhiteSpace(Path) ? "/" : Path),
        _ => NetMount.UriFor(this)
    };

    [JsonIgnore]
    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name : Protocol switch
    {
        "gdrive" => "Google Drive",
        "dropbox" => "Dropbox",
        "sftp" => $"{(string.IsNullOrEmpty(User) ? "" : User + "@")}{Host}",
        "webdav" => Host,
        _ => $"{Host}/{(Path ?? "").Replace('\\', '/').Trim('/')}".TrimEnd('/')
    };

    [JsonIgnore]
    public string Glyph => Protocol switch { "sftp" => "", "webdav" => "", "gdrive" or "dropbox" => "", _ => "" };

    [JsonIgnore]
    public string Badge => Protocol switch { "sftp" => "SFTP", "webdav" => "WEBDAV", "gdrive" => "DRIVE", "dropbox" => "DROPBOX", _ => "SMB" };

    public ServerEntry Clone() => (ServerEntry)MemberwiseClone();
}

// =====================================================================
// SMB + WebDAV: mounted by GVfs (the same as Dolphin and Files use), then browsed as ordinary folders
// =====================================================================
public static class NetMount
{
    public static bool IsNetUri(string p) => p != null && (p.StartsWith("smb://", StringComparison.OrdinalIgnoreCase) ||
        p.StartsWith("dav://", StringComparison.OrdinalIgnoreCase) || p.StartsWith("davs://", StringComparison.OrdinalIgnoreCase));

    static string CleanPath(ServerEntry s) => (s.Path ?? "").Replace('\\', '/').Trim('/');

    public static string UriFor(ServerEntry s)
    {
        var path = CleanPath(s);
        var user = string.IsNullOrWhiteSpace(s.User) ? "" : Uri.EscapeDataString(s.User) + "@";
        if (s.Protocol == "smb") return $"smb://{user}{s.Host}/{path}".TrimEnd('/');
        var scheme = s.Https ? "davs" : "dav";
        var port = s.Port > 0 && s.Port != (s.Https ? 443 : 80) ? ":" + s.Port : "";
        return $"{scheme}://{user}{s.Host}{port}/{path}".TrimEnd('/');
    }

    static string GvfsRoot => System.IO.Path.Combine(Xdg.Runtime, "gvfs");

    /// <summary>The folder GVfs shows a mounted smb:// or dav(s):// location at, or null when it isn't mounted.</summary>
    public static string LocalPathFor(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return null;
        var host = u.Host.ToLowerInvariant();
        var parts = Uri.UnescapeDataString(u.AbsolutePath).Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] mounts;
        try { mounts = Directory.Exists(GvfsRoot) ? Directory.GetDirectories(GvfsRoot) : Array.Empty<string>(); } catch { return null; }
        if (u.Scheme == "smb")
        {
            if (parts.Length == 0) return null;
            var share = parts[0].ToLowerInvariant();
            foreach (var m in mounts)
            {
                var n = System.IO.Path.GetFileName(m).ToLowerInvariant();
                if (n.StartsWith("smb-share:") && n.Contains("server=" + host) && n.Contains("share=" + share))
                    return parts.Length > 1 ? System.IO.Path.Combine(new[] { m }.Concat(parts.Skip(1)).ToArray()) : m;
            }
        }
        else
        {
            foreach (var m in mounts)
            {
                var n = System.IO.Path.GetFileName(m).ToLowerInvariant();
                if (!n.StartsWith("dav:") || !n.Contains("host=" + host)) continue;
                var prefix = "";
                var pm = System.Text.RegularExpressions.Regex.Match(n, @"prefix=([^,]*)");
                if (pm.Success) prefix = Uri.UnescapeDataString(pm.Groups[1].Value).Trim('/');
                var rest = string.Join('/', parts);
                if (prefix.Length > 0 && rest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) rest = rest[prefix.Length..].Trim('/');
                return rest.Length > 0 ? System.IO.Path.Combine(m, rest) : m;
            }
        }
        return null;
    }

    public static bool HaveGio => IconService.Which("gio") != null;

    /// <summary>Mounts with gio. Returns (path, null) on success, (null, "") when a password is needed, else (null, error).</summary>
    public static async Task<(string path, string error)> Connect(string uri, string user, string password)
    {
        var existing = LocalPathFor(uri);
        if (existing != null && Directory.Exists(existing)) return (existing, null);
        if (!HaveGio) return (null, "Opening network shares needs GVfs (the gio command and gvfs-smb / gvfs-dav). Install them with your package manager.");
        var root = MountRoot(uri);
        // Guest shares work without a password; otherwise gio asks for one, which we answer on stdin.
        var (code, output) = password == null
            ? await Gio(new[] { "mount", "-a", root }, null)
            : await Gio(new[] { "mount", root }, $"{(string.IsNullOrEmpty(user) ? Environment.UserName : user)}\nWORKGROUP\n{password}\n");
        var path = LocalPathFor(uri);
        if (path != null && Directory.Exists(path)) return (path, null);
        if (code == 0 && path == null) path = LocalPathFor(uri);
        if (path != null) return (path, null);
        var msg = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .LastOrDefault(l => !l.EndsWith(':') && !l.Contains("[")) ?? "";
        if (password == null && (msg.Contains("anonymous", StringComparison.OrdinalIgnoreCase) || msg.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                                 msg.Contains("password", StringComparison.OrdinalIgnoreCase) || msg.Contains("Permission", StringComparison.OrdinalIgnoreCase) || code != 0))
            return (null, "");
        return (null, msg.Length > 0 ? msg.Replace("gio: ", "") : "The server didn't accept the connection.");
    }

    /// <summary>smb://host/share/folder → smb://host/share (gio mounts whole shares).</summary>
    static string MountRoot(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Scheme != "smb") return uri;
        var parts = u.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var auth = u.UserInfo.Length > 0 ? u.UserInfo + "@" : "";
        return parts.Length == 0 ? uri : $"smb://{auth}{u.Authority}/{parts[0]}";
    }

    public static async void Disconnect(ServerEntry s)
    {
        try { await Gio(new[] { "mount", "-u", MountRoot(UriFor(s)) }, null); } catch { }
    }

    static async Task<(int, string)> Gio(string[] args, string stdin)
    {
        var psi = new ProcessStartInfo("gio") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        if (stdin != null) await p.StandardInput.WriteAsync(stdin);
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(40));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (-1, "The server took too long to respond."); }
        return (p.ExitCode, (await o) + "\n" + (await e));
    }
}

// =====================================================================
// Cloud storage: sync folders made by desktop clients (Dropbox, Insync, onedrive, Nextcloud…) and GNOME Online Accounts
// =====================================================================
public static class CloudFolders
{
    public sealed record Cloud(string Name, string Path, string Glyph);

    public static List<Cloud> Detect()
    {
        var list = new List<Cloud>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string name, string path, string glyph = "")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
                path = System.IO.Path.GetFullPath(path).TrimEnd('/');
                if (path.Length == 0) path = "/";
                if (seen.Add(path)) list.Add(new Cloud(name, path, glyph));
            }
            catch { }
        }
        var home = Xdg.Home;

        Add("OneDrive", System.IO.Path.Combine(home, "OneDrive"));
        foreach (var n in new[] { "Google Drive", "GoogleDrive", "My Drive", "gdrive" }) Add("Google Drive", System.IO.Path.Combine(home, n));

        // Dropbox writes its folder locations to ~/.dropbox/info.json
        try
        {
            var info = System.IO.Path.Combine(home, ".dropbox", "info.json");
            if (File.Exists(info))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(info));
                foreach (var acct in doc.RootElement.EnumerateObject())
                    if (acct.Value.ValueKind == JsonValueKind.Object && acct.Value.TryGetProperty("path", out var p))
                        Add(acct.Name == "business" ? "Dropbox (Business)" : "Dropbox", p.GetString());
            }
        }
        catch { }
        Add("Dropbox", System.IO.Path.Combine(home, "Dropbox"));
        Add("Nextcloud", System.IO.Path.Combine(home, "Nextcloud"));
        Add("ownCloud", System.IO.Path.Combine(home, "ownCloud"));
        Add("MEGA", System.IO.Path.Combine(home, "MEGA"));
        Add("pCloud", System.IO.Path.Combine(home, "pCloudDrive"));
        Add("iCloud Drive", System.IO.Path.Combine(home, "iCloudDrive"));

        // Google Drive added in GNOME Online Accounts appears as a GVfs mount
        try
        {
            var gvfs = System.IO.Path.Combine(Xdg.Runtime, "gvfs");
            if (Directory.Exists(gvfs))
                foreach (var d in Directory.GetDirectories(gvfs, "google-drive:*"))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(System.IO.Path.GetFileName(d), "user=([^,]+)");
                    Add(m.Success ? $"Google Drive ({m.Groups[1].Value})" : "Google Drive", d);
                }
        }
        catch { }
        return list;
    }
}

// =====================================================================
// SFTP over SSH (SSH.NET). Locations look like  sftp://user@host:22/home/user
// =====================================================================
public sealed class SftpNotConnectedException : Exception
{
    public SftpNotConnectedException(string key) : base($"Not connected to {key}") { Key = key; }
    public string Key { get; }
}

public static class Sftp
{
    public sealed record Loc(string User, string Host, int Port, string Path)
    {
        public string Key => $"{User}@{Host}:{Port}";
    }

    static readonly ConcurrentDictionary<string, SftpClient> clients = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsSftp(string p) => p != null && p.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase);

    public static string Make(string user, string host, int port, string path)
    {
        if (string.IsNullOrEmpty(path)) path = "/";
        if (!path.StartsWith('/')) path = "/" + path;
        return $"sftp://{(string.IsNullOrEmpty(user) ? "" : user + "@")}{host}:{port}{path}";
    }

    public static Loc Parse(string url)
    {
        var rest = url.Substring("sftp://".Length);
        int slash = rest.IndexOf('/');
        var auth = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? "/" : rest[slash..];
        string user = "";
        int at = auth.LastIndexOf('@');
        if (at >= 0) { user = auth[..at]; auth = auth[(at + 1)..]; }
        if (user.Length == 0) user = Environment.UserName;
        int port = 22;
        int colon = auth.LastIndexOf(':');
        if (colon > 0 && !auth.EndsWith(']') && int.TryParse(auth[(colon + 1)..], out var pp)) { port = pp; auth = auth[..colon]; }
        if (path.Length > 1) path = path.TrimEnd('/');
        return new Loc(user, auth, port, path);
    }

    public static string Normalize(string url)
    {
        var l = Parse(url);
        return Make(l.User, l.Host, l.Port, l.Path);
    }

    public static string Combine(string url, string name) => url.TrimEnd('/') + "/" + name;

    public static string ParentOf(string url)
    {
        var l = Parse(url);
        if (l.Path == "/") return null;
        int i = l.Path.LastIndexOf('/');
        return Make(l.User, l.Host, l.Port, i <= 0 ? "/" : l.Path[..i]);
    }

    public static string NameOf(string url)
    {
        var l = Parse(url);
        if (l.Path == "/") return l.Host;
        return l.Path[(l.Path.LastIndexOf('/') + 1)..];
    }

    public static bool IsConnected(string url) =>
        clients.TryGetValue(Parse(url).Key, out var c) && c.IsConnected;

    static SftpClient Client(Loc l)
    {
        if (clients.TryGetValue(l.Key, out var c) && c.IsConnected) return c;
        throw new SftpNotConnectedException(l.Key);
    }

    /// <summary>
    /// Connects using SSH keys (the entry's key file, or ~/.ssh/id_*) and/or the password.
    /// <paramref name="trustHost"/> decides whether an unknown/changed host key is accepted.
    /// Throws SshAuthenticationException when credentials are needed or wrong.
    /// </summary>
    public static void Connect(ServerEntry s, string password, Func<string, string, bool> trustHost)
    {
        int port = s.Port > 0 ? s.Port : 22;
        var user = string.IsNullOrWhiteSpace(s.User) ? Environment.UserName : s.User;
        var key = $"{user}@{s.Host}:{port}";
        var methods = new List<AuthenticationMethod>();

        var keyFiles = new List<IPrivateKeySource>();
        var candidates = !string.IsNullOrWhiteSpace(s.KeyFile)
            ? new[] { s.KeyFile.StartsWith("~/") ? System.IO.Path.Combine(Xdg.Home, s.KeyFile[2..]) : s.KeyFile }
            : new[] { "id_ed25519", "id_ecdsa", "id_rsa" }.Select(n => System.IO.Path.Combine(Xdg.Home, ".ssh", n)).ToArray();
        foreach (var f in candidates.Where(File.Exists))
        {
            try { keyFiles.Add(new PrivateKeyFile(f)); }
            catch
            {
                if (!string.IsNullOrEmpty(password))
                    try { keyFiles.Add(new PrivateKeyFile(f, password)); } catch { }
            }
        }
        if (keyFiles.Count > 0) methods.Add(new PrivateKeyAuthenticationMethod(user, keyFiles.ToArray()));

        if (!string.IsNullOrEmpty(password))
        {
            methods.Add(new PasswordAuthenticationMethod(user, password));
            var kb = new KeyboardInteractiveAuthenticationMethod(user);
            kb.AuthenticationPrompt += (_, e) =>
            {
                foreach (var p in e.Prompts)
                    if (p.Request.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0) p.Response = password;
            };
            methods.Add(kb);
        }
        if (methods.Count == 0) methods.Add(new NoneAuthenticationMethod(user));

        var ci = new ConnectionInfo(s.Host, port, user, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(15) };
        var c = new SftpClient(ci) { OperationTimeout = TimeSpan.FromSeconds(60) };
        c.HostKeyReceived += (_, e) => e.CanTrust = trustHost($"[{s.Host}]:{port}", $"{e.HostKeyName} SHA256:{e.FingerPrintSHA256}");
        try
        {
            c.Connect();
        }
        catch
        {
            c.Dispose();
            throw;
        }
        if (clients.TryRemove(key, out var old)) { try { old.Dispose(); } catch { } }
        clients[key] = c;
    }

    public static void Disconnect(string url)
    {
        if (clients.TryRemove(Parse(url).Key, out var c))
        {
            try { c.Disconnect(); c.Dispose(); } catch { }
        }
    }

    public static void DisconnectAll()
    {
        foreach (var k in clients.Keys.ToList())
            if (clients.TryRemove(k, out var c)) { try { c.Dispose(); } catch { } }
    }

    public static string HomeOf(string url)
    {
        var l = Parse(url);
        var c = Client(l);
        lock (c) return Make(l.User, l.Host, l.Port, c.WorkingDirectory ?? "/");
    }

    public static List<FileItem> List(string url)
    {
        var l = Parse(url);
        var c = Client(l);
        var list = new List<FileItem>();
        lock (c)
        {
            foreach (var f in c.ListDirectory(l.Path))
            {
                if (f.Name == "." || f.Name == "..") continue;
                bool dir = f.IsDirectory;
                long size = f.Length;
                var mod = f.LastWriteTime;
                if (f.IsSymbolicLink)
                {
                    try { var a = c.GetAttributes(f.FullName); dir = a.IsDirectory; size = a.Size; mod = a.LastWriteTime; } catch { }
                }
                list.Add(FileItem.Remote(Combine(url, f.Name), f.Name, dir, size, mod));
            }
        }
        return list;
    }

    public static bool Exists(string url)
    {
        var l = Parse(url);
        var c = Client(l);
        lock (c) return c.Exists(l.Path);
    }

    public static void CreateDirectory(string url)
    {
        var l = Parse(url);
        var c = Client(l);
        lock (c) c.CreateDirectory(l.Path);
    }

    public static void CreateFile(string url)
    {
        var l = Parse(url);
        var c = Client(l);
        lock (c) using (c.Create(l.Path)) { }
    }

    public static void Rename(string url, string newUrl)
    {
        var l = Parse(url);
        var c = Client(l);
        lock (c) c.RenameFile(l.Path, Parse(newUrl).Path);
    }

    public static void Delete(string url)
    {
        var l = Parse(url);
        var c = Client(l);
        lock (c) DeleteRec(c, l.Path);
    }

    static void DeleteRec(SftpClient c, string path)
    {
        var a = c.GetAttributes(path);
        if (a.IsDirectory && !a.IsSymbolicLink)
        {
            foreach (var f in c.ListDirectory(path).ToList())
            {
                if (f.Name is "." or "..") continue;
                if (f.IsDirectory && !f.IsSymbolicLink) DeleteRec(c, f.FullName);
                else c.DeleteFile(f.FullName);
            }
            c.DeleteDirectory(path);
        }
        else c.DeleteFile(path);
    }

    /// <summary>Download a file or folder (recursively) to a local path.</summary>
    public static void Download(string url, string localPath)
    {
        var l = Parse(url);
        var c = Client(l);
        lock (c) DownloadRec(c, l.Path, localPath);
    }

    static void DownloadRec(SftpClient c, string remote, string local)
    {
        var a = c.GetAttributes(remote);
        if (a.IsDirectory)
        {
            Directory.CreateDirectory(local);
            foreach (var f in c.ListDirectory(remote).ToList())
            {
                if (f.Name is "." or "..") continue;
                DownloadRec(c, f.FullName, System.IO.Path.Combine(local, SafeLocalName(f.Name)));
            }
        }
        else
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(local)!);
            using var fs = File.Create(local);
            c.DownloadFile(remote, fs);
        }
    }

    /// <summary>Upload a local file or folder (recursively) into a remote directory.</summary>
    public static void Upload(string localPath, string remoteDirUrl)
    {
        var l = Parse(remoteDirUrl);
        var c = Client(l);
        lock (c) UploadRec(c, localPath, l.Path.TrimEnd('/') + "/" + System.IO.Path.GetFileName(localPath.TrimEnd('/')));
    }

    /// <summary>Overwrite one remote file with a local file (used to save edits back).</summary>
    public static void UploadFileTo(string localFile, string remoteFileUrl)
    {
        var l = Parse(remoteFileUrl);
        var c = Client(l);
        lock (c)
        {
            using var fs = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            c.UploadFile(fs, l.Path, true);
        }
    }

    static void UploadRec(SftpClient c, string local, string remote)
    {
        if (Directory.Exists(local))
        {
            if (!c.Exists(remote)) c.CreateDirectory(remote);
            foreach (var e in Directory.EnumerateFileSystemEntries(local))
                UploadRec(c, e, remote.TrimEnd('/') + "/" + System.IO.Path.GetFileName(e));
        }
        else
        {
            using var fs = File.OpenRead(local);
            c.UploadFile(fs, remote, true);
        }
    }

    public static string SafeLocalName(string name)
    {
        foreach (var ch in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return name;
    }

    public static string TempPathFor(string url)
    {
        var l = Parse(url);
        var parts = l.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(SafeLocalName);
        return System.IO.Path.Combine(new[] { System.IO.Path.GetTempPath(), "Orbit-" + Environment.UserName, "sftp", SafeLocalName(l.Host) }.Concat(parts).ToArray());
    }

    public static string Describe(Exception ex) => ex switch
    {
        SftpNotConnectedException => "Not connected to this server.",
        SshAuthenticationException => "The server rejected the username, password or key.",
        SftpPathNotFoundException => "That folder doesn't exist on the server.",
        SftpPermissionDeniedException => "You don't have permission to do that on the server.",
        SshConnectionException => "The connection to the server was lost.",
        SshOperationTimeoutException => "The server took too long to respond.",
        System.Net.Sockets.SocketException se => "Couldn't reach the server: " + se.Message,
        AggregateException ae when ae.InnerException != null => Describe(ae.InnerException),
        _ => ex.Message
    };
}

// =====================================================================
// Google Drive / Dropbox mounted as a folder through rclone + FUSE (both optional, user-installed).
// Signing in happens in the provider's own page in the user's browser — Orbit never sees the password.
// =====================================================================
public static class CloudMount
{
    sealed record Mount(Process Proc, string Root);
    static readonly Dictionary<string, Mount> mounts = new(StringComparer.OrdinalIgnoreCase);

    public static string RcloneType(string protocol) => protocol == "gdrive" ? "drive" : "dropbox";

    public static string FindRclone() => IconService.Which("rclone") ?? (File.Exists(System.IO.Path.Combine(Xdg.Home, ".local/bin/rclone")) ? System.IO.Path.Combine(Xdg.Home, ".local/bin/rclone") : null);

    public static bool FuseInstalled() => IconService.Which("fusermount3") != null || IconService.Which("fusermount") != null;

    static async Task<(int code, string output)> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        using var cts = new System.Threading.CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (-1, "Timed out waiting for sign-in."); }
        return (p.ExitCode, (await outTask) + (await errTask));
    }

    /// <summary>Creates the rclone remote if needed; this opens the provider's sign-in page in the browser.</summary>
    public static async Task<string> EnsureRemoteAsync(string rclone, string name, string protocol)
    {
        var (code, output) = await RunAsync(rclone, new[] { "listremotes" }, TimeSpan.FromSeconds(20));
        if (code == 0 && output.Split('\n').Any(l => l.Trim().Equals(name + ":", StringComparison.OrdinalIgnoreCase))) return null;
        (code, output) = await RunAsync(rclone, new[] { "config", "create", name, RcloneType(protocol) }, TimeSpan.FromMinutes(10));
        if (code != 0) return "Sign-in didn't complete. " + LastLine(output);
        return null;
    }

    static string LastLine(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";

    static bool IsMountPoint(string dir)
    {
        try { return File.ReadLines("/proc/self/mountinfo").Any(l => l.Split(' ').Length > 4 && Unescape(l.Split(' ')[4]) == dir); }
        catch { return false; }
    }

    static string Unescape(string s) => s.Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");

    /// <summary>Starts "rclone mount" in the background and waits for the folder to appear.</summary>
    public static async Task<(string root, string error)> MountAsync(string rclone, string name, string label)
    {
        var existing = MountedPath(name);
        if (existing != null) return (existing, null);
        var root = System.IO.Path.Combine(Xdg.Data, "Orbit", "mounts", label);
        try { Directory.CreateDirectory(root); } catch (Exception ex) { return (null, ex.Message); }
        if (IsMountPoint(root)) { try { Process.Start("fusermount3", new[] { "-u", root })?.WaitForExit(3000); } catch { } }
        var psi = new ProcessStartInfo(rclone) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "mount", name + ":", root, "--vfs-cache-mode", "full", "--volname", label }) psi.ArgumentList.Add(a);
        Process p;
        try { p = Process.Start(psi)!; }
        catch (Exception ex) { return (null, ex.Message); }
        var err = new System.Text.StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
        p.BeginErrorReadLine();
        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(500);
            if (p.HasExited) { lock (err) return (null, "Mount failed: " + LastLine(err.ToString())); }
            if (IsMountPoint(root)) { lock (mounts) mounts[name] = new Mount(p, root); return (root, null); }
        }
        try { p.Kill(true); } catch { }
        return (null, "The folder didn't appear in time.");
    }

    public static string MountedPath(string name)
    {
        lock (mounts)
        {
            if (name != null && mounts.TryGetValue(name, out var m) && !m.Proc.HasExited) return m.Root;
            return null;
        }
    }

    public static void Unmount(string name)
    {
        lock (mounts)
        {
            if (name != null && mounts.Remove(name, out var m)) Stop(m);
        }
    }

    static void Stop(Mount m)
    {
        try { Process.Start("fusermount3", new[] { "-u", "-z", m.Root })?.WaitForExit(3000); } catch { }
        try { if (!m.Proc.HasExited) m.Proc.Kill(true); } catch { }
    }

    public static void UnmountAll()
    {
        lock (mounts)
        {
            foreach (var m in mounts.Values) Stop(m);
            mounts.Clear();
        }
    }
}
