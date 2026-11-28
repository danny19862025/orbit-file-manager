using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace Orbit;

public partial class ConnectDialog : Window
{
    public ServerEntry Result { get; private set; }
    public string Password { get; private set; }
    public bool Save { get; private set; }
    readonly bool passwordOnly;

    /// <summary>Full "connect to server" form. Pass an entry to edit it.</summary>
    public ConnectDialog(Window owner, ServerEntry edit = null)
    {
        InitializeComponent();
        Owner = owner;
        if (edit != null)
        {
            TitleText.Text = "Edit server";
            RbSmb.IsChecked = edit.Protocol == "smb";
            RbSftp.IsChecked = edit.Protocol == "sftp";
            RbDav.IsChecked = edit.Protocol == "webdav";
            RbGdrive.IsChecked = edit.Protocol == "gdrive";
            RbDropbox.IsChecked = edit.Protocol == "dropbox";
            HostBox.Text = edit.Host;
            PortBox.Text = edit.Port > 0 ? edit.Port.ToString() : "";
            PathBox.Text = edit.Path;
            UserBox.Text = edit.User;
            KeyBox.Text = edit.KeyFile;
            HttpsCheck.IsChecked = edit.Https;
            NameBox.Text = edit.Name;
            OkBtn.Content = "Save and connect";
        }
        Proto_Changed(null, null);
        Loaded += (_, _) => HostBox.Focus();
    }

    /// <summary>The user name typed in the sign-in prompt (null when it wasn't shown).</summary>
    public string SignInUser { get; private set; }

    /// <summary>Sign-in prompt for an SFTP server: user name (when <paramref name="user"/> is given) and password.</summary>
    public ConnectDialog(Window owner, string title, string message, string user = null)
    {
        InitializeComponent();
        Owner = owner;
        passwordOnly = true;
        TitleText.Text = title;
        Message.Text = message;
        Message.Visibility = Visibility.Visible;
        FullForm.Visibility = Visibility.Collapsed;
        SavePanel.Visibility = Visibility.Collapsed;
        PwLabel.Text = "Password or key passphrase (not saved)";
        if (user != null)
        {
            SignInUserPanel.Visibility = Visibility.Visible;
            SignInUserBox.Text = user;
        }
        Loaded += (_, _) => PwBox.Focus();
    }

    /// <summary>Set when the user chose "Open in Orbit" for an installed Google Drive / Dropbox folder.</summary>
    public string OpenPath { get; private set; }

    string Proto => RbSftp.IsChecked == true ? "sftp" : RbDav.IsChecked == true ? "webdav"
                  : RbGdrive.IsChecked == true ? "gdrive" : RbDropbox.IsChecked == true ? "dropbox" : "smb";

    bool IsCloud => Proto is "gdrive" or "dropbox";
    string CloudName => Proto == "gdrive" ? "Google Drive" : "Dropbox";
    string cloudFolder;

    void Proto_Changed(object sender, RoutedEventArgs e)
    {
        if (HostBox == null) return;
        var p = Proto;
        ServerFields.Visibility = IsCloud ? Visibility.Collapsed : Visibility.Visible;
        CloudPanel.Visibility = IsCloud ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
        if (IsCloud)
        {
            PasswordPanel.Visibility = Visibility.Collapsed;
            ProtoHint.Text = $"Use {CloudName}'s own desktop app (recommended), or mount it as a drive letter with rclone. " +
                             "Either way you sign in on Google's or Dropbox's own page — Orbit never sees your password.";
            RefreshCloud();
            return;
        }
        OkBtn.Content = "Connect";
        OkBtn.IsEnabled = true;
        OkBtn.ToolTip = null;
        KeyPanel.Visibility = p == "sftp" ? Visibility.Visible : Visibility.Collapsed;
        PasswordPanel.Visibility = p == "sftp" ? Visibility.Visible : Visibility.Collapsed;
        HttpsCheck.Visibility = p == "webdav" ? Visibility.Visible : Visibility.Collapsed;
        PathLabel.Text = p switch { "smb" => "Share and folder (e.g. media/movies)", "sftp" => "Folder (blank = home folder)", _ => "Path on server (optional)" };
        ProtoHint.Text = p switch
        {
            "smb" => "Windows file shares, Samba on Linux, and most NAS boxes. Windows asks for your password and can remember it securely.",
            "sftp" => "Any server you can SSH into. Orbit checks the server's identity the first time and warns you if it ever changes.",
            _ => "WebDAV folders such as Nextcloud, ownCloud or a NAS. Windows asks for your password."
        };
    }

    void RefreshCloud()
    {
        var found = CloudFolders.Detect().FirstOrDefault(c => c.Name.StartsWith(CloudName, StringComparison.OrdinalIgnoreCase));
        cloudFolder = found?.Path;
        var green = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0xDE, 0x80));
        if (found != null)
        {
            CloudDot.Fill = green;
            CloudStatus.Text = $"{CloudName} for desktop is installed";
            CloudDetail.Text = $"Your files are at {found.Path}. It's already in the sidebar under Network.";
            CloudOpenBtn.Visibility = Visibility.Visible;
            CloudGetBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            CloudDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "FaintBrush");
            CloudStatus.Text = $"{CloudName} for desktop isn't installed";
            CloudDetail.Text = $"Install the free {CloudName} app and sign in — Orbit picks up its folder automatically, " +
                               "with files available offline or on demand. Then press Check again.";
            CloudOpenBtn.Visibility = Visibility.Collapsed;
            CloudGetBtn.Visibility = Visibility.Visible;
            CloudGetBtn.Content = $"Get {CloudName} for desktop";
        }

        var rclone = CloudMount.FindRclone();
        bool winfsp = CloudMount.WinFspInstalled();
        RcloneGetBtn.Visibility = rclone == null ? Visibility.Visible : Visibility.Collapsed;
        WinFspGetBtn.Visibility = winfsp ? Visibility.Collapsed : Visibility.Visible;
        RcloneGetPanel.Visibility = rclone == null || !winfsp ? Visibility.Visible : Visibility.Collapsed;
        RcloneStatus.Text = rclone != null && winfsp
            ? $"rclone and WinFsp are installed. “Sign in & mount” opens {CloudName}'s sign-in page in your browser, then mounts it as a drive letter while Orbit runs."
            : "Needs two free tools: rclone (connects to the cloud) and WinFsp (makes it appear as a drive). " +
              (rclone == null && !winfsp ? "Neither is installed yet." : rclone == null ? "rclone isn't installed yet." : "WinFsp isn't installed yet.");
        OkBtn.Content = "Sign in & mount";
        OkBtn.IsEnabled = rclone != null && winfsp;
        OkBtn.ToolTip = OkBtn.IsEnabled ? null : "Install rclone and WinFsp first";
    }

    void CloudOpen_Click(object sender, RoutedEventArgs e)
    {
        OpenPath = cloudFolder;
        DialogResult = true;
    }

    void CloudGet_Click(object sender, RoutedEventArgs e) =>
        AppInfo.OpenUrl(Proto == "gdrive" ? "https://www.google.com/drive/download/" : "https://www.dropbox.com/install");

    void CloudRecheck_Click(object sender, RoutedEventArgs e) => RefreshCloud();
    void RcloneGet_Click(object sender, RoutedEventArgs e) => AppInfo.OpenUrl("https://rclone.org/downloads/");
    void WinFspGet_Click(object sender, RoutedEventArgs e) => AppInfo.OpenUrl("https://winfsp.dev/rel/");

    void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose a private key",
            InitialDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
            Filter = "All files|*.*"
        };
        if (dlg.ShowDialog(this) == true) KeyBox.Text = dlg.FileName;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        Password = PwBox.Password;
        if (passwordOnly)
        {
            if (SignInUserPanel.Visibility == Visibility.Visible)
            {
                SignInUser = SignInUserBox.Text.Trim();
                if (SignInUser.Length == 0) { ShowError("Enter your user name on the server."); return; }
            }
            DialogResult = true;
            return;
        }

        if (IsCloud)
        {
            Result = new ServerEntry
            {
                Protocol = Proto,
                Host = Proto == "gdrive" ? "orbit-gdrive" : "orbit-dropbox", // rclone remote name
                Name = string.IsNullOrWhiteSpace(NameBox.Text) ? null : NameBox.Text.Trim()
            };
            Save = SaveCheck.IsChecked == true;
            DialogResult = true;
            return;
        }

        var host = HostBox.Text.Trim();
        var proto = Proto;
        bool https = HttpsCheck.IsChecked == true;
        // Be forgiving about pasted addresses like \\nas\share, smb://nas/share, ssh://user@host or user@host —
        // and let an address that says what it is pick the type, so an SSH server is never saved as SMB.
        foreach (var (prefix, kind, secure) in new[]
                 {
                     ("smb://", "smb", false), ("sftp://", "sftp", false), ("ssh://", "sftp", false), ("https://", "webdav", true),
                     ("http://", "webdav", false), ("davs://", "webdav", true), ("dav://", "webdav", false)
                 })
            if (host.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                host = host[prefix.Length..];
                proto = kind;
                if (kind == "webdav") https = secure;
                break;
            }
        if (host.StartsWith(@"\\")) proto = "smb";
        host = host.TrimStart('\\', '/');
        string extraPath = null;
        int slash = host.IndexOfAny(new[] { '/', '\\' });
        if (slash > 0) { extraPath = host[(slash + 1)..]; host = host[..slash]; }
        string user = UserBox.Text.Trim();
        int at = host.LastIndexOf('@');
        if (at > 0) { if (user.Length == 0) user = host[..at]; host = host[(at + 1)..]; }
        int port = 0;
        int colon = host.LastIndexOf(':');
        if (colon > 0 && int.TryParse(host[(colon + 1)..], out var hp)) { port = hp; host = host[..colon]; }

        if (host.Length == 0) { ShowError("Enter the server's name or IP address."); return; }
        if (PortBox.Text.Trim().Length > 0 && (!int.TryParse(PortBox.Text.Trim(), out port) || port <= 0 || port > 65535))
        {
            ShowError("The port must be a number between 1 and 65535.");
            return;
        }
        // Port 22 is SSH — Windows file sharing can't use it, so that's an SFTP server.
        if (proto == "smb" && port == 22) proto = "sftp";
        if (proto == "smb" && port == 445) port = 0;
        if (proto == "smb" && port > 0)
        {
            ShowError($"Windows file sharing (SMB) always uses port 445, so leave Port empty. If {host} is an SSH server, choose SFTP (SSH) instead.");
            return;
        }

        var path = PathBox.Text.Trim();
        if (path.Length == 0 && extraPath != null) path = extraPath;
        if (proto == "smb" && path.Trim('/', '\\').Length == 0)
        {
            ShowError("Enter the share name, e.g. \"public\" or \"media/movies\".");
            return;
        }

        Result = new ServerEntry
        {
            Protocol = proto,
            Host = host,
            Port = port,
            User = user,
            Path = path,
            Https = https,
            KeyFile = string.IsNullOrWhiteSpace(KeyBox.Text) ? null : KeyBox.Text.Trim(),
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? null : NameBox.Text.Trim()
        };
        Save = SaveCheck.IsChecked == true;
        DialogResult = true;
    }

    void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource == this) DragMove();
    }
}
