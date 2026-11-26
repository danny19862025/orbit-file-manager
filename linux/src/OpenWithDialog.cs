using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Orbit;

/// <summary>"Open with…": the installed apps that open this kind of file, with an option to make one the default.</summary>
public sealed class OpenWithDialog : OrbitWindow
{
    sealed record AppRow(DesktopApps App, string Name, IImage Icon, string Note);

    public OpenWithDialog(string path)
    {
        var mime = Mime.ForName(Path.GetFileName(path));
        var def = DesktopApps.DefaultFor(mime);
        var apps = DesktopApps.For(mime);
        var others = DesktopApps.All().Where(a => !apps.Contains(a) && a.MimeTypes.Length > 0).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        Title = "Open with";
        Width = 480;
        Height = 560;
        CanResize = false;
        var root = new DockPanel();
        var head = Ui.TitleBar(this, "", "Open with", out _, out _, Close);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        var intro = new StackPanel { Margin = new Thickness(22, 0, 22, 10) };
        intro.Children.Add(new TextBlock { Text = Path.GetFileName(path), FontSize = 16, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        intro.Children.Add(Ui.Sub($"{IconService.TypeName(Path.GetExtension(path).ToLowerInvariant(), false, Path.GetFileName(path))}  ·  {mime}"));
        DockPanel.SetDock(intro, Dock.Top);
        root.Children.Add(intro);

        var bottom = new StackPanel { Margin = new Thickness(22, 10, 22, 20) };
        var always = Ui.Check($"Always use this app for {(Path.GetExtension(path) is { Length: > 0 } e ? e + " files" : "this kind of file")}");
        bottom.Children.Add(always);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Spacing = 10 };
        var cancel = Ui.Dlg("Cancel");
        cancel.Click += (_, _) => Close();
        var open = Ui.Dlg("Open", true);
        buttons.Children.Add(cancel);
        buttons.Children.Add(open);
        bottom.Children.Add(buttons);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        var rows = apps.Select(a => new AppRow(a, a.Name, AppIcon(a), def != null && a.Id == def.Id ? "Default" : null))
            .Concat(others.Select(a => new AppRow(a, a.Name, null, "Other app")))
            .ToList();
        var list = new ListBox { Classes = { "flat", "results" }, SelectionMode = SelectionMode.Single, Margin = new Thickness(14, 0), ItemsSource = rows };
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<AppRow>((r, _) =>
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            var img = new Image { Width = 28, Height = 28, Margin = new Thickness(0, 0, 12, 0), Source = r?.Icon ?? (r == null ? null : AppIcon(r.App)) };
            g.Children.Add(img);
            var t = new TextBlock { Text = r?.Name, VerticalAlignment = VerticalAlignment.Center, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            if (r?.Note != null)
            {
                var n = Ui.Sub(r.Note);
                n.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(n, 2);
                g.Children.Add(n);
            }
            return g;
        });
        if (rows.Count > 0) list.SelectedIndex = 0;
        root.Children.Add(rows.Count == 0 ? Ui.Sub("No installed app says it opens this kind of file.", new Thickness(22)) : list);
        SetBody(root);

        void Go()
        {
            if (list.SelectedItem is not AppRow r) return;
            try
            {
                if (always.IsChecked == true) r.App.SetAsDefault(mime);
                r.App.Launch(new[] { path });
                Close();
            }
            catch (Exception ex) { _ = Dialog.Info(this, "Open with", "Couldn't start " + r.Name + ": " + ex.Message, true); }
        }
        open.Click += (_, _) => Go();
        list.DoubleTapped += (_, _) => Go();
        KeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Escape) { Close(); ev.Handled = true; }
            else if (ev.Key == Key.Enter) { Go(); ev.Handled = true; }
        };
        Opened += (_, _) => list.Focus();
    }

    static IImage AppIcon(DesktopApps a)
    {
        if (string.IsNullOrEmpty(a.IconName)) return IconService.Themed(new[] { "application-x-executable" }, 48);
        if (Path.IsPathRooted(a.IconName) && File.Exists(a.IconName)) return Orbit.Render.IconFile(a.IconName, 48);
        return IconService.Themed(new[] { a.IconName, "application-x-executable" }, 48);
    }
}
