using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OrbitSetup;

/// <summary>The installer window, styled like Orbit (Catppuccin Mocha).</summary>
sealed class SetupForm : Form
{
    static readonly Color Bg = ColorTranslator.FromHtml("#1E1E2E"), Panel = ColorTranslator.FromHtml("#181825"),
        Line = ColorTranslator.FromHtml("#45475A"), Text1 = ColorTranslator.FromHtml("#CDD6F4"), Muted = ColorTranslator.FromHtml("#A6ADC8"),
        Accent = ColorTranslator.FromHtml("#CBA6F7"), Error = ColorTranslator.FromHtml("#F38BA8");

    readonly TextBox pathBox = new();
    readonly CheckBox desktopBox = new(), launchBox = new();
    readonly ProgressBar bar = new();
    readonly Label status = new(), subtitle = new();
    readonly Button installBtn = new(), cancelBtn = new(), browseBtn = new();
    bool busy, done;

    public SetupForm()
    {
        Text = "Orbit Setup";
        Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
        AutoScaleMode = AutoScaleMode.None; // laid out at 96 dpi, scaled once below (fonts in points scale by themselves)
        ClientSize = new Size(560, 392);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = Text1;
        Font = new Font("Segoe UI", 9.75f);

        var logo = new PictureBox { Location = new Point(28, 26), Size = new Size(64, 64), SizeMode = PictureBoxSizeMode.Zoom };
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png")) if (s != null) logo.Image = Image.FromStream(s);
        var title = new Label { Text = "Orbit", Location = new Point(106, 26), AutoSize = true, Font = new Font("Segoe UI Semibold", 20f), ForeColor = Text1 };
        subtitle.Location = new Point(109, 66); subtitle.AutoSize = true; subtitle.ForeColor = Muted;

        var (oldDir, oldVersion) = Installer.Existing();
        subtitle.Text = oldDir == null
            ? $"Version {Installer.Version} · a fast, modern file manager for Windows"
            : $"Version {oldVersion ?? "?"} is installed — this {(oldVersion == Installer.Version ? "reinstalls" : "updates it to")} {Installer.Version}";

        var locLabel = new Label { Text = "Install location", Location = new Point(28, 120), AutoSize = true, ForeColor = Muted };
        pathBox.Location = new Point(28, 143); pathBox.Size = new Size(398, 28);
        pathBox.Text = oldDir ?? Installer.DefaultDir;
        pathBox.BackColor = Panel; pathBox.ForeColor = Text1; pathBox.BorderStyle = BorderStyle.FixedSingle;
        Style(browseBtn, "Browse…", false); browseBtn.Location = new Point(436, 141); browseBtn.Size = new Size(96, 30);
        browseBtn.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Choose where to install Orbit (an \"Orbit\" folder is created inside)", ShowNewFolderButton = true };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                pathBox.Text = Path.GetFileName(dlg.SelectedPath).Equals("Orbit", StringComparison.OrdinalIgnoreCase) ? dlg.SelectedPath : Path.Combine(dlg.SelectedPath, "Orbit");
        };

        Check(desktopBox, "Create a desktop shortcut", new Point(28, 188), oldDir == null || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Orbit.lnk")));
        Check(launchBox, "Open Orbit when setup finishes", new Point(28, 216), true);

        var note = new Label
        {
            Text = "Installs for your account only — no administrator rights needed. A Start menu shortcut is added, and you can uninstall any time from Settings › Apps.",
            Location = new Point(28, 252), Size = new Size(504, 40), ForeColor = Muted
        };

        bar.Location = new Point(28, 302); bar.Size = new Size(504, 6); bar.Style = ProgressBarStyle.Continuous; bar.Visible = false;
        status.Location = new Point(28, 312); status.Size = new Size(504, 22); status.ForeColor = Muted;

        Style(cancelBtn, "Cancel", false); cancelBtn.Location = new Point(330, 344); cancelBtn.Size = new Size(96, 32);
        cancelBtn.Click += (_, _) => { if (!busy) Close(); };
        Style(installBtn, oldDir == null ? "Install" : "Update", true); installBtn.Location = new Point(436, 344); installBtn.Size = new Size(96, 32);
        installBtn.Click += async (_, _) => await InstallClicked();
        AcceptButton = installBtn;
        CancelButton = cancelBtn;

        Controls.AddRange(new Control[] { logo, title, subtitle, locLabel, pathBox, browseBtn, desktopBox, launchBox, note, bar, status, cancelBtn, installBtn });
        float f; using (var g = CreateGraphics()) f = g.DpiX / 96f; // the process is DPI-aware (setup.manifest)
        if (Math.Abs(f - 1) > 0.01) Scale(new SizeF(f, f));
    }

    void Style(Button b, string text, bool primary)
    {
        b.Text = text;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = primary ? Accent : Line;
        b.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(Accent) : ColorTranslator.FromHtml("#313244");
        b.BackColor = primary ? Accent : Panel;
        b.ForeColor = primary ? Bg : Text1;
        b.Font = primary ? new Font("Segoe UI Semibold", 9.75f) : Font;
        b.Cursor = Cursors.Hand;
    }

    void Check(CheckBox c, string text, Point at, bool on)
    {
        c.Text = " " + text; c.Location = at; c.AutoSize = true; c.Checked = on; c.ForeColor = Text1; c.FlatStyle = FlatStyle.System;
    }

    async Task InstallClicked()
    {
        if (done) { Close(); return; }
        if (busy) return;
        busy = true;
        foreach (Control c in new Control[] { pathBox, browseBtn, desktopBox, launchBox, installBtn, cancelBtn }) c.Enabled = false;
        bar.Visible = true;
        status.ForeColor = Muted;
        var dir = pathBox.Text.Trim();
        bool desktop = desktopBox.Checked, launch = launchBox.Checked;
        try
        {
            await Task.Run(() => Installer.Install(dir, desktop,
                (p, msg) => BeginInvoke(new Action(() => { bar.Value = (int)Math.Round(p * 100); status.Text = msg; })),
                q => (bool)Invoke(new Func<bool>(() => MessageBox.Show(this, q, "Orbit Setup", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes))));
            done = true;
            status.ForeColor = Accent;
            status.Text = "Orbit is installed. You'll find it in the Start menu.";
            installBtn.Text = "Finish";
            installBtn.Enabled = true;
            if (launch)
            {
                try { Process.Start(new ProcessStartInfo(Path.Combine(Path.GetFullPath(dir), "Orbit.exe")) { UseShellExecute = true, WorkingDirectory = dir }); } catch { }
                Close();
            }
        }
        catch (OperationCanceledException)
        {
            status.Text = "Setup was cancelled — nothing was changed.";
            Reset();
        }
        catch (Exception ex)
        {
            status.ForeColor = Error;
            status.Text = ex is UnauthorizedAccessException ? "Orbit isn't allowed to write to that folder. Choose another one." : ex.Message;
            Reset();
        }
        finally { busy = false; }
    }

    void Reset()
    {
        bar.Visible = false;
        foreach (Control c in new Control[] { pathBox, browseBtn, desktopBox, launchBox, installBtn, cancelBtn }) c.Enabled = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); } catch { } // dark title bar
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (busy) e.Cancel = true;
        base.OnFormClosing(e);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
