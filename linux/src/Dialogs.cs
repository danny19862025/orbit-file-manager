using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Orbit;

/// <summary>Themed message boxes (Avalonia has none): a title, a message and a row of buttons.</summary>
public static class Dialog
{
    /// <summary>Shows the dialog and returns the index of the pressed button (-1 when closed), and the check box state.</summary>
    public static async Task<(int Choice, bool Checked)> Show(Window owner, string title, string message, string[] buttons,
        int primary = 0, bool warning = false, string checkText = null, int cancel = -1)
    {
        int result = -1;
        var w = new OrbitWindow { Width = 470, SizeToContent = SizeToContent.Height, CanResize = false, Title = title, ShowInTaskbar = owner == null };
        var stack = new StackPanel { Margin = new Thickness(22, 0, 22, 20) };
        var head = new DockPanel { Height = 44 };
        var glyph = new TextBlock { Text = warning ? "" : "", Classes = { "icon" }, FontSize = 15, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        glyph.Bind(TextBlock.ForegroundProperty, glyph.GetResourceObservable("AccentBrush"));
        DockPanel.SetDock(glyph, Dock.Left);
        head.Children.Add(glyph);
        head.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        w.DragArea(head);
        stack.Children.Add(head);
        var msg = new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), LineHeight = 20 };
        stack.Children.Add(msg);
        CheckBox check = null;
        if (checkText != null)
        {
            check = new CheckBox { Content = checkText, Classes = { "check" }, Margin = new Thickness(0, 14, 0, 0) };
            stack.Children.Add(check);
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0), Spacing = 10 };
        for (int i = 0; i < buttons.Length; i++)
        {
            int idx = i;
            var b = new Button { Content = buttons[i], Classes = { "dlg" } };
            if (i == primary) b.Classes.Add("primary");
            b.Click += (_, _) => { result = idx; w.Close(); };
            row.Children.Add(b);
            if (i == primary) w.Opened += (_, _) => b.Focus();
        }
        stack.Children.Add(row);
        w.SetBody(stack);
        w.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { result = cancel; w.Close(); e.Handled = true; }
            else if (e.Key == Key.Enter) { result = primary; w.Close(); e.Handled = true; }
        };
        if (owner != null && owner.IsVisible) await w.ShowDialog(owner);
        else
        {
            var tcs = new TaskCompletionSource();
            w.Closed += (_, _) => tcs.TrySetResult();
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.Show();
            await tcs.Task;
        }
        return (result, check?.IsChecked == true);
    }

    public static async Task<bool> Ask(Window owner, string title, string message, string yes = "Yes", string no = "No", bool warning = false, bool yesIsDefault = true)
    {
        var (c, _) = await Show(owner, title, message, new[] { no, yes }, primary: yesIsDefault ? 1 : 0, warning: warning, cancel: 0);
        return c == 1;
    }

    public static Task Info(Window owner, string title, string message, bool warning = false) =>
        Show(owner, title, message, new[] { "OK" }, 0, warning, cancel: 0);

    /// <summary>From a worker thread: shows the dialog on the UI thread and waits for the answer.</summary>
    public static T OnUi<T>(Func<Task<T>> f) => Dispatcher.UIThread.InvokeAsync(f).GetAwaiter().GetResult();

    /// <summary>A one-line text prompt (e.g. a password). Returns null when cancelled.</summary>
    public static async Task<string> Prompt(Window owner, string title, string message, string label, bool password, string initial = "")
    {
        string result = null;
        var w = new OrbitWindow { Width = 440, SizeToContent = SizeToContent.Height, CanResize = false, Title = title };
        var stack = new StackPanel { Margin = new Thickness(22, 0, 22, 20) };
        var head = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 15, Height = 44, Padding = new Thickness(0, 13, 0, 0) };
        w.DragArea(head);
        stack.Children.Add(head);
        if (!string.IsNullOrEmpty(message)) stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) });
        var lab = new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(0, 10, 0, 4) };
        lab.Bind(TextBlock.ForegroundProperty, lab.GetResourceObservable("MutedBrush"));
        stack.Children.Add(lab);
        var box = new TextBox { Classes = { "field", "input" }, Text = initial };
        if (password) box.PasswordChar = '•';
        stack.Children.Add(box);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0), Spacing = 10 };
        var cancel = new Button { Content = "Cancel", Classes = { "dlg" } };
        cancel.Click += (_, _) => w.Close();
        var ok = new Button { Content = "OK", Classes = { "dlg", "primary" } };
        ok.Click += (_, _) => { result = box.Text ?? ""; w.Close(); };
        row.Children.Add(cancel);
        row.Children.Add(ok);
        stack.Children.Add(row);
        w.SetBody(stack);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { result = box.Text ?? ""; w.Close(); e.Handled = true; }
            else if (e.Key == Key.Escape) { w.Close(); e.Handled = true; }
        };
        w.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await w.ShowDialog(owner);
        return result;
    }
}

/// <summary>
/// Base for Orbit's own windows: no system title bar (Orbit draws its own, like on Windows), a 1 px theme border,
/// the theme's background (translucent when glass is on) and dragging by the title area.
/// </summary>
public class OrbitWindow : Window
{
    readonly Border frame;

    public OrbitWindow()
    {
        SystemDecorations = SystemDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Icon = MainWindow.AppIcon;
        frame = new Border { BorderThickness = new Thickness(1) };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("LineBrush"));
        Content = frame;
        this.Bind(BackgroundProperty, this.GetResourceObservable("ContentBg"));
        ApplyGlass();
        Themes.Changed += ApplyGlass;
        Closed += (_, _) => Themes.Changed -= ApplyGlass;
    }

    void ApplyGlass()
    {
        TransparencyLevelHint = AppSettings.Current.Translucent
            ? new[] { WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None }
            : new[] { WindowTransparencyLevel.None };
    }

    public void SetBody(Control body) => frame.Child = body;

    /// <summary>Lets the user move the window by dragging this element (and double-click it to maximize, when resizable).</summary>
    public void DragArea(Control c)
    {
        switch (c)
        {
            case Panel p when p.Background == null: p.Background = Brushes.Transparent; break;
            case Border b when b.Background == null: b.Background = Brushes.Transparent; break;
            case TextBlock t when t.Background == null: t.Background = Brushes.Transparent; break;
        }
        c.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(true) != null) return;
            if (e.ClickCount == 2 && CanResize) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; return; }
            BeginMoveDrag(e);
        };
    }
}
