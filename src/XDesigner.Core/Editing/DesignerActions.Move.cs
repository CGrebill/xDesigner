using System.Globalization;
using System.Xml.Linq;
using XDesigner.Core.Layout;
using XDesigner.Core.Metadata;
using XDesigner.Core.Rendering;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Rendering;

namespace XDesigner.Core.Editing;

/// <summary>Which edges a resize handle moves.</summary>
[Flags]
public enum ResizeEdges
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
}

/// <summary>Move, resize and nudge (§7.1, §8.3). Each call is one undo step.</summary>
public sealed partial class DesignerActions
{
    /// <summary>Attached properties owned by each panel kind, removed when an element leaves that kind of panel.</summary>
    private static readonly (string Attribute, PanelKind Owner)[] PanelAttachedProperties =
    [
        ("Canvas.Left", PanelKind.Canvas), ("Canvas.Top", PanelKind.Canvas), ("Canvas.Right", PanelKind.Canvas), ("Canvas.Bottom", PanelKind.Canvas),
        ("Grid.Row", PanelKind.Grid), ("Grid.Column", PanelKind.Grid), ("Grid.RowSpan", PanelKind.Grid), ("Grid.ColumnSpan", PanelKind.Grid),
        ("DockPanel.Dock", PanelKind.DockPanel),
    ];

    /// <summary>Attached properties a placement in that kind of panel decides (so they're cleared when the plan doesn't set them).</summary>
    private static readonly Dictionary<PanelKind, string[]> PlacementControlled = new()
    {
        [PanelKind.Canvas] = ["Canvas.Left", "Canvas.Top", "Canvas.Right", "Canvas.Bottom"],
        [PanelKind.Grid] = ["Grid.Row", "Grid.Column"],
        [PanelKind.DockPanel] = ["DockPanel.Dock"],
    };

    /// <summary>Containers where elements sit at a position (attributes), rather than in an order.</summary>
    public static bool IsPositional(PanelKind? kind) => kind is PanelKind.Canvas or PanelKind.Grid or PanelKind.Other;

    /// <summary>
    /// Moves one element as planned by <see cref="DropPlanner.Plan"/> (called with the element and its descendants
    /// excluded, and <c>positionX/Y</c> = its new top-left): repositions it, reorders it within its panel, or
    /// moves it into another container, fixing up layout attributes. Returns false if nothing changed.
    /// </summary>
    public bool MoveTo(int id, DropPlan plan, LayoutIndex layout)
    {
        if (_document.FindById(id) is not { Parent: { } oldParent } node
            || _document.FindById(plan.ContainerId) is not { } target
            || target.AncestorsAndSelf().Any(a => a.Id == id))
        {
            return false;
        }

        var targetKind = layout.Find(target.Id)?.Panel?.Kind;
        var commands = new List<IUndoableCommand>();
        if (oldParent.Id == target.Id)
        {
            var siblings = ObjectChildren(node.Element.Parent!);
            var oldIndex = siblings.IndexOf(node.Element);
            int? newIndex = plan.Indicator.Kind switch
            {
                DropIndicatorKind.InsertionLine => plan.Index > oldIndex ? plan.Index - 1 : plan.Index,
                DropIndicatorKind.DockArea => DockIndex(target.Element, siblings.Count - 1, plan),
                _ => null,
            };
            if (plan.Indicator.Kind == DropIndicatorKind.InsertionLine && newIndex == oldIndex)
            {
                return false;
            }

            if (newIndex is { } index && index != oldIndex)
            {
                commands.Add(new RemoveElementCommand(id));
                commands.Add(new InsertElementCommand(target.Id, node.Element, index, $"Move {node.DisplayName}"));
            }

            commands.AddRange(PlacementCommands(node, plan, targetKind, reparent: false, layout.Find(id)?.Bounds, layout.Find(id)?.Margin));
        }
        else
        {
            commands.Add(new RemoveElementCommand(id));
            commands.Add(new InsertElementCommand(target.Id, node.Element, plan.Index, $"Move {node.DisplayName}"));
            commands.AddRange(PlacementCommands(node, plan, targetKind, reparent: true, layout.Find(id)?.Bounds, layout.Find(id)?.Margin));
        }

        return Execute($"Move {node.DisplayName}", commands);
    }

    /// <summary>
    /// Shifts elements by (<paramref name="dx"/>, <paramref name="dy"/>) DIPs inside their current containers.
    /// Only elements in positional containers (Canvas, Grid, plain panels) move. Returns how many moved.
    /// </summary>
    public int MoveBy(IReadOnlyCollection<int> ids, double dx, double dy, LayoutIndex layout, ControlCatalog catalog)
    {
        var commands = MoveByCommands(ids, dx, dy, layout, catalog, out var moved);
        Execute(moved == 1 ? $"Move {_document.FindById(ids.First())?.DisplayName}" : $"Move {moved} controls", commands);
        return moved;
    }

    /// <summary>
    /// Resizes an element to <paramref name="bounds"/> (frame DIPs). Width/Height follow the moved edges; moving the
    /// left/top edge also repositions it. In a Grid or plain panel the element becomes top-left aligned with a margin,
    /// so it doesn't jump when it gains a fixed size.
    /// </summary>
    public bool Resize(int id, RectDto bounds, ResizeEdges edges, LayoutIndex layout, ControlCatalog catalog)
    {
        if (_document.FindById(id) is not { Parent: { } parent } node || edges == ResizeEdges.None)
        {
            return false;
        }

        var commands = new List<IUndoableCommand>();
        var sized = new HashSet<string>(StringComparer.Ordinal);
        if ((edges & (ResizeEdges.Left | ResizeEdges.Right)) != 0)
        {
            commands.AddRange(SetIfChanged(node, "Width", Number(Math.Max(1, bounds.Width))));
            sized.Add("Width");
        }

        if ((edges & (ResizeEdges.Top | ResizeEdges.Bottom)) != 0)
        {
            commands.AddRange(SetIfChanged(node, "Height", Number(Math.Max(1, bounds.Height))));
            sized.Add("Height");
        }

        var kind = layout.Find(parent.Id)?.Panel?.Kind;
        var movesOrigin = (edges & (ResizeEdges.Left | ResizeEdges.Top)) != 0;
        if (IsPositional(kind) && (movesOrigin || kind != PanelKind.Canvas)
            && DropPlanner.PlanInto(_document, catalog, layout, parent.Id, bounds.X, bounds.Y, new HashSet<int> { id }) is { } plan)
        {
            // Pin the size of the axis not being dragged too: a stretched control that becomes top-left aligned
            // would otherwise collapse to its content size on that axis.
            commands.AddRange(PlacementCommands(node, plan, kind, reparent: false, bounds, layout.Find(id)?.Margin, sized));
        }

        return Execute($"Resize {node.DisplayName}", commands);
    }

    /// <summary>
    /// Arrow keys (§8.3): elements at a position move by (<paramref name="dx"/>, <paramref name="dy"/>); an element in a
    /// StackPanel/WrapPanel/UniformGrid moves one place earlier (left/up) or later (right/down) instead.
    /// </summary>
    public bool Nudge(IReadOnlyCollection<int> ids, double dx, double dy, LayoutIndex layout, ControlCatalog catalog)
    {
        var commands = MoveByCommands(ids, dx, dy, layout, catalog, out _);
        var forward = dx > 0 || dy > 0;
        var ordered = ids.Select(_document.FindById).OfType<ElementNode>()
            .Where(n => n.Parent is { } p && layout.Find(p.Id)?.Panel?.Kind is PanelKind.StackPanel or PanelKind.WrapPanel or PanelKind.UniformGrid);
        foreach (var group in ordered.GroupBy(n => n.Parent!.Id))
        {
            commands.AddRange(ReorderCommands(group.Key, group.Select(n => n.Element).ToHashSet(), forward));
        }

        var first = _document.FindById(ids.FirstOrDefault())?.DisplayName;
        return Execute(ids.Count == 1 ? $"Move {first}" : $"Move {ids.Count} controls", commands);
    }

    /// <summary>The ids plus all their descendants (what a move must not drop into).</summary>
    public IReadOnlySet<int> WithDescendants(IEnumerable<int> ids)
    {
        var set = new HashSet<int>();
        foreach (var id in ids)
        {
            if (_document.FindById(id) is { } node)
            {
                foreach (var e in node.Element.DescendantsAndSelf().Where(RenderCopyBuilder.IsObjectElement))
                {
                    set.Add(_document.NodeFor(e).Id);
                }
            }
        }

        return set;
    }

    private List<IUndoableCommand> MoveByCommands(IReadOnlyCollection<int> ids, double dx, double dy, LayoutIndex layout, ControlCatalog catalog, out int moved)
    {
        var commands = new List<IUndoableCommand>();
        moved = 0;
        foreach (var id in ids)
        {
            if (_document.FindById(id) is not { Parent: { } parent } node
                || layout.Find(id) is not { } element
                || !IsPositional(layout.Find(parent.Id)?.Panel?.Kind))
            {
                continue;
            }

            var plan = DropPlanner.PlanInto(_document, catalog, layout, parent.Id, element.Bounds.X + dx, element.Bounds.Y + dy, new HashSet<int> { id });
            if (plan is not null)
            {
                commands.AddRange(PlacementCommands(node, plan, layout.Find(parent.Id)?.Panel?.Kind, reparent: false, element.Bounds, element.Margin));
                moved++;
            }
        }

        return commands;
    }

    /// <summary>
    /// Moves the <paramref name="selected"/> children of a stack one place forward or back. Selected neighbours move
    /// together as a block, and a block at the end stays where it is.
    /// </summary>
    private List<IUndoableCommand> ReorderCommands(int parentId, HashSet<XElement> selected, bool forward)
    {
        var parent = _document.FindById(parentId)!.Element;
        var order = ObjectChildren(parent);
        if (forward)
        {
            for (var i = order.Count - 2; i >= 0; i--)
            {
                if (selected.Contains(order[i]) && !selected.Contains(order[i + 1]))
                {
                    (order[i], order[i + 1]) = (order[i + 1], order[i]);
                }
            }
        }
        else
        {
            for (var i = 1; i < order.Count; i++)
            {
                if (selected.Contains(order[i]) && !selected.Contains(order[i - 1]))
                {
                    (order[i], order[i - 1]) = (order[i - 1], order[i]);
                }
            }
        }

        // Replay the new order as remove/insert pairs, simulating each step so the indices are right when undone too.
        var commands = new List<IUndoableCommand>();
        var current = ObjectChildren(parent);
        for (var k = 0; k < order.Count; k++)
        {
            if (current[k] != order[k])
            {
                current.Remove(order[k]);
                current.Insert(k, order[k]);
                commands.Add(new RemoveElementCommand(_document.NodeFor(order[k]).Id));
                commands.Add(new InsertElementCommand(parentId, order[k], k, "Move"));
            }
        }

        return commands;
    }

    /// <summary>Where a child moved within its own DockPanel goes: before the filling last child when docked, else last.</summary>
    private static int DockIndex(XElement dockPanel, int remaining, DropPlan plan)
    {
        var lastChildFills = !string.Equals((string?)dockPanel.Attribute("LastChildFill"), "False", StringComparison.OrdinalIgnoreCase);
        var docks = plan.Attributes.Any(a => a.Key == "DockPanel.Dock");
        return docks && lastChildFills && remaining > 0 ? remaining - 1 : remaining;
    }

    /// <summary>
    /// Attribute changes that make <paramref name="node"/>'s layout match <paramref name="plan"/>: clears attached
    /// properties of the panel it left (or that the new placement decides), drops designer positioning
    /// (Left/Top alignment + margin) when the new container doesn't position, sets the plan's attributes, and pins
    /// the current size when a stretching element becomes positioned so it doesn't shrink.
    /// </summary>
    /// <param name="currentMargin">The element's laid-out margin: on a Canvas, its bounds include it, so the position doesn't.</param>
    /// <param name="alreadySized">Size attributes the caller sets itself (a resize), not to be pinned again.</param>
    private static List<IUndoableCommand> PlacementCommands(
        ElementNode node, DropPlan plan, PanelKind? targetKind, bool reparent, RectDto? currentBounds, ThicknessDto? currentMargin = null,
        IReadOnlySet<string>? alreadySized = null)
    {
        var element = node.Element;
        var planned = plan.Attributes.ToDictionary(a => a.Key, a => a.Value);
        var commands = new List<IUndoableCommand>();
        var controlled = targetKind is { } k && PlacementControlled.TryGetValue(k, out var c) ? c : [];

        foreach (var (attribute, owner) in PanelAttachedProperties)
        {
            if (element.Attribute(attribute) is not null && !planned.ContainsKey(attribute)
                && (controlled.Contains(attribute) || (reparent && owner != targetKind)))
            {
                commands.Add(new SetAttributeCommand(node.Id, attribute, null));
            }
        }

        var hasDesignerPosition = (string?)element.Attribute("HorizontalAlignment") == "Left"
            && (string?)element.Attribute("VerticalAlignment") == "Top"
            && element.Attribute("Margin") is not null;
        if (hasDesignerPosition && !planned.ContainsKey("Margin"))
        {
            commands.Add(new SetAttributeCommand(node.Id, "HorizontalAlignment", null));
            commands.Add(new SetAttributeCommand(node.Id, "VerticalAlignment", null));
            commands.Add(new SetAttributeCommand(node.Id, "Margin", null));
        }
        else if (targetKind == PanelKind.Canvas && currentMargin is { } m)
        {
            // The margin stays, and Canvas.Left/Top position the margin box: subtract it so the control doesn't jump.
            Subtract(planned, "Canvas.Left", m.Left);
            Subtract(planned, "Canvas.Top", m.Top);
        }

        if (planned.ContainsKey("Margin") && currentBounds is { } b)
        {
            if (alreadySized?.Contains("Width") != true && element.Attribute("Width") is null
                && (string?)element.Attribute("HorizontalAlignment") is null or "Stretch")
            {
                commands.Add(new SetAttributeCommand(node.Id, "Width", Number(b.Width)));
            }

            if (alreadySized?.Contains("Height") != true && element.Attribute("Height") is null
                && (string?)element.Attribute("VerticalAlignment") is null or "Stretch")
            {
                commands.Add(new SetAttributeCommand(node.Id, "Height", Number(b.Height)));
            }
        }

        foreach (var (attribute, value) in planned)
        {
            commands.AddRange(SetIfChanged(node, attribute, value));
        }

        return commands;
    }

    private static void Subtract(Dictionary<XName, string> planned, string attribute, double amount)
    {
        if (amount != 0 && planned.TryGetValue(attribute, out var text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            planned[attribute] = Math.Max(0, Math.Round(value - amount)).ToString(CultureInfo.InvariantCulture);
        }
    }

    private static IEnumerable<IUndoableCommand> SetIfChanged(ElementNode node, XName attribute, string value) =>
        (string?)node.Element.Attribute(attribute) == value ? [] : [new SetAttributeCommand(node.Id, attribute, value)];

    private static List<XElement> ObjectChildren(XElement container) =>
        container.Elements().Where(RenderCopyBuilder.IsObjectElement).ToList();

    private bool Execute(string description, List<IUndoableCommand> commands)
    {
        if (commands.Count == 0)
        {
            return false;
        }

        _undo.Execute(new CompositeCommand(description, commands));
        return true;
    }

    private static string Number(double value) => Math.Round(value).ToString(CultureInfo.InvariantCulture);
}
