using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using XDesigner.Protocol.Rendering;
using XDesigner.RenderHost.DesignTime;

namespace XDesigner.RenderHost.Rendering;

/// <summary>
/// Walks the visual tree and reports every visual that carries a <see cref="DesignId"/>,
/// nested under its nearest designed ancestor. Template parts have no id and are skipped,
/// but their designed descendants (e.g. a Button's content) are still found.
/// </summary>
internal static class LayoutTreeBuilder
{
    /// <summary>Builds the tree for everything rendered in <paramref name="frame"/>; null if nothing carries an id.</summary>
    public static ElementLayout? Build(Visual frame)
    {
        var roots = new List<ElementLayout>();
        Collect(frame, frame, roots);
        return roots.Count switch
        {
            0 => null,
            1 => roots[0],
            // Shouldn't happen for a single document; wrap so nothing is lost.
            _ => new ElementLayout(0, "", new RectDto(0, 0, frame.Bounds.Width, frame.Bounds.Height), true,
                ThicknessDto.Zero, ThicknessDto.Zero, null, roots.ToArray()),
        };
    }

    private static void Collect(Visual visual, Visual frame, List<ElementLayout> into)
    {
        var id = visual.GetValue(DesignId.ValueProperty);
        if (id == 0)
        {
            foreach (var child in visual.GetVisualChildren())
            {
                Collect(child, frame, into);
            }

            return;
        }

        var children = new List<ElementLayout>();
        foreach (var child in visual.GetVisualChildren())
        {
            Collect(child, frame, children);
        }

        into.Add(Describe(visual, id, frame, children.ToArray()));
    }

    private static ElementLayout Describe(Visual visual, int id, Visual frame, ElementLayout[] children)
    {
        var origin = visual.TranslatePoint(default, frame) ?? default;
        var bounds = new RectDto(origin.X, origin.Y, visual.Bounds.Width, visual.Bounds.Height);
        var margin = visual is Layoutable layoutable ? ToDto(layoutable.Margin) : ThicknessDto.Zero;

        return new ElementLayout(
            id,
            visual.GetType().FullName ?? visual.GetType().Name,
            bounds,
            visual.IsEffectivelyVisible,
            margin,
            ReadPadding(visual),
            DescribePanel(visual),
            children);
    }

    private static ThicknessDto ReadPadding(Visual visual) => visual switch
    {
        Decorator d => ToDto(d.Padding),
        TemplatedControl t => ToDto(t.Padding),
        _ => AvaloniaPropertyRegistry.Instance.FindRegistered(visual, "Padding") is { } p && visual.GetValue(p) is Thickness th
            ? ToDto(th)
            : ThicknessDto.Zero,
    };

    private static PanelInfo? DescribePanel(Visual visual) => visual switch
    {
        Grid grid => new PanelInfo(
            PanelKind.Grid,
            RowOffsets: Offsets(grid.RowDefinitions.Select(r => r.ActualHeight), grid.Bounds.Height),
            ColumnOffsets: Offsets(grid.ColumnDefinitions.Select(c => c.ActualWidth), grid.Bounds.Width)),
        Canvas => new PanelInfo(PanelKind.Canvas),
        StackPanel stack => new PanelInfo(PanelKind.StackPanel, ToDto(stack.Orientation)),
        WrapPanel wrap => new PanelInfo(PanelKind.WrapPanel, ToDto(wrap.Orientation)),
        DockPanel => new PanelInfo(PanelKind.DockPanel),
        UniformGrid uniform => new PanelInfo(PanelKind.UniformGrid, Rows: uniform.Rows, Columns: uniform.Columns),
        Panel => new PanelInfo(PanelKind.Other),
        _ => null,
    };

    /// <summary>
    /// Start of each row/column plus the end of the last. Avalonia's ActualHeight/ActualWidth include the RowSpacing/
    /// ColumnSpacing after each one, so the sums are where rows start; the last edge is clamped to the grid, which the
    /// last row's spacing would pass (review M20). A grid without definitions has one implicit row/column spanning it.
    /// </summary>
    internal static double[] Offsets(IEnumerable<double> sizes, double total)
    {
        var list = sizes.ToList();
        if (list.Count == 0)
        {
            return [0, total];
        }

        var offsets = new double[list.Count + 1];
        for (var i = 0; i < list.Count; i++)
        {
            offsets[i + 1] = offsets[i] + list[i];
        }

        offsets[^1] = Math.Min(offsets[^1], total);
        return offsets;
    }

    private static ThicknessDto ToDto(Thickness t) => new(t.Left, t.Top, t.Right, t.Bottom);

    private static LayoutOrientation ToDto(Orientation o) =>
        o == Orientation.Horizontal ? LayoutOrientation.Horizontal : LayoutOrientation.Vertical;
}
