using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Orbit;

/// <summary>Small builders for Orbit's own windows, matching the styles in App.axaml.</summary>
public static class Ui
{
    public static T Brush<T>(this T c, AvaloniaProperty p, string key) where T : Control
    {
        c.Bind(p, c.GetResourceObservable(key));
        return c;
    }

    public static TextBlock Text(string text, double size = 13, FontWeight? weight = null, string brush = null, bool wrap = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeight.Normal, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (brush != null) t.Brush(TextBlock.ForegroundProperty, brush);
        return t;
    }

    public static TextBlock H1(string text) => new() { Text = text, FontSize = 20, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) };

    public static TextBlock H2(string text, double top = 22)
    {
        var t = new TextBlock { Text = text, FontSize = 13.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, top, 0, 10) };
        return t.Brush(TextBlock.ForegroundProperty, "SectionBrush");
    }

    public static TextBlock Sub(string text, Thickness? margin = null)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0) };
        return t.Brush(TextBlock.ForegroundProperty, "MutedBrush");
    }

    public static TextBlock Glyph(string glyph, double size = 15, string brush = null, Thickness? margin = null)
    {
        var t = new TextBlock { Text = glyph, Classes = { "icon" }, FontSize = size, VerticalAlignment = VerticalAlignment.Center, Margin = margin ?? new Thickness(0) };
        if (brush != null) t.Brush(TextBlock.ForegroundProperty, brush);
        return t;
    }

    /// <summary>A panel-coloured rounded row (Settings).</summary>
    public static Border Row(Control child, Thickness? padding = null, Thickness? margin = null)
    {
        var b = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = padding ?? new Thickness(14, 12), Margin = margin ?? new Thickness(0, 0, 0, 8), Child = child };
        b.Brush(Border.BackgroundProperty, "PanelBg");
        b.Brush(Border.BorderBrushProperty, "LineBrush");
        return b;
    }

    /// <summary>A row with a control on the right and a title (+ optional subtitle) on the left.</summary>
    public static Border SettingRow(string title, string sub, Control right)
    {
        var dock = new DockPanel();
        right.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = title });
        if (sub != null) left.Children.Add(Sub(sub));
        dock.Children.Add(left);
        return Row(dock);
    }

    public static CheckBox Check(string content = null, bool? isChecked = false) => new() { Classes = { "check" }, Content = content, IsChecked = isChecked };

    public static Button Dlg(string content, bool primary = false)
    {
        var b = new Button { Content = content, Classes = { "dlg" } };
        if (primary) b.Classes.Add("primary");
        return b;
    }

    public static Button Icon(string glyph, double size = 15, string tip = null)
    {
        var b = new Button { Content = glyph, Classes = { "icon" }, FontSize = size };
        if (tip != null) ToolTip.SetTip(b, tip);
        return b;
    }

    public static RadioButton Seg(string content, string group, bool isChecked = false, object tag = null) =>
        new() { Content = content, GroupName = group, IsChecked = isChecked, Classes = { "seg" }, Tag = tag };

    /// <summary>The rounded segmented-control background around a row of Seg buttons.</summary>
    public static Border SegBar(params RadioButton[] buttons)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var b in buttons) sp.Children.Add(b);
        var b2 = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left, Child = sp };
        return b2.Brush(Border.BackgroundProperty, "PanelBg");
    }

    public static Border Line(Thickness? margin = null)
    {
        var b = new Border { Height = 1, Margin = margin ?? new Thickness(0) };
        return b.Brush(Border.BackgroundProperty, "LineBrush");
    }

    /// <summary>The title bar of a dialog: glyph · title on the left, close button on the right.</summary>
    public static DockPanel TitleBar(OrbitWindow w, string glyph, string title, out TextBlock titleText, out TextBlock glyphText, Action close, double height = 44, double left = 20)
    {
        var dock = new DockPanel { Height = height, Margin = new Thickness(left, 0, 6, 0) };
        var x = Icon("", 11);
        x.Click += (_, _) => close();
        DockPanel.SetDock(x, Dock.Right);
        dock.Children.Add(x);
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        glyphText = Glyph(glyph ?? "", 15, "AccentBrush", new Thickness(0, 0, 10, 0));
        glyphText.IsVisible = glyph != null;
        sp.Children.Add(glyphText);
        titleText = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(titleText);
        dock.Children.Add(sp);
        w.DragArea(dock);
        return dock;
    }

    public static TextBox Field(string text = "", bool input = true)
    {
        var t = new TextBox { Text = text, Classes = { "field" } };
        if (input) t.Classes.Add("input");
        return t;
    }
}
