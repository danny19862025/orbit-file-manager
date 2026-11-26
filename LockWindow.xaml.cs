using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Orbit;

/// <summary>Asks for the password and runs <see cref="FolderLock"/> with progress. The password only lives in the PasswordBox.</summary>
public partial class LockWindow : Window
{
    readonly MainWindow main;
    readonly string path;
    readonly bool locking;
    CancellationTokenSource cts;
    bool busy;

    public LockWindow(MainWindow owner, string path, bool locking)
    {
        InitializeComponent();
        Owner = owner;
        main = owner;
        this.path = path;
        this.locking = locking;
        var name = Path.GetFileName(path.TrimEnd('\\'));

        if (locking)
        {
            TitleText.Text = "Lock folder";
            Headline.Text = $"Lock “{name}” with a password";
            Explain.Text = $"Orbit encrypts the folder into one file, “{name}{FolderLock.Ext}”, with AES-256. You'll need the password to open it again.";
            RecycleOriginal.Content = "Move the original folder to the Recycle Bin afterwards";
            WarningText.Text = "Orbit can't recover a forgotten password — nobody can. Empty the Recycle Bin afterwards so no unlocked copy is left behind.";
            OkBtn.Content = "Lock";
        }
        else
        {
            TitleGlyph.Text = "";
            TitleText.Text = "Unlock";
            Headline.Text = $"Unlock “{name}”";
            Explain.Text = "Type the password this folder was locked with. Orbit restores it next to the locked file.";
            ConfirmPanel.Visibility = Visibility.Collapsed;
            RecycleOriginal.Content = "Move the locked file to the Recycle Bin afterwards";
            Warning.Visibility = Visibility.Collapsed;
            OkBtn.Content = "Unlock";
        }
        Pwd_Changed(null, null);
        Loaded += (_, _) => Pwd.Focus();
    }

    void Pwd_Changed(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        if (!locking) { OkBtn.IsEnabled = Pwd.Password.Length > 0; return; }
        var p = Pwd.Password;
        string msg;
        bool ok = false;
        if (p.Length == 0) msg = "Use at least 8 characters. A few unrelated words make a strong, memorable password.";
        else if (p.Length < 8) msg = $"Too short — {8 - p.Length} more character{(8 - p.Length == 1 ? "" : "s")} needed.";
        else if (Pwd2.Password.Length > 0 && Pwd2.Password != p) msg = "The two passwords don't match.";
        else
        {
            int classes = new[] { p.Any(char.IsLower), p.Any(char.IsUpper), p.Any(char.IsDigit), p.Any(c => !char.IsLetterOrDigit(c)) }.Count(b => b);
            msg = p.Length >= 16 || (p.Length >= 12 && classes >= 3) ? "Strong password." :
                  p.Length >= 12 || classes >= 3 ? "Good password." : "OK — a longer password would be stronger.";
            ok = Pwd2.Password == p;
            if (!ok) msg += " Type it again to confirm.";
        }
        Strength.Text = msg;
        OkBtn.IsEnabled = ok;
    }

    async void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !OkBtn.IsEnabled) return;
        busy = true;
        cts = new CancellationTokenSource();
        var password = Pwd.Password;
        bool recycle = RecycleOriginal.IsChecked == true;
        Fields.IsEnabled = OkBtn.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        Busy.Visibility = Visibility.Visible;
        Bar.IsIndeterminate = true;
        BusyText.Text = "Preparing the key…";
        var progress = new Progress<FolderLock.Progress>(p =>
        {
            Bar.IsIndeterminate = false;
            Bar.Value = p.Total > 0 ? (double)p.Done / p.Total : 1;
            BusyText.Text = p.Current;
        });
        try
        {
            var token = cts.Token;
            var result = await Task.Run(() => locking
                ? FolderLock.Lock(path, password, progress, token)
                : FolderLock.Unlock(path, password, progress, token));
            var hwnd = new WindowInteropHelper(main).Handle;
            if (recycle) FileOps.Delete(new[] { path.TrimEnd('\\') }, true, hwnd);
            main.ShowInOrbit(result);
            main.ToastFromSpace(locking ? $"Locked “{Path.GetFileName(result)}”" : $"Unlocked “{Path.GetFileName(result)}”");
            busy = false;
            Close();
        }
        catch (OperationCanceledException) { busy = false; Close(); }
        catch (Exception ex)
        {
            busy = false;
            Busy.Visibility = Visibility.Collapsed;
            Fields.IsEnabled = true;
            ErrorText.Text = ex is UnauthorizedAccessException ? "Orbit isn't allowed to read or write some of these files." : ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            Pwd_Changed(null, null);
            ErrorText.Visibility = Visibility.Visible;
            if (ex is FolderLock.WrongPasswordException) { Pwd.SelectAll(); Pwd.Focus(); }
        }
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (busy) { cts?.Cancel(); BusyText.Text = "Stopping…"; return; }
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; cts?.Cancel(); }
        base.OnClosing(e);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource == this) DragMove();
    }
}
