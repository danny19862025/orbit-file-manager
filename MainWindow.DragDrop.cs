using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Orbit;

/// <summary>Drag &amp; drop polish: drop feedback, spring-loaded folders, tab/breadcrumb targets, tab reordering, marquee selection.</summary>
public partial class MainWindow
{
    const string TabFormat = "OrbitTab";

    readonly DispatcherTimer springTimer;
    FileItem springItem;
    TabState springTab;
    Point? tabDragStart;

    ListBox marqueeList;
    Point marqueeStart;
    HashSet<object> marqueeBase;
    bool marqueeMoved;

    // =====================================================================
    // Dragging files out of the list
    // =====================================================================
    void StartFileDrag(ListBox list, string[] paths)
    {
        var data = new DataObject(DataFormats.FileDrop, paths);
        try
        {
            DragDrop.DoDragDrop(list, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch (Exception ex)
        {
            AppSettings.LogError(ex);
            Toast("Drag failed: " + ex.Message);
        }
        finally
        {
            SetDropTarget(null);
            ShowDropFeedback(null, DragDropEffects.None, null);
            springTimer.Stop();
            if (list.IsMouseCaptured) list.ReleaseMouseCapture(); // stop the list continuing a drag-select afterwards
        }
    }

    // =====================================================================
    // Feedback while something is dragged over the content area
    // =====================================================================
    void ShowDropFeedback(string target, DragDropEffects eff, FileItem over)
    {
        if (target == null)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            DropHint.Visibility = Visibility.Collapsed;
            springItem = null;
            if (springTab == null) springTimer.Stop();
            return;
        }
        if (eff == DragDropEffects.None)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            DropHintText.Text = "Already in this folder — drop onto a folder, a tab or the path bar";
            DropHint.Visibility = Visibility.Visible;
            springItem = null;
            return;
        }
        var verb = eff == DragDropEffects.Move ? "Move" : "Copy";
        if (IsRemote(target)) verb = "Upload";
        var hold = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0
            ? (eff == DragDropEffects.Move ? "   ·   hold Ctrl to copy" : "   ·   hold Shift to move")
            : "";
        if (over != null)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            DropHintText.Text = $"{verb} into “{over.Name}”{hold}";
            if (!ReferenceEquals(springItem, over))
            {
                springItem = over;
                springTab = null;
                springTimer.Stop();
                springTimer.Start(); // hover a moment to open the folder
            }
        }
        else
        {
            DropOverlay.Visibility = Visibility.Visible;
            DropHintText.Text = $"{verb} to “{Display(target)}”{hold}";
            springItem = null;
            if (springTab == null) springTimer.Stop();
        }
        DropHint.Visibility = Visibility.Visible;
    }

    void AutoScrollDuringDrag(DragEventArgs e)
    {
        var sv = FindChild<ScrollViewer>(ActiveList);
        if (sv == null) return;
        double y = e.GetPosition(sv).Y;
        if (y < 36) sv.ScrollToVerticalOffset(sv.VerticalOffset - 18);
        else if (y > sv.ActualHeight - 36) sv.ScrollToVerticalOffset(sv.VerticalOffset + 18);
    }

    void SpringTimer_Tick(object sender, EventArgs e)
    {
        springTimer.Stop();
        if (springTab != null)
        {
            var t = springTab;
            springTab = null;
            if (!ReferenceEquals(t, current)) SwitchTo(t);
        }
        else if (springItem != null && springItem.IsDir)
        {
            var target = springItem.FullPath;
            springItem = null;
            SetDropTarget(null);
            Navigate(target);
        }
    }

    // =====================================================================
    // Tabs: drop files into a tab's folder, hover to switch, drag to reorder
    // =====================================================================
    void Tab_DragEnter(object sender, DragEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not TabState t || e.Data.GetDataPresent(TabFormat)) return;
        if (!ReferenceEquals(t, current))
        {
            springTab = t;
            springItem = null;
            springTimer.Stop();
            springTimer.Start();
        }
    }

    void Tab_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (((FrameworkElement)sender).Tag is not TabState t) return;
        if (e.Data.GetDataPresent(TabFormat))
        {
            if (e.Data.GetData(TabFormat) is TabState moving && !ReferenceEquals(moving, t))
            {
                int from = Tabs.IndexOf(moving), to = Tabs.IndexOf(t);
                if (from >= 0 && to >= 0) Tabs.Move(from, to);
            }
            e.Effects = DragDropEffects.Move;
            return;
        }
        if (IsTag(t.Path) || !TryGetDropFiles(e.Data, out var files)) return;
        e.Effects = ChooseEffect(e, files, t.Path);
    }

    void Tab_DragLeave(object sender, DragEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is TabState t && ReferenceEquals(t, springTab))
        {
            springTab = null;
            springTimer.Stop();
        }
    }

    void Tab_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        springTimer.Stop();
        springTab = null;
        if (e.Data.GetDataPresent(TabFormat)) { SaveSoon(); return; }
        if (((FrameworkElement)sender).Tag is not TabState t || IsTag(t.Path) || !TryGetDropFiles(e.Data, out var files)) return;
        var eff = ChooseEffect(e, files, t.Path);
        e.Effects = eff;
        DoTransfer(files, t.Path, eff);
    }

    void Tab_MouseMove(object sender, MouseEventArgs e)
    {
        if (tabDragStart == null || e.LeftButton != MouseButtonState.Pressed) { tabDragStart = null; return; }
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - tabDragStart.Value.X) < SystemParameters.MinimumHorizontalDragDistance * 2) return;
        tabDragStart = null;
        if (((FrameworkElement)sender).Tag is not TabState t || Tabs.Count < 2) return;
        var data = new DataObject();
        data.SetData(TabFormat, t);
        try { DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move); }
        catch { }
        SaveSoon();
    }

    // =====================================================================
    // Breadcrumbs: drop onto any parent folder in the path bar
    // =====================================================================
    void Crumb_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (((FrameworkElement)sender).Tag is Crumb c && c.Path != null && !IsTag(c.Path) && TryGetDropFiles(e.Data, out var files))
            e.Effects = ChooseEffect(e, files, c.Path);
    }

    void Crumb_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (((FrameworkElement)sender).Tag is Crumb c && c.Path != null && !IsTag(c.Path) && TryGetDropFiles(e.Data, out var files))
        {
            var eff = ChooseEffect(e, files, c.Path);
            e.Effects = eff;
            DoTransfer(files, c.Path, eff);
        }
    }

    // =====================================================================
    // Marquee (box) selection — drag on empty space
    // =====================================================================
    void BeginMarquee(ListBox list, MouseButtonEventArgs e)
    {
        var sv = FindChild<ScrollViewer>(list);
        var p = e.GetPosition(list);
        marqueeList = list;
        marqueeStart = new Point(p.X + (sv?.HorizontalOffset ?? 0), p.Y + (sv?.VerticalOffset ?? 0));
        marqueeBase = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0
            ? new HashSet<object>(list.SelectedItems.Cast<object>())
            : new HashSet<object>();
        marqueeMoved = false;
        list.CaptureMouse();
        e.Handled = true;
    }

    void UpdateMarquee(MouseEventArgs e)
    {
        var list = marqueeList;
        if (e.LeftButton != MouseButtonState.Pressed) { EndMarquee(); return; }
        var sv = FindChild<ScrollViewer>(list);
        var p = e.GetPosition(list);

        if (sv != null)
        {
            if (p.Y < 0) sv.ScrollToVerticalOffset(sv.VerticalOffset + p.Y / 2 - 4);
            else if (p.Y > list.ActualHeight) sv.ScrollToVerticalOffset(sv.VerticalOffset + (p.Y - list.ActualHeight) / 2 + 4);
        }
        double ox = sv?.HorizontalOffset ?? 0, oy = sv?.VerticalOffset ?? 0;
        var r = new Rect(marqueeStart, new Point(p.X + ox, p.Y + oy));
        if (!marqueeMoved && r.Width < 4 && r.Height < 4) return;
        marqueeMoved = true;

        // draw: content coords → list coords → overlay canvas coords (clipped to the visible area)
        var view = new Rect(r.X - ox, r.Y - oy, r.Width, r.Height);
        view.Intersect(new Rect(0, 0, list.ActualWidth, list.ActualHeight));
        if (view.IsEmpty) { Marquee.Visibility = Visibility.Collapsed; }
        else
        {
            var tl = list.TranslatePoint(view.TopLeft, MarqueeCanvas);
            Canvas.SetLeft(Marquee, tl.X);
            Canvas.SetTop(Marquee, tl.Y);
            Marquee.Width = view.Width;
            Marquee.Height = view.Height;
            Marquee.Visibility = Visibility.Visible;
        }

        // select realized containers that intersect (off-screen ones keep their state)
        var host = FindChild<VirtualizingPanel>(list);
        if (host == null) return;
        var gen = list.ItemContainerGenerator;
        int n = VisualTreeHelper.GetChildrenCount(host);
        for (int i = 0; i < n; i++)
        {
            if (VisualTreeHelper.GetChild(host, i) is not ListBoxItem c || !c.IsVisible) continue;
            Rect b;
            try { b = c.TransformToAncestor(list).TransformBounds(new Rect(c.RenderSize)); }
            catch { continue; }
            b.Offset(ox, oy);
            var item = gen.ItemFromContainer(c);
            bool want = b.IntersectsWith(r) || marqueeBase.Contains(item);
            if (c.IsSelected != want) c.IsSelected = want;
        }
    }

    void EndMarquee()
    {
        var list = marqueeList;
        marqueeList = null;
        Marquee.Visibility = Visibility.Collapsed;
        if (list != null && list.IsMouseCaptured) list.ReleaseMouseCapture();
        UpdateSelText();
    }
}
