using System.Globalization;
using System.Xml.Linq;
using XDesigner.Core.Metadata;
using XDesigner.Core.Rendering;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;
using XDesigner.Protocol.Rendering;

namespace XDesigner.Core.Layout;

/// <summary>How the surface should show where a drop will land.</summary>
public enum DropIndicatorKind
{
    /// <summary>At an exact point (Canvas, Grid cell, margin-positioned panels).</summary>
    Point,

    /// <summary>Between children (StackPanel, WrapPanel, UniformGrid): <see cref="DropIndicator.Line"/>.</summary>
    InsertionLine,

    /// <summary>Against an edge of a DockPanel (or its fill area): <see cref="DropIndicator.Area"/>.</summary>
    DockArea,

    /// <summary>Becomes the container's content or an item: the whole container is highlighted.</summary>
    Container,
}

/// <summary>Geometry for the drop feedback, in frame DIPs.</summary>
/// <param name="Container">Bounds of the container that will receive the element.</param>
/// <param name="Area">Grid cell or dock band, when relevant.</param>
/// <param name="Line">Thin rectangle marking an insertion point between children.</param>
public sealed record DropIndicator(DropIndicatorKind Kind, RectDto Container, RectDto? Area = null, RectDto? Line = null);

/// <summary>Where and how a new (or moved) element goes (§7.1).</summary>
/// <param name="ContainerId">Node id of the receiving element.</param>
/// <param name="Index">Position among the container's object children.</param>
/// <param name="Attributes">Layout attributes to set on the element (attached properties, alignment, margin).</param>
/// <param name="IsPositioned">True when the element is placed at a point (Canvas/Grid/margin), so default sizes apply.</param>
public sealed record DropPlan(
    int ContainerId,
    int Index,
    IReadOnlyList<KeyValuePair<XName, string>> Attributes,
    bool IsPositioned,
    DropIndicator Indicator);

/// <summary>
/// Computes drop targets and panel-specific placement (§7.1). The target is the deepest element under the
/// pointer that can take another child; elements that can't (a Button that already has content, a user's
/// own UserControl, a TextBox) pass the drop up to their parent, the way the WinForms designer does.
/// </summary>
public static class DropPlanner
{
    /// <summary>Within this fraction of a DockPanel's size from an edge, the element docks to that edge.</summary>
    public const double DockBand = 0.25;

    /// <summary>Plans a drop at (<paramref name="x"/>, <paramref name="y"/>) in frame DIPs, or returns null with a reason.</summary>
    /// <param name="exclude">Elements that can't receive the drop (when moving: the moved elements and their descendants).</param>
    public static DropPlan? Plan(
        XamlDocument document, ControlCatalog catalog, LayoutIndex layout, double x, double y,
        out string? reason, IReadOnlySet<int>? exclude = null, double? positionX = null, double? positionY = null)
    {
        var hit = layout.HitTest(x, y, e => document.FindById(e.DesignId) is not null);
        if (hit is null)
        {
            reason = "Drop onto the form.";
            return null;
        }

        for (var node = document.FindById(hit.DesignId); node is not null; node = node.Parent)
        {
            if (exclude?.Contains(node.Id) == true || layout.Find(node.Id) is not { } bounds)
            {
                continue;
            }

            if (catalog.Resolve(node) is { } type && Accepts(node, type, exclude))
            {
                reason = null;
                return PlanFor(node, type, bounds, layout, x, y, exclude, positionX ?? x, positionY ?? y);
            }
        }

        reason = $"Nothing here can hold another control. Add a panel ({catalog.Profile.PanelExamples}) first.";
        return null;
    }

    /// <summary>
    /// Plans a drop into a specific container (toolbox double-click, paste), at a point inside it.
    /// Returns null if the container can't take another child.
    /// </summary>
    public static DropPlan? PlanInto(
        XamlDocument document, ControlCatalog catalog, LayoutIndex layout, int containerId, double x, double y, IReadOnlySet<int>? exclude = null)
    {
        return document.FindById(containerId) is { } node && layout.Find(containerId) is { } bounds
               && catalog.Resolve(node) is { } type && Accepts(node, type, exclude)
            ? PlanFor(node, type, bounds, layout, x, y, exclude, x, y)
            : null;
    }

    /// <summary>Whether the element can take one more child right now (ignoring <paramref name="exclude"/>d children being moved).</summary>
    public static bool Accepts(ElementNode node, ControlTypeInfo type, IReadOnlySet<int>? exclude = null) => type.Content switch
    {
        ContentModel.Children or ContentModel.Items => true,
        ContentModel.SingleContent => IsEmpty(node, type.ContentPropertyName ?? "Content", exclude),
        ContentModel.SingleChild => IsEmpty(node, type.ContentPropertyName ?? "Child", exclude),
        _ => false,
    };

    private static bool IsEmpty(ElementNode node, string contentProperty, IReadOnlySet<int>? exclude)
    {
        bool HasContent(XElement container) =>
            container.Elements().Any(e => RenderCopyBuilder.IsObjectElement(e) && exclude?.Contains(node.Document.NodeFor(e).Id) != true)
            || !string.IsNullOrWhiteSpace(string.Concat(container.Nodes().OfType<XText>().Select(t => t.Value)));

        // An empty <Border.Child></Border.Child> (left by deleting its control) doesn't make the element full.
        return node.Element.Attribute(contentProperty) is null
            && !HasContent(node.Element)
            && !node.Element.Elements().Any(e => e.Name.LocalName.EndsWith("." + contentProperty, StringComparison.Ordinal) && HasContent(e));
    }

    /// <param name="x">Pointer position: picks insertion points (stack, wrap, dock).</param>
    /// <param name="px">Where the element's top-left will be: sets positions (Canvas, Grid cell + margin, other panels).</param>
    private static DropPlan PlanFor(ElementNode node, ControlTypeInfo type, ElementLayout container, LayoutIndex layout, double x, double y, IReadOnlySet<int>? exclude, double px, double py)
    {
        var children = node.Children.Where(c => exclude?.Contains(c.Id) != true).ToList();
        var count = node.Children.Count;
        var box = container.Bounds;

        if (type.Content is ContentModel.SingleContent or ContentModel.SingleChild or ContentModel.Items)
        {
            return new DropPlan(node.Id, count, [], false, new DropIndicator(DropIndicatorKind.Container, box));
        }

        switch (container.Panel?.Kind)
        {
            case PanelKind.Canvas:
                return new DropPlan(node.Id, count,
                    [Attr("Canvas.Left", px - box.X), Attr("Canvas.Top", py - box.Y)],
                    true, new DropIndicator(DropIndicatorKind.Point, box));

            case PanelKind.Grid:
                return PlanGrid(node.Id, count, container, px, py);

            case PanelKind.StackPanel:
                return PlanStack(node, children, layout, container, x, y);

            case PanelKind.WrapPanel or PanelKind.UniformGrid:
                return PlanFlow(node, children, layout, container, x, y);

            case PanelKind.DockPanel:
                return PlanDock(node, count, box, x, y);

            default:
                // Any other panel: position with alignment + margin inside its content box.
                var contentX = box.X + container.Padding.Left;
                var contentY = box.Y + container.Padding.Top;
                return new DropPlan(node.Id, count, PositionAttributes(px - contentX, py - contentY), true,
                    new DropIndicator(DropIndicatorKind.Point, box));
        }
    }

    private static DropPlan PlanGrid(int id, int count, ElementLayout grid, double x, double y)
    {
        var box = grid.Bounds;
        var cols = grid.Panel?.ColumnOffsets ?? [0, box.Width];
        var rows = grid.Panel?.RowOffsets ?? [0, box.Height];
        var col = CellIndex(cols, x - box.X);
        var row = CellIndex(rows, y - box.Y);
        var cellX = box.X + cols[col];
        var cellY = box.Y + rows[row];

        var attributes = new List<KeyValuePair<XName, string>>();
        if (row > 0)
        {
            attributes.Add(new("Grid.Row", row.ToString(CultureInfo.InvariantCulture)));
        }

        if (col > 0)
        {
            attributes.Add(new("Grid.Column", col.ToString(CultureInfo.InvariantCulture)));
        }

        attributes.AddRange(PositionAttributes(x - cellX, y - cellY));
        var cell = new RectDto(cellX, cellY, cols[col + 1] - cols[col], rows[row + 1] - rows[row]);
        return new DropPlan(id, count, attributes, true, new DropIndicator(DropIndicatorKind.Point, box, Area: cell));
    }

    /// <summary>Index of the band (row/column) containing <paramref name="offset"/>; clamps to the first/last.</summary>
    private static int CellIndex(double[] edges, double offset)
    {
        for (var i = 0; i < edges.Length - 1; i++)
        {
            if (offset < edges[i + 1])
            {
                return i;
            }
        }

        return Math.Max(0, edges.Length - 2);
    }

    private static DropPlan PlanStack(ElementNode node, List<ElementNode> children, LayoutIndex layout, ElementLayout container, double x, double y)
    {
        var box = container.Bounds;
        var horizontal = container.Panel?.Orientation == LayoutOrientation.Horizontal;
        var laidOut = children.Select(c => (Node: c, Layout: layout.Find(c.Id))).Where(c => c.Layout is not null).ToList();

        var index = 0;
        foreach (var (_, child) in laidOut)
        {
            var mid = horizontal ? child!.Bounds.X + child.Bounds.Width / 2 : child!.Bounds.Y + child.Bounds.Height / 2;
            if ((horizontal ? x : y) > mid)
            {
                index++;
            }
        }

        // The line sits at the leading edge of the child it goes before, or after the last child.
        double at;
        if (index < laidOut.Count)
        {
            var b = laidOut[index].Layout!.Bounds;
            at = horizontal ? b.X : b.Y;
        }
        else if (laidOut.Count > 0)
        {
            var b = laidOut[^1].Layout!.Bounds;
            at = horizontal ? b.Right : b.Bottom;
        }
        else
        {
            at = horizontal ? box.X + container.Padding.Left : box.Y + container.Padding.Top;
        }

        var line = horizontal ? new RectDto(at - 1, box.Y, 2, box.Height) : new RectDto(box.X, at - 1, box.Width, 2);
        var xamlIndex = index < laidOut.Count ? node.Children.ToList().FindIndex(c => c.Id == laidOut[index].Node.Id) : node.Children.Count;
        return new DropPlan(node.Id, xamlIndex, [], false, new DropIndicator(DropIndicatorKind.InsertionLine, box, Line: line));
    }

    /// <summary>WrapPanel/UniformGrid: insert before the first child that comes after the pointer in reading order.</summary>
    private static DropPlan PlanFlow(ElementNode node, List<ElementNode> children, LayoutIndex layout, ElementLayout container, double x, double y)
    {
        var box = container.Bounds;
        var laidOut = children.Select(c => (Node: c, Layout: layout.Find(c.Id))).Where(c => c.Layout is not null).ToList();
        var index = laidOut.FindIndex(c => y < c.Layout!.Bounds.Y || (y < c.Layout.Bounds.Bottom && x < c.Layout.Bounds.X + c.Layout.Bounds.Width / 2));
        if (index < 0)
        {
            index = laidOut.Count;
        }

        RectDto line;
        if (index < laidOut.Count)
        {
            var b = laidOut[index].Layout!.Bounds;
            line = new RectDto(b.X - 1, b.Y, 2, b.Height);
        }
        else if (laidOut.Count > 0)
        {
            var b = laidOut[^1].Layout!.Bounds;
            line = new RectDto(b.Right - 1, b.Y, 2, b.Height);
        }
        else
        {
            line = new RectDto(box.X + container.Padding.Left, box.Y, 2, Math.Min(box.Height, 32));
        }

        var xamlIndex = index < laidOut.Count ? node.Children.ToList().FindIndex(c => c.Id == laidOut[index].Node.Id) : node.Children.Count;
        return new DropPlan(node.Id, xamlIndex, [], false, new DropIndicator(DropIndicatorKind.InsertionLine, box, Line: line));
    }

    private static DropPlan PlanDock(ElementNode node, int count, RectDto box, double x, double y)
    {
        var fx = box.Width > 0 ? (x - box.X) / box.Width : 0.5;
        var fy = box.Height > 0 ? (y - box.Y) / box.Height : 0.5;
        (string Edge, double Distance)[] edges = [("Left", fx), ("Right", 1 - fx), ("Top", fy), ("Bottom", 1 - fy)];
        var nearest = edges.MinBy(e => e.Distance);
        var lastChildFills = !string.Equals((string?)node.Element.Attribute("LastChildFill"), "False", StringComparison.OrdinalIgnoreCase);

        if (nearest.Distance >= DockBand)
        {
            // Centre: becomes the last child, which fills the remaining space.
            return new DropPlan(node.Id, count, [], false, new DropIndicator(DropIndicatorKind.DockArea, box, Area: Shrink(box, DockBand)));
        }

        // Docked children go before the filling last child.
        var index = lastChildFills && count > 0 ? count - 1 : count;
        var band = nearest.Edge switch
        {
            "Left" => box with { Width = box.Width * DockBand },
            "Right" => box with { X = box.Right - box.Width * DockBand, Width = box.Width * DockBand },
            "Top" => box with { Height = box.Height * DockBand },
            _ => box with { Y = box.Bottom - box.Height * DockBand, Height = box.Height * DockBand },
        };
        return new DropPlan(node.Id, index, [new("DockPanel.Dock", nearest.Edge)], false,
            new DropIndicator(DropIndicatorKind.DockArea, box, Area: band));
    }

    private static RectDto Shrink(RectDto box, double fraction) =>
        new(box.X + box.Width * fraction, box.Y + box.Height * fraction, box.Width * (1 - 2 * fraction), box.Height * (1 - 2 * fraction));

    /// <summary>WPF-designer style positioning inside a cell or panel: top-left alignment plus a margin.</summary>
    private static List<KeyValuePair<XName, string>> PositionAttributes(double left, double top) =>
    [
        new("HorizontalAlignment", "Left"),
        new("VerticalAlignment", "Top"),
        new("Margin", $"{Round(left)},{Round(top)},0,0"),
    ];

    private static KeyValuePair<XName, string> Attr(string name, double value) => new(name, Round(value));

    private static string Round(double value) =>
        Math.Max(0, Math.Round(value)).ToString(CultureInfo.InvariantCulture);
}
