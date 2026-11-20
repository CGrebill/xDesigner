using XDesigner.Protocol.Rendering;
using XDesigner.RenderHost.DesignTime;

namespace XDesigner.RenderHost.Rendering;

/// <summary>
/// Reports every MAUI element that carries a <see cref="DesignId"/>, nested under its nearest designed ancestor, in DIPs
/// relative to the document root (§14.6). Positions add up each element's <c>Frame</c> (its box in its parent); MAUI's
/// Grid doesn't publish its row and column sizes, so they are worked out from the definitions and the children.
/// </summary>
internal static class MauiLayoutTree
{
    public static ElementLayout? Build(VisualElement root)
    {
        var roots = new List<ElementLayout>();
        Collect(root, 0, 0, true, roots, isRoot: true);
        return roots.Count switch
        {
            0 => null,
            1 => roots[0],
            _ => new ElementLayout(0, "", new RectDto(0, 0, root.Width, root.Height), true, ThicknessDto.Zero, ThicknessDto.Zero, null, roots.ToArray()),
        };
    }

    private static void Collect(IVisualTreeElement element, double parentX, double parentY, bool parentVisible, List<ElementLayout> into, bool isRoot = false)
    {
        var x = parentX;
        var y = parentY;
        var visible = parentVisible;
        if (element is VisualElement visual)
        {
            if (!isRoot)
            {
                x += visual.Frame.X;
                y += visual.Frame.Y;
            }

            visible &= visual.IsVisible;
        }

        var id = element is BindableObject bindable ? DesignId.GetValue(bindable) : 0;
        var target = id == 0 ? into : [];
        foreach (var child in element.GetVisualChildren())
        {
            Collect(child, x, y, visible, target);
        }

        if (id != 0 && element is VisualElement designed)
        {
            into.Add(new ElementLayout(
                id,
                designed.GetType().FullName ?? designed.GetType().Name,
                new RectDto(x, y, designed.Frame.Width, designed.Frame.Height),
                visible,
                designed is View view ? ToDto(view.Margin) : ThicknessDto.Zero,
                designed is IPadding padded ? ToDto(padded.Padding) : ThicknessDto.Zero,
                DescribePanel(designed),
                target.ToArray()));
        }
    }

    private static PanelInfo? DescribePanel(VisualElement element) => element switch
    {
        Grid grid => new PanelInfo(PanelKind.Grid, RowOffsets: GridOffsets(grid, rows: true), ColumnOffsets: GridOffsets(grid, rows: false)),
        AbsoluteLayout => new PanelInfo(PanelKind.Canvas),
        StackBase stack => new PanelInfo(PanelKind.StackPanel, stack is HorizontalStackLayout || stack is StackLayout { Orientation: StackOrientation.Horizontal }
            ? LayoutOrientation.Horizontal
            : LayoutOrientation.Vertical),
        FlexLayout flex => new PanelInfo(PanelKind.WrapPanel, flex.Direction is Microsoft.Maui.Layouts.FlexDirection.Row or Microsoft.Maui.Layouts.FlexDirection.RowReverse
            ? LayoutOrientation.Horizontal
            : LayoutOrientation.Vertical),
        Layout => new PanelInfo(PanelKind.Other),
        _ => null,
    };

    /// <summary>
    /// Start of each row (column) plus the end of the last, relative to the grid: fixed sizes as given, Auto ones as tall as
    /// their tallest single-row child (with margins), and the space left shared by the stars.
    /// </summary>
    internal static double[] GridOffsets(Grid grid, bool rows)
    {
        var lengths = rows ? grid.RowDefinitions.Select(r => r.Height).ToList() : grid.ColumnDefinitions.Select(c => c.Width).ToList();
        var padding = grid.Padding;
        var start = rows ? padding.Top : padding.Left;
        var end = (rows ? grid.Height - padding.Bottom : grid.Width - padding.Right);
        if (lengths.Count == 0)
        {
            return [start, Math.Max(start, end)];
        }

        var spacing = rows ? grid.RowSpacing : grid.ColumnSpacing;
        var sizes = new double[lengths.Count];
        for (var i = 0; i < lengths.Count; i++)
        {
            if (lengths[i].IsAbsolute)
            {
                sizes[i] = lengths[i].Value;
            }
            else if (lengths[i].IsAuto)
            {
                sizes[i] = grid.Children.OfType<View>()
                    .Where(c => (rows ? Grid.GetRow(c) : Grid.GetColumn(c)) == i && (rows ? Grid.GetRowSpan(c) : Grid.GetColumnSpan(c)) == 1)
                    .Select(c => rows ? c.Frame.Height + c.Margin.VerticalThickness : c.Frame.Width + c.Margin.HorizontalThickness)
                    .DefaultIfEmpty(0)
                    .Max();
            }
        }

        var stars = lengths.Where(l => l.IsStar).Sum(l => l.Value);
        var left = Math.Max(0, end - start - (spacing * (lengths.Count - 1)) - sizes.Sum());
        for (var i = 0; i < lengths.Count; i++)
        {
            if (lengths[i].IsStar && stars > 0)
            {
                sizes[i] = left * lengths[i].Value / stars;
            }
        }

        var offsets = new double[lengths.Count + 1];
        offsets[0] = start;
        for (var i = 0; i < lengths.Count; i++)
        {
            offsets[i + 1] = offsets[i] + sizes[i] + (i < lengths.Count - 1 ? spacing : 0);
        }

        return offsets;
    }

    private static ThicknessDto ToDto(Thickness t) => new(t.Left, t.Top, t.Right, t.Bottom);
}
