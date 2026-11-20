using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using XDesigner.Protocol.Rendering;
using XDesigner.RenderHost.DesignTime;

namespace XDesigner.RenderHost.Rendering;

/// <summary>
/// Walks the visual tree and reports every element that carries a <see cref="DesignId"/>, nested under its nearest
/// designed ancestor, in DIPs relative to the document root. Template parts have no id and are skipped, but their designed
/// descendants (a Button's content) are still found.
/// </summary>
internal static class LayoutTreeBuilder
{
    /// <summary>Builds the tree for <paramref name="root"/>; null if nothing carries an id.</summary>
    public static ElementLayout? Build(FrameworkElement root)
    {
        var roots = new List<ElementLayout>();
        Collect(root, root, true, roots);
        return roots.Count switch
        {
            0 => null,
            1 => roots[0],
            _ => new ElementLayout(0, "", new RectDto(0, 0, root.ActualWidth, root.ActualHeight), true,
                ThicknessDto.Zero, ThicknessDto.Zero, null, roots.ToArray()),
        };
    }

    /// <summary>The element and everything below it in the visual tree.</summary>
    public static IEnumerable<DependencyObject> SelfAndDescendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            for (var i = VisualTreeHelper.GetChildrenCount(current) - 1; i >= 0; i--)
            {
                stack.Push(VisualTreeHelper.GetChild(current, i));
            }
        }
    }

    private static void Collect(DependencyObject element, FrameworkElement root, bool parentVisible, List<ElementLayout> into)
    {
        var visible = parentVisible && (element is not UIElement ui || ui.Visibility == Visibility.Visible);
        var id = DesignId.GetValue(element);
        var target = id == 0 ? into : [];
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            Collect(VisualTreeHelper.GetChild(element, i), root, visible, target);
        }

        if (id != 0 && element is UIElement designed)
        {
            into.Add(Describe(designed, id, root, visible, target.ToArray()));
        }
    }

    private static ElementLayout Describe(UIElement element, int id, FrameworkElement root, bool visible, ElementLayout[] children)
    {
        var size = element is FrameworkElement fe ? new Windows.Foundation.Size(fe.ActualWidth, fe.ActualHeight) : element.RenderSize;
        RectDto bounds;
        try
        {
            var r = element.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, size.Width, size.Height));
            bounds = new RectDto(r.X, r.Y, r.Width, r.Height);
        }
        catch (ArgumentException)
        {
            bounds = new RectDto(0, 0, 0, 0); // not connected to the root (an open popup's content)
        }

        return new ElementLayout(
            id,
            element.GetType().FullName ?? element.GetType().Name,
            bounds,
            visible,
            element is FrameworkElement f ? ToDto(f.Margin) : ThicknessDto.Zero,
            ToDto(PaddingOf(element)),
            DescribePanel(element),
            children);
    }

    internal static Thickness PaddingOf(UIElement element) => element switch
    {
        Control c => c.Padding,
        Border b => b.Padding,
        StackPanel s => s.Padding,
        Grid g => g.Padding,
        RelativePanel r => r.Padding,
        ContentPresenter p => p.Padding,
        TextBlock t => t.Padding,
        _ => default,
    };

    private static PanelInfo? DescribePanel(UIElement element) => element switch
    {
        Grid grid => new PanelInfo(
            PanelKind.Grid,
            RowOffsets: Offsets(grid.RowDefinitions.Select(r => r.ActualHeight), grid.RowSpacing, grid.Padding.Top, grid.ActualHeight - grid.Padding.Bottom),
            ColumnOffsets: Offsets(grid.ColumnDefinitions.Select(c => c.ActualWidth), grid.ColumnSpacing, grid.Padding.Left, grid.ActualWidth - grid.Padding.Right)),
        Canvas => new PanelInfo(PanelKind.Canvas),
        StackPanel stack => new PanelInfo(PanelKind.StackPanel, ToDto(stack.Orientation)),
        VariableSizedWrapGrid wrap => new PanelInfo(PanelKind.WrapPanel, ToDto(wrap.Orientation)),
        Panel => new PanelInfo(PanelKind.Other),
        _ => null,
    };

    /// <summary>
    /// Start of each row/column plus the end of the last, relative to the grid. Rows start inside the padding, and with
    /// spacing each row starts after the gap. A grid without definitions has one implicit row/column filling it.
    /// </summary>
    internal static double[] Offsets(IEnumerable<double> sizes, double spacing, double start, double end)
    {
        var list = sizes.ToList();
        if (list.Count == 0)
        {
            return [start, Math.Max(start, end)];
        }

        var offsets = new double[list.Count + 1];
        offsets[0] = start;
        for (var i = 0; i < list.Count; i++)
        {
            offsets[i + 1] = offsets[i] + list[i] + (i < list.Count - 1 ? spacing : 0);
        }

        return offsets;
    }

    private static ThicknessDto ToDto(Thickness t) => new(t.Left, t.Top, t.Right, t.Bottom);

    private static LayoutOrientation ToDto(Orientation o) =>
        o == Orientation.Horizontal ? LayoutOrientation.Horizontal : LayoutOrientation.Vertical;
}
