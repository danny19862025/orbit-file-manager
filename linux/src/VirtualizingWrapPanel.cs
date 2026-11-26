using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Orbit;

/// <summary>
/// A wrap panel for equal-sized tiles that only creates containers for the rows on screen (plus one either side),
/// so folders with tens of thousands of items scroll smoothly. Containers that scroll away are reused.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel
{
    public static readonly StyledProperty<double> ItemWidthProperty = AvaloniaProperty.Register<VirtualizingWrapPanel, double>(nameof(ItemWidth), 118);
    public static readonly StyledProperty<double> ItemHeightProperty = AvaloniaProperty.Register<VirtualizingWrapPanel, double>(nameof(ItemHeight), 126);

    public double ItemWidth { get => GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }

    public int Columns { get; private set; } = 1;
    public int VisibleRows { get; private set; } = 4;

    readonly Dictionary<int, Control> realized = new();
    readonly Stack<Control> pool = new();
    Rect viewport;
    double lastWidth;

    static VirtualizingWrapPanel()
    {
        AffectsMeasure<VirtualizingWrapPanel>(ItemWidthProperty, ItemHeightProperty);
    }

    public VirtualizingWrapPanel()
    {
        EffectiveViewportChanged += (_, e) =>
        {
            var old = viewport;
            viewport = e.EffectiveViewport;
            if (Math.Abs(old.Y - viewport.Y) > 1 || Math.Abs(old.Height - viewport.Height) > 1 || Math.Abs(old.Width - viewport.Width) > 1)
                InvalidateMeasure();
        };
    }

    (int first, int last) Range(int count)
    {
        if (count == 0) return (0, -1);
        double h = viewport.Height > 0 ? viewport.Height : 1000;
        double top = Math.Max(0, viewport.Y);
        int firstRow = Math.Max(0, (int)(top / ItemHeight) - 1);
        int lastRow = (int)Math.Ceiling((top + h) / ItemHeight) + 1;
        int first = Math.Min(count - 1, firstRow * Columns);
        int last = Math.Min(count - 1, (lastRow + 1) * Columns - 1);
        return (first, last);
    }

    protected override Size MeasureOverride(Size available)
    {
        var items = Items;
        int count = items.Count;
        double width = double.IsInfinity(available.Width) ? Math.Max(viewport.Width, ItemWidth) : available.Width;
        lastWidth = width;
        Columns = Math.Max(1, (int)(width / ItemWidth));
        VisibleRows = Math.Max(1, (int)((viewport.Height > 0 ? viewport.Height : 600) / ItemHeight));
        int rows = (count + Columns - 1) / Columns;

        var (first, last) = Range(count);
        foreach (var kv in realized.ToList())
            if (kv.Key < first || kv.Key > last || kv.Key >= count) { Recycle(kv.Value); realized.Remove(kv.Key); }
        var size = new Size(ItemWidth, ItemHeight);
        for (int i = first; i <= last; i++)
        {
            if (!realized.TryGetValue(i, out var c)) realized[i] = c = Realize(i, items[i]);
            c.Measure(size);
        }
        return new Size(width, rows * ItemHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (i, c) in realized)
            c.Arrange(RectFor(i));
        return finalSize;
    }

    Rect RectFor(int i) => new((i % Columns) * ItemWidth, (i / Columns) * ItemHeight, ItemWidth, ItemHeight);

    Control Realize(int index, object item)
    {
        var gen = ItemContainerGenerator!;
        Control c;
        if (gen.NeedsContainer(item, index, out var recycleKey))
        {
            if (pool.Count > 0) { c = pool.Pop(); c.IsVisible = true; }
            else
            {
                c = gen.CreateContainer(item, index, recycleKey);
                AddInternalChild(c);
            }
            gen.PrepareItemContainer(c, item, index);
            gen.ItemContainerPrepared(c, item, index);
        }
        else
        {
            c = (Control)item;
            if (!Children.Contains(c)) AddInternalChild(c);
        }
        return c;
    }

    void Recycle(Control c)
    {
        if (c.IsKeyboardFocusWithin) (this.FindAncestorOfType<ListBox>())?.Focus();
        ItemContainerGenerator?.ClearItemContainer(c);
        c.IsVisible = false;
        pool.Push(c);
    }

    protected override void OnItemsChanged(IReadOnlyList<object> items, NotifyCollectionChangedEventArgs e)
    {
        // indices shift with every change; simplest correct answer is to re-realize what's on screen
        foreach (var c in realized.Values) Recycle(c);
        realized.Clear();
        InvalidateMeasure();
    }

    protected override Control ScrollIntoView(int index)
    {
        var items = Items;
        if (index < 0 || index >= items.Count) return null;
        if (!realized.TryGetValue(index, out var c))
        {
            Columns = Math.Max(1, (int)(Math.Max(lastWidth, ItemWidth) / ItemWidth));
            realized[index] = c = Realize(index, items[index]);
            c.Measure(new Size(ItemWidth, ItemHeight));
            c.Arrange(RectFor(index));
        }
        this.BringIntoView(RectFor(index));
        return c;
    }

    protected override Control ContainerFromIndex(int index) => realized.TryGetValue(index, out var c) ? c : null;

    protected override int IndexFromContainer(Control container)
    {
        foreach (var (i, c) in realized) if (ReferenceEquals(c, container)) return i;
        return -1;
    }

    protected override IEnumerable<Control> GetRealizedContainers() => realized.Values;

    protected override IInputElement GetControl(NavigationDirection direction, IInputElement from, bool wrap)
    {
        int count = Items.Count;
        if (count == 0) return null;
        int cur = from is Control fc ? IndexFromContainer(fc) : -1;
        int next = direction switch
        {
            NavigationDirection.Left or NavigationDirection.Previous => cur - 1,
            NavigationDirection.Right or NavigationDirection.Next => cur + 1,
            NavigationDirection.Up => cur - Columns,
            NavigationDirection.Down => cur + Columns,
            NavigationDirection.First => 0,
            NavigationDirection.Last => count - 1,
            _ => cur
        };
        if (next < 0 || next >= count) return null;
        return ScrollIntoView(next);
    }
}
