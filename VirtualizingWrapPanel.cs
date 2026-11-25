using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Orbit;

/// <summary>
/// Grid-of-tiles panel that only creates containers for rows on screen, so folders with
/// tens of thousands of files stay smooth.
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(118.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(126.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    const double Pad = 8;
    Size extent, viewport;
    Point offset;

    public int Columns { get; private set; } = 1;
    public int VisibleRows => Math.Max(1, (int)(viewport.Height / ItemHeight));

    int ItemCount => ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;

    protected override Size MeasureOverride(Size available)
    {
        var size = new Size(
            double.IsInfinity(available.Width) ? ItemWidth * 6 + Pad * 2 : available.Width,
            double.IsInfinity(available.Height) ? ItemHeight * 4 : available.Height);

        int count = ItemCount;
        Columns = Math.Max(1, (int)((size.Width - Pad * 2) / ItemWidth));
        int rows = (count + Columns - 1) / Columns;
        UpdateScroll(size, new Size(size.Width, rows * ItemHeight + Pad * 2));

        var children = InternalChildren; // touching this ensures the generator exists
        var gen = ItemContainerGenerator;

        if (count == 0)
        {
            CleanUp(0, -1);
            return size;
        }

        int firstRow = Math.Max(0, (int)Math.Floor((offset.Y - Pad) / ItemHeight));
        int lastRow = (int)Math.Ceiling((offset.Y + size.Height - Pad) / ItemHeight);
        int first = Math.Min(count - 1, firstRow * Columns);
        int last = Math.Min(count - 1, (lastRow + 1) * Columns - 1);

        var startPos = gen.GeneratorPositionFromIndex(first);
        int childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;
        using (gen.StartAt(startPos, GeneratorDirection.Forward, true))
        {
            for (int i = first; i <= last; i++, childIndex++)
            {
                var child = (UIElement)gen.GenerateNext(out bool isNew);
                if (child == null) break;
                if (isNew)
                {
                    if (childIndex >= children.Count) AddInternalChild(child);
                    else InsertInternalChild(childIndex, child);
                    gen.PrepareItemContainer(child);
                }
                child.Measure(new Size(ItemWidth, ItemHeight));
            }
        }

        CleanUp(first, last);
        return size;
    }

    void CleanUp(int first, int last)
    {
        var children = InternalChildren;
        var gen = ItemContainerGenerator;
        for (int i = children.Count - 1; i >= 0; i--)
        {
            var pos = new GeneratorPosition(i, 0);
            int idx = gen.IndexFromGeneratorPosition(pos);
            if (idx < first || idx > last)
            {
                gen.Remove(pos, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var gen = ItemContainerGenerator;
        var children = InternalChildren;
        for (int i = 0; i < children.Count; i++)
        {
            int idx = gen.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (idx < 0) continue;
            int r = idx / Columns, c = idx % Columns;
            children[i].Arrange(new Rect(Pad + c * ItemWidth - offset.X, Pad + r * ItemHeight - offset.Y, ItemWidth, ItemHeight));
        }
        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                if (args.ItemUICount > 0) RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
        }
    }

    void UpdateScroll(Size vp, Size ext)
    {
        bool changed = vp != viewport || ext != extent;
        viewport = vp;
        extent = ext;
        double maxY = Math.Max(0, extent.Height - viewport.Height);
        if (offset.Y > maxY) { offset.Y = maxY; changed = true; }
        offset.X = 0;
        if (changed) ScrollOwner?.InvalidateScrollInfo();
    }

    void ScrollToIndex(int idx)
    {
        if (idx < 0) return;
        double top = Pad + (idx / Columns) * ItemHeight;
        if (top - Pad < offset.Y) SetVerticalOffset(top - Pad);
        else if (top + ItemHeight > offset.Y + viewport.Height) SetVerticalOffset(top + ItemHeight - viewport.Height + Pad);
    }

    protected override void BringIndexIntoView(int index) => ScrollToIndex(index);

    // ---------- IScrollInfo ----------
    public ScrollViewer ScrollOwner { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => extent.Width;
    public double ExtentHeight => extent.Height;
    public double ViewportWidth => viewport.Width;
    public double ViewportHeight => viewport.Height;
    public double HorizontalOffset => offset.X;
    public double VerticalOffset => offset.Y;

    public void SetVerticalOffset(double y)
    {
        y = Math.Max(0, Math.Min(y, Math.Max(0, extent.Height - viewport.Height)));
        if (Math.Abs(y - offset.Y) < 0.1) return;
        offset.Y = y;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public void SetHorizontalOffset(double x) { }
    public void LineUp() => SetVerticalOffset(offset.Y - 40);
    public void LineDown() => SetVerticalOffset(offset.Y + 40);
    public void PageUp() => SetVerticalOffset(offset.Y - viewport.Height);
    public void PageDown() => SetVerticalOffset(offset.Y + viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(offset.Y - SystemParameters.WheelScrollLines * 24);
    public void MouseWheelDown() => SetVerticalOffset(offset.Y + SystemParameters.WheelScrollLines * 24);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual == null || rectangle.IsEmpty || ReferenceEquals(visual, this) || !IsAncestorOf(visual)) return Rect.Empty;
        DependencyObject d = visual;
        while (d != null && !ReferenceEquals(VisualTreeHelper.GetParent(d), this)) d = VisualTreeHelper.GetParent(d);
        if (d is not UIElement child) return Rect.Empty;
        int ci = InternalChildren.IndexOf(child);
        if (ci < 0) return Rect.Empty;
        int idx = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(ci, 0));
        ScrollToIndex(idx);
        return rectangle;
    }
}
