using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Orbit;

/// <summary>"Connect to server" (SMB, SFTP, WebDAV, Google Drive, Dropbox) and the sign-in prompt for servers.</summary>
public sealed class ConnectDialog : OrbitWindow
{
    public ServerEntry Result { get; private set; }
    public string Password { get; private set; }
    public string UserName { get; private set; }
    public bool Save { get; private set; }
    /// <summary>Set when the user chose "Open in Orbit" for an installed Google Drive / Dropbox folder.</summary>
    public string OpenPath { get; private set; }

    readonly bool passwordOnly;
    RadioButton rbSmb, rbSftp, rbDav, rbGdrive, rbDropbox;
    TextBox hostBox, portBox, pathBox, userBox, keyBox, nameBox, pwBox;
    TextBlock pathLabel, protoHint, errorText, pwLabel, cloudStatus, cloudDetail, rcloneStatus, message;
    CheckBox httpsCheck, saveCheck;
    StackPanel serverFields, cloudPanel, keyPanel, passwordPanel, savePanel, fullForm, userPanel;
    Button okBtn, cloudOpenBtn, cloudGetBtn, rcloneGetBtn, fuseGetBtn;
    WrapPanel rcloneGetPanel;
    Ellipse cloudDot;
    string cloudFolder;

    /// <summary>Full "connect to server" form. Pass an entry to edit it.</summary>
    public ConnectDialog(Window owner, ServerEntry edit = null)
    {
        BuildUi(edit != null ? "Edit server" : "Connect to server");
        if (edit != null)
        {
            rbSmb.IsChecked = edit.Protocol == "smb";
            rbSftp.IsChecked = edit.Protocol == "sftp";
            rbDav.IsChecked = edit.Protocol == "webdav";
            rbGdrive.IsChecked = edit.Protocol == "gdrive";
            rbDropbox.IsChecked = edit.Protocol == "dropbox";
            hostBox.Text = edit.Host;
            portBox.Text = edit.Port > 0 ? edit.Port.ToString() : "";
            pathBox.Text = edit.Path;
            userBox.Text = edit.User;
            keyBox.Text = edit.KeyFile;
            httpsCheck.IsChecked = edit.Https;
            nameBox.Text = edit.Name;
            okBtn.Content = "Save and connect";
        }
        Proto_Changed();
        Opened += (_, _) => hostBox.Focus();
    }

    /// <summary>Password prompt for a server (optionally asking for the username too).</summary>
    public ConnectDialog(Window owner, string title, string msg, bool askUser = false, string user = null)
    {
        passwordOnly = true;
        BuildUi(title);
        message.Text = msg;
        message.IsVisible = true;
        fullForm.IsVisible = false;
        savePanel.IsVisible = false;
        passwordPanel.IsVisible = true;
        userPanel.IsVisible = askUser;
        userBox.Text = user ?? Environment.UserName;
        pwLabel.Text = askUser ? "Password (not saved)" : "Password or key passphrase (not saved)";
        Opened += (_, _) => { if (askUser && string.IsNullOrEmpty(user)) userBox.Focus(); else pwBox.Focus(); };
    }

    string Proto => rbSftp.IsChecked == true ? "sftp" : rbDav.IsChecked == true ? "webdav"
                  : rbGdrive.IsChecked == true ? "gdrive" : rbDropbox.IsChecked == true ? "dropbox" : "smb";
    bool IsCloud => Proto is "gdrive" or "dropbox";
    string CloudName => Proto == "gdrive" ? "Google Drive" : "Dropbox";

    static TextBlock Label(string text) => new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(0, 10, 0, 4) }.Brush(TextBlock.ForegroundProperty, "MutedBrush");

    void BuildUi(string title)
    {
        Title = title;
        Width = 580;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        var root = new StackPanel { Margin = new Thickness(22, 0, 22, 20) };
        var head = new DockPanel { Height = 44 };
        var x = Ui.Icon("\uE711", 11);
        x.Margin = new Thickness(0, 0, -12, 0);
        x.Click += (_, _) => Close(false);
        DockPanel.SetDock(x, Dock.Right);
        head.Children.Add(x);
        head.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        DragArea(head);
        root.Children.Add(head);
        message = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 2, 0, 4) }.Brush(TextBlock.ForegroundProperty, "MutedBrush");
        root.Children.Add(message);

        fullForm = new StackPanel();
        rbSmb = Ui.Seg("SMB / Samba", "proto", true);
        rbSftp = Ui.Seg("SFTP (SSH)", "proto");
        rbDav = Ui.Seg("WebDAV", "proto");
        rbGdrive = Ui.Seg("Google Drive", "proto");
        rbDropbox = Ui.Seg("Dropbox", "proto");
        foreach (var r in new[] { rbSmb, rbSftp, rbDav, rbGdrive, rbDropbox }) r.IsCheckedChanged += (_, _) => { if (r.IsChecked == true) Proto_Changed(); };
        var bar = Ui.SegBar(rbSmb, rbSftp, rbDav, rbGdrive, rbDropbox);
        bar.Margin = new Thickness(0, 4, 0, 2);
        fullForm.Children.Add(bar);
        protoHint = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(2, 8, 0, 0) }.Brush(TextBlock.ForegroundProperty, "MutedBrush");
        fullForm.Children.Add(protoHint);

        serverFields = new StackPanel();
        var hostGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,90") };
        var hostCol = new StackPanel();
        hostCol.Children.Add(Label("Server (host name or IP)"));
        hostBox = Ui.Field();
        hostCol.Children.Add(hostBox);
        hostGrid.Children.Add(hostCol);
        var portCol = new StackPanel();
        Grid.SetColumn(portCol, 2);
        portCol.Children.Add(Label("Port"));
        portBox = Ui.Field();
        portCol.Children.Add(portBox);
        hostGrid.Children.Add(portCol);
        serverFields.Children.Add(hostGrid);
        pathLabel = Label("Share and folder");
        serverFields.Children.Add(pathLabel);
        pathBox = Ui.Field();
        serverFields.Children.Add(pathBox);
        fullForm.Children.Add(serverFields);

        keyPanel = new StackPanel();
        keyPanel.Children.Add(Label("Private key file (optional — ~/.ssh keys are tried automatically)"));
        var keyDock = new DockPanel();
        var browse = Ui.Dlg("Browse…");
        browse.MinWidth = 0;
        browse.Margin = new Thickness(8, 0, 0, 0);
        browse.Click += async (_, _) =>
        {
            var opts = new FilePickerOpenOptions { Title = "Choose a private key", AllowMultiple = false };
            try { opts.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(System.IO.Path.Combine(Xdg.Home, ".ssh")); } catch { }
            var r = await StorageProvider.OpenFilePickerAsync(opts);
            if (r.Count > 0 && r[0].TryGetLocalPath() is { } p) keyBox.Text = p;
        };
        DockPanel.SetDock(browse, Dock.Right);
        keyDock.Children.Add(browse);
        keyBox = Ui.Field();
        keyDock.Children.Add(keyBox);
        keyPanel.Children.Add(keyDock);
        httpsCheck = Ui.Check("Use HTTPS", true);
        httpsCheck.Margin = new Thickness(0, 14, 0, 0);

        // Google Drive / Dropbox
        cloudPanel = new StackPanel { IsVisible = false, Margin = new Thickness(0, 12, 0, 0) };
        var c1 = new StackPanel();
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal };
        cloudDot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center };
        statusRow.Children.Add(cloudDot);
        cloudStatus = new TextBlock { FontWeight = FontWeight.SemiBold };
        statusRow.Children.Add(cloudStatus);
        c1.Children.Add(statusRow);
        cloudDetail = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0) }.Brush(TextBlock.ForegroundProperty, "MutedBrush");
        c1.Children.Add(cloudDetail);
        var cbtns = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        cloudOpenBtn = Ui.Dlg("Open in Orbit");
        cloudOpenBtn.Margin = new Thickness(0, 0, 8, 0);
        cloudOpenBtn.Click += (_, _) => { OpenPath = cloudFolder; Close(true); };
        cloudGetBtn = Ui.Dlg("Get the desktop app");
        cloudGetBtn.Margin = new Thickness(0, 0, 8, 0);
        cloudGetBtn.Click += (_, _) => AppInfo.OpenUrl(Proto == "gdrive" ? "https://support.google.com/drive/answer/10838124" : "https://www.dropbox.com/install-linux");
        var recheck = Ui.Dlg("Check again");
        recheck.Click += (_, _) => RefreshCloud();
        cbtns.Children.Add(cloudOpenBtn);
        cbtns.Children.Add(cloudGetBtn);
        cbtns.Children.Add(recheck);
        c1.Children.Add(cbtns);
        cloudPanel.Children.Add(Card(c1));
        var c2 = new StackPanel();
        c2.Children.Add(new TextBlock { Text = "Or mount it as a folder (advanced)", FontWeight = FontWeight.SemiBold });
        rcloneStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0) }.Brush(TextBlock.ForegroundProperty, "MutedBrush");
        c2.Children.Add(rcloneStatus);
        rcloneGetPanel = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        rcloneGetBtn = Ui.Dlg("Get rclone");
        rcloneGetBtn.Margin = new Thickness(0, 0, 8, 0);
        rcloneGetBtn.Click += (_, _) => AppInfo.OpenUrl("https://rclone.org/install/");
        fuseGetBtn = Ui.Dlg("About FUSE");
        fuseGetBtn.Click += (_, _) => AppInfo.OpenUrl("https://github.com/libfuse/libfuse");
        rcloneGetPanel.Children.Add(rcloneGetBtn);
        rcloneGetPanel.Children.Add(fuseGetBtn);
        c2.Children.Add(rcloneGetPanel);
        var card2 = Card(c2);
        card2.Margin = new Thickness(0, 10, 0, 0);
        cloudPanel.Children.Add(card2);
        fullForm.Children.Add(cloudPanel);
        root.Children.Add(fullForm);

        userPanel = new StackPanel();
        userPanel.Children.Add(Label("Username (optional)"));
        userBox = Ui.Field();
        userPanel.Children.Add(userBox);
        root.Children.Add(userPanel);
        root.Children.Add(keyPanel);
        root.Children.Add(httpsCheck);

        passwordPanel = new StackPanel();
        pwLabel = Label("Password (not saved)");
        passwordPanel.Children.Add(pwLabel);
        pwBox = Ui.Field();
        pwBox.PasswordChar = '•';
        passwordPanel.Children.Add(pwBox);
        root.Children.Add(passwordPanel);

        savePanel = new StackPanel();
        savePanel.Children.Add(Label("Name in sidebar (optional)"));
        nameBox = Ui.Field();
        savePanel.Children.Add(nameBox);
        saveCheck = Ui.Check("Save to the Network section", true);
        saveCheck.Margin = new Thickness(0, 14, 0, 0);
        savePanel.Children.Add(saveCheck);
        root.Children.Add(savePanel);

        errorText = new TextBlock { Foreground = new SolidColorBrush(Color.Parse("#F38BA8")), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), IsVisible = false };
        root.Children.Add(errorText);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0), Spacing = 10 };
        var cancel = Ui.Dlg("Cancel");
        cancel.Click += (_, _) => Close(false);
        okBtn = Ui.Dlg("Connect", true);
        okBtn.Click += (_, _) => Ok();
        buttons.Children.Add(cancel);
        buttons.Children.Add(okBtn);
        root.Children.Add(buttons);
        SetBody(root);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(false); e.Handled = true; }
            else if (e.Key == Key.Enter && okBtn.IsEnabled) { Ok(); e.Handled = true; }
        };
    }

    static Border Card(Control child)
    {
        var b = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12), Child = child };
        return b.Brush(Border.BackgroundProperty, "PanelBg").Brush(Border.BorderBrushProperty, "LineBrush");
    }

    void Proto_Changed()
    {
        if (hostBox == null || passwordOnly) return;
        var p = Proto;
        serverFields.IsVisible = !IsCloud;
        userPanel.IsVisible = !IsCloud;
        cloudPanel.IsVisible = IsCloud;
        errorText.IsVisible = false;
        if (IsCloud)
        {
            passwordPanel.IsVisible = false;
            keyPanel.IsVisible = false;
            httpsCheck.IsVisible = false;
            protoHint.Text = $"Use a {CloudName} sync app (recommended), or mount it as a folder with rclone. " +
                             "Either way you sign in on Google's or Dropbox's own page — Orbit never sees your password.";
            RefreshCloud();
            return;
        }
        okBtn.Content = "Connect";
        okBtn.IsEnabled = true;
        ToolTip.SetTip(okBtn, null);
        keyPanel.IsVisible = p == "sftp";
        passwordPanel.IsVisible = true;
        httpsCheck.IsVisible = p == "webdav";
        pathLabel.Text = p switch { "smb" => "Share and folder (e.g. media/movies)", "sftp" => "Folder (blank = home folder)", _ => "Path on server (optional)" };
        protoHint.Text = p switch
        {
            "smb" => "Windows file shares, Samba and most NAS boxes, opened through GVfs like in your file manager. Leave the password empty for guest shares.",
            "sftp" => "Any server you can SSH into. Orbit checks the server's identity the first time and warns you if it ever changes.",
            _ => "WebDAV folders such as Nextcloud, ownCloud or a NAS, opened through GVfs."
        };
    }

    void RefreshCloud()
    {
        var found = CloudFolders.Detect().FirstOrDefault(c => c.Name.StartsWith(CloudName, StringComparison.OrdinalIgnoreCase));
        cloudFolder = found?.Path;
        if (found != null)
        {
            cloudDot.Fill = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
            cloudStatus.Text = $"A {CloudName} folder is set up";
            cloudDetail.Text = $"Your files are at {found.Path}. It's already in the sidebar under Network.";
            cloudOpenBtn.IsVisible = true;
            cloudGetBtn.IsVisible = false;
        }
        else
        {
            cloudDot.Brush(Shape.FillProperty, "FaintBrush");
            cloudStatus.Text = $"No {CloudName} sync folder found";
            cloudDetail.Text = Proto == "gdrive"
                ? "Add your Google account in GNOME Online Accounts or KDE's Online Accounts, or use a sync client such as Insync — Orbit picks up the folder automatically. Then press Check again."
                : "Install the Dropbox app for Linux and sign in — Orbit picks up ~/Dropbox automatically. Then press Check again.";
            cloudOpenBtn.IsVisible = false;
            cloudGetBtn.IsVisible = true;
            cloudGetBtn.Content = Proto == "gdrive" ? "Ways to sync Google Drive" : "Get Dropbox for Linux";
        }

        var rclone = CloudMount.FindRclone();
        bool fuse = CloudMount.FuseInstalled();
        rcloneGetBtn.IsVisible = rclone == null;
        fuseGetBtn.IsVisible = !fuse;
        rcloneGetPanel.IsVisible = rclone == null || !fuse;
        rcloneStatus.Text = rclone != null && fuse
            ? $"rclone and FUSE are installed. “Sign in & mount” opens {CloudName}'s sign-in page in your browser, then mounts it as a folder while Orbit runs."
            : "Needs two free tools: rclone (connects to the cloud) and FUSE (makes it appear as a folder; install fuse3). " +
              (rclone == null && !fuse ? "Neither is installed yet." : rclone == null ? "rclone isn't installed yet." : "FUSE isn't installed yet.");
        okBtn.Content = "Sign in & mount";
        okBtn.IsEnabled = rclone != null && fuse;
        ToolTip.SetTip(okBtn, okBtn.IsEnabled ? null : "Install rclone and FUSE first");
    }

    void Ok()
    {
        Password = string.IsNullOrEmpty(pwBox.Text) ? null : pwBox.Text;
        UserName = string.IsNullOrWhiteSpace(userBox.Text) ? null : userBox.Text.Trim();
        if (passwordOnly)
        {
            Password ??= "";
            Close(true);
            return;
        }

        if (IsCloud)
        {
            Result = new ServerEntry
            {
                Protocol = Proto,
                Host = Proto == "gdrive" ? "orbit-gdrive" : "orbit-dropbox", // rclone remote name
                Name = string.IsNullOrWhiteSpace(nameBox.Text) ? null : nameBox.Text.Trim()
            };
            Save = saveCheck.IsChecked == true;
            Close(true);
            return;
        }

        var host = (hostBox.Text ?? "").Trim();
        // Be forgiving about pasted addresses like \\nas\share, smb://nas/share or user@host
        foreach (var prefix in new[] { "smb://", "sftp://", "ssh://", "https://", "http://", "davs://", "dav://" })
            if (host.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) host = host[prefix.Length..];
        host = host.TrimStart('\\', '/');
        string extraPath = null;
        int slash = host.IndexOfAny(new[] { '/', '\\' });
        if (slash > 0) { extraPath = host[(slash + 1)..]; host = host[..slash]; }
        string user = (userBox.Text ?? "").Trim();
        int at = host.LastIndexOf('@');
        if (at > 0) { if (user.Length == 0) user = host[..at]; host = host[(at + 1)..]; }
        int port = 0;
        int colon = host.LastIndexOf(':');
        if (colon > 0 && int.TryParse(host[(colon + 1)..], out var hp)) { port = hp; host = host[..colon]; }

        if (host.Length == 0) { ShowError("Enter the server's name or IP address."); return; }
        var portText = (portBox.Text ?? "").Trim();
        if (portText.Length > 0 && (!int.TryParse(portText, out port) || port <= 0 || port > 65535))
        {
            ShowError("The port must be a number between 1 and 65535.");
            return;
        }
        var path = (pathBox.Text ?? "").Trim();
        if (path.Length == 0 && extraPath != null) path = extraPath.Replace('\\', '/');
        if (Proto == "smb" && path.Trim('/', '\\').Length == 0)
        {
            ShowError("Enter the share name, e.g. \"public\" or \"media/movies\".");
            return;
        }

        Result = new ServerEntry
        {
            Protocol = Proto,
            Host = host,
            Port = port,
            User = user,
            Path = path,
            Https = httpsCheck.IsChecked == true,
            KeyFile = string.IsNullOrWhiteSpace(keyBox.Text) ? null : keyBox.Text.Trim(),
            Name = string.IsNullOrWhiteSpace(nameBox.Text) ? null : nameBox.Text.Trim()
        };
        Save = saveCheck.IsChecked == true;
        Close(true);
    }

    void ShowError(string msg)
    {
        errorText.Text = msg;
        errorText.IsVisible = true;
    }
}
