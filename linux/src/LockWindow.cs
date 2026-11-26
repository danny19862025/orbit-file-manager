using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Orbit;

/// <summary>Asks for the password and runs <see cref="FolderLock"/> with progress. The password only lives in the text box.</summary>
public sealed class LockWindow : OrbitWindow
{
    readonly MainWindow main;
    readonly string path;
    readonly bool locking;
    CancellationTokenSource cts;
    bool busy;

    readonly TextBox pwd, pwd2;
    readonly TextBlock strength, errorText, busyText;
    readonly StackPanel fields, busyPanel;
    readonly CheckBox recycleOriginal;
    readonly ProgressBar bar;
    readonly Button okBtn;

    public LockWindow(MainWindow owner, string path, bool locking)
    {
        main = owner;
        this.path = path;
        this.locking = locking;
        var name = Path.GetFileName(path.TrimEnd('/'));
        Title = locking ? "Lock folder" : "Unlock";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;

        var root = new StackPanel();
        root.Children.Add(Ui.TitleBar(this, locking ? "" : "", Title, out _, out _, Cancel));
        var body = new StackPanel { Margin = new Thickness(26, 6, 26, 22) };
        body.Children.Add(new TextBlock { Text = locking ? $"Lock “{name}” with a password" : $"Unlock “{name}”", FontSize = 19, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock
        {
            Text = locking ? $"Orbit encrypts the folder into one file, “{name}{FolderLock.Ext}”, with AES-256. You'll need the password to open it again."
                           : "Type the password this folder was locked with. Orbit restores it next to the locked file.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 16)
        }.Brush(TextBlock.ForegroundProperty, "MutedBrush"));

        fields = new StackPanel();
        fields.Children.Add(new TextBlock { Text = "Password", Margin = new Thickness(0, 0, 0, 5) }.Brush(TextBlock.ForegroundProperty, "MutedBrush"));
        pwd = Ui.Field();
        pwd.PasswordChar = '•';
        pwd.Brush(TextBox.BackgroundProperty, "PanelBg");
        fields.Children.Add(pwd);
        var confirm = new StackPanel { IsVisible = locking };
        confirm.Children.Add(new TextBlock { Text = "Type it again", Margin = new Thickness(0, 12, 0, 5) }.Brush(TextBlock.ForegroundProperty, "MutedBrush"));
        pwd2 = Ui.Field();
        pwd2.PasswordChar = '•';
        confirm.Children.Add(pwd2);
        strength = new TextBlock { FontSize = 12, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap }.Brush(TextBlock.ForegroundProperty, "MutedBrush");
        confirm.Children.Add(strength);
        fields.Children.Add(confirm);
        recycleOriginal = Ui.Check(locking ? "Move the original folder to the Trash afterwards" : "Move the locked file to the Trash afterwards", true);
        recycleOriginal.Margin = new Thickness(0, 16, 0, 0);
        fields.Children.Add(recycleOriginal);
        body.Children.Add(fields);

        if (locking)
        {
            var warn = new DockPanel();
            var g = Ui.Glyph("", 14, "AccentBrush", new Thickness(0, 2, 10, 0));
            g.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(g, Dock.Left);
            warn.Children.Add(g);
            warn.Children.Add(new TextBlock { Text = "Orbit can't recover a forgotten password — nobody can. Empty the Trash afterwards so no unlocked copy is left behind.", TextWrapping = TextWrapping.Wrap, FontSize = 12.5 }
                .Brush(TextBlock.ForegroundProperty, "MutedBrush"));
            body.Children.Add(new Border { Margin = new Thickness(0, 16, 0, 0), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), BorderThickness = new Thickness(1), Child = warn }
                .Brush(Border.BackgroundProperty, "PanelBg").Brush(Border.BorderBrushProperty, "LineBrush"));
        }

        busyPanel = new StackPanel { IsVisible = false, Margin = new Thickness(0, 16, 0, 0) };
        bar = new ProgressBar { Height = 6, MinHeight = 6, Maximum = 1, CornerRadius = new CornerRadius(3) };
        busyPanel.Children.Add(bar);
        busyText = new TextBlock { FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis }.Brush(TextBlock.ForegroundProperty, "MutedBrush");
        busyPanel.Children.Add(busyText);
        body.Children.Add(busyPanel);
        errorText = new TextBlock { Foreground = new SolidColorBrush(Color.Parse("#F38BA8")), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), IsVisible = false };
        body.Children.Add(errorText);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0), Spacing = 10 };
        var cancel = Ui.Dlg("Cancel");
        cancel.Click += (_, _) => Cancel();
        okBtn = Ui.Dlg(locking ? "Lock" : "Unlock", true);
        okBtn.Click += (_, _) => Ok();
        buttons.Children.Add(cancel);
        buttons.Children.Add(okBtn);
        body.Children.Add(buttons);
        root.Children.Add(body);
        SetBody(root);

        pwd.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Pwd_Changed(); };
        pwd2.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Pwd_Changed(); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
            else if (e.Key == Key.Enter) { Ok(); e.Handled = true; }
        };
        Closing += (_, e) => { if (busy) { e.Cancel = true; cts?.Cancel(); } };
        Pwd_Changed();
        Opened += (_, _) => pwd.Focus();
    }

    void Pwd_Changed()
    {
        errorText.IsVisible = false;
        var p = pwd.Text ?? "";
        if (!locking) { okBtn.IsEnabled = p.Length > 0; return; }
        var p2 = pwd2.Text ?? "";
        string msg;
        bool ok = false;
        if (p.Length == 0) msg = "Use at least 8 characters. A few unrelated words make a strong, memorable password.";
        else if (p.Length < 8) msg = $"Too short — {8 - p.Length} more character{(8 - p.Length == 1 ? "" : "s")} needed.";
        else if (p2.Length > 0 && p2 != p) msg = "The two passwords don't match.";
        else
        {
            int classes = new[] { p.Any(char.IsLower), p.Any(char.IsUpper), p.Any(char.IsDigit), p.Any(c => !char.IsLetterOrDigit(c)) }.Count(b => b);
            msg = p.Length >= 16 || (p.Length >= 12 && classes >= 3) ? "Strong password." :
                  p.Length >= 12 || classes >= 3 ? "Good password." : "OK — a longer password would be stronger.";
            ok = p2 == p;
            if (!ok) msg += " Type it again to confirm.";
        }
        strength.Text = msg;
        okBtn.IsEnabled = ok;
    }

    async void Ok()
    {
        if (busy || !okBtn.IsEnabled) return;
        busy = true;
        cts = new CancellationTokenSource();
        var password = pwd.Text ?? "";
        bool recycle = recycleOriginal.IsChecked == true;
        fields.IsEnabled = okBtn.IsEnabled = false;
        errorText.IsVisible = false;
        busyPanel.IsVisible = true;
        bar.IsIndeterminate = true;
        busyText.Text = "Preparing the key…";
        var progress = new Progress<FolderLock.Progress>(p =>
        {
            bar.IsIndeterminate = false;
            bar.Value = p.Total > 0 ? (double)p.Done / p.Total : 1;
            busyText.Text = p.Current;
        });
        try
        {
            var token = cts.Token;
            var result = await Task.Run(() => locking
                ? FolderLock.Lock(path, password, progress, token)
                : FolderLock.Unlock(path, password, progress, token));
            if (recycle) FileOps.Delete(new[] { path.TrimEnd('/') }, true, main);
            main.ShowInOrbit(result);
            main.ToastFromSpace(locking ? $"Locked “{Path.GetFileName(result)}”" : $"Unlocked “{Path.GetFileName(result)}”");
            busy = false;
            Close();
        }
        catch (OperationCanceledException) { busy = false; Close(); }
        catch (Exception ex)
        {
            busy = false;
            busyPanel.IsVisible = false;
            fields.IsEnabled = true;
            Pwd_Changed();
            errorText.Text = ex is UnauthorizedAccessException ? "Orbit isn't allowed to read or write some of these files." : ex.Message;
            errorText.IsVisible = true;
            if (ex is FolderLock.WrongPasswordException) { pwd.SelectAll(); pwd.Focus(); }
        }
    }

    void Cancel()
    {
        if (busy) { cts?.Cancel(); busyText.Text = "Stopping…"; return; }
        Close();
    }
}
