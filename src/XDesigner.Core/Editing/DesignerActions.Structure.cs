using System.Xml.Linq;
using XDesigner.Core.Layout;
using XDesigner.Core.Metadata;
using XDesigner.Core.Rendering;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;
using XDesigner.Protocol.Rendering;

namespace XDesigner.Core.Editing;

/// <summary>
/// Structural edits for the outline and context menus (§9.6, §8.3): reorder, move into another container, wrap in a
/// panel. Each is one undo step; the moved elements keep their ids (same <see cref="XElement"/> instances).
/// </summary>
public sealed partial class DesignerActions
{
    /// <summary>Attributes that place an element in its parent; a wrapper takes them over (Wrap in…).</summary>
    private static readonly string[] PlacementAttributes =
    [
        "Grid.Row", "Grid.Column", "Grid.RowSpan", "Grid.ColumnSpan", "Canvas.Left", "Canvas.Top", "Canvas.Right", "Canvas.Bottom",
        "DockPanel.Dock", "Margin", "HorizontalAlignment", "VerticalAlignment",
    ];

    /// <summary>Moves an element to position <paramref name="newIndex"/> among its siblings (z-order in a Canvas/Grid). False if unchanged.</summary>
    public bool Reorder(int id, int newIndex)
    {
        if (_document.FindById(id) is not { Parent: { } parent } node)
        {
            return false;
        }

        var siblings = ObjectChildren(node.Element.Parent!);
        var oldIndex = siblings.IndexOf(node.Element);
        newIndex = Math.Clamp(newIndex, 0, siblings.Count - 1);
        if (newIndex == oldIndex)
        {
            return false;
        }

        var into = node.Element.Parent != parent.Element ? node.Element.Parent!.Name : null;
        return Execute($"Move {node.DisplayName}",
            [new RemoveElementCommand(id), new InsertElementCommand(parent.Id, node.Element, newIndex, $"Move {node.DisplayName}", into)]);
    }

    /// <summary>Bring to front: last child (drawn on top of its siblings).</summary>
    public bool BringToFront(int id) => Reorder(id, int.MaxValue);

    /// <summary>Send to back: first child.</summary>
    public bool SendToBack(int id) => Reorder(id, 0);

    /// <summary>
    /// Outline drag and drop: moves an element into <paramref name="containerId"/> at <paramref name="index"/> (its position
    /// among the container's children). Within the same parent it's a reorder; into another container the placement is
    /// fixed up as for a surface move (a Canvas/Grid places it near the top-left). Returns why not, or null.
    /// </summary>
    public string? MoveToContainer(int id, int containerId, int index, LayoutIndex layout, ControlCatalog catalog)
    {
        if (_document.FindById(id) is not { Parent: { } parent } node)
        {
            return "The root element can't be moved.";
        }

        if (_document.FindById(containerId) is not { } container)
        {
            return "The target is no longer in the document.";
        }

        if (container.AncestorsAndSelf().Any(a => a.Id == id))
        {
            return $"{node.DisplayName} can't be moved into itself.";
        }

        if (container.Id == parent.Id)
        {
            var siblings = ObjectChildren(node.Element.Parent!);
            var oldIndex = siblings.IndexOf(node.Element);
            Reorder(id, index > oldIndex ? index - 1 : index);
            return null;
        }

        if (catalog.Resolve(container) is not { } type || !DropPlanner.Accepts(container, type, WithDescendants([id])))
        {
            return $"{container.DisplayName} can't hold another control here.";
        }

        // Where it would land if dropped at the container's top-left; then the requested position in its children.
        var bounds = layout.Find(containerId)?.Bounds;
        var plan = bounds is { } b
            ? DropPlanner.PlanInto(_document, catalog, layout, containerId, b.X + 8, b.Y + 8, WithDescendants([id]))
            : null;
        plan ??= new DropPlan(containerId, index, [], false, new DropIndicator(DropIndicatorKind.Container, bounds ?? new RectDto(0, 0, 0, 0)));
        return MoveTo(id, plan with { Index = index }, layout) ? null : "Nothing to move.";
    }

    /// <summary>
    /// Wrap in…: puts the elements (siblings, in their order) inside a new container of <paramref name="wrapperType"/> at the
    /// first one's position. The wrapper takes over the first element's placement in the parent (Grid cell, Canvas
    /// position, dock side, margin, alignment); the wrapped elements lose theirs. Returns the wrapper's id, or null.
    /// </summary>
    public int? WrapIn(IReadOnlyCollection<int> ids, ControlTypeInfo wrapperType)
    {
        var nodes = ids.Select(_document.FindById).OfType<ElementNode>().Where(n => n.Parent is not null).ToList();
        if (nodes.Count == 0 || nodes.Select(n => n.Element.Parent).Distinct().Count() != 1
            || (wrapperType.Content is not ContentModel.Children && nodes.Count > 1))
        {
            return null;
        }

        var parentElement = nodes[0].Element.Parent!;
        var siblings = ObjectChildren(parentElement);
        nodes = nodes.OrderBy(n => siblings.IndexOf(n.Element)).ToList();
        var parent = nodes[0].Parent!;
        var index = siblings.IndexOf(nodes[0].Element);
        var into = parentElement != parent.Element ? parentElement.Name : null;

        var wrapper = new XElement(ElementNamespace(_document.Xml.Root!, wrapperType.XmlNamespace, wrapperType.ClrNamespace, out _) + wrapperType.Name);
        // In the document's own spelling (MAUI: AbsoluteLayout.LayoutBounds, HorizontalOptions…).
        var placementAttributes = PlacementAttributes.Select(a => _document.Profile.Layout.NameOf(a)).Distinct().ToList();
        foreach (var attribute in placementAttributes)
        {
            if (nodes[0].Element.Attribute(attribute) is { } a)
            {
                wrapper.SetAttributeValue(attribute, a.Value);
            }
        }

        var wrapperId = _document.NodeFor(wrapper).Id;
        var commands = new List<IUndoableCommand>();
        foreach (var node in nodes)
        {
            commands.AddRange(placementAttributes.Where(a => node.Element.Attribute(a) is not null)
                .Select(a => (IUndoableCommand)new SetAttributeCommand(node.Id, a, null)));
        }

        commands.AddRange(nodes.Select(n => (IUndoableCommand)new RemoveElementCommand(n.Id)));
        commands.Add(new InsertElementCommand(parent.Id, wrapper, index, "Wrap", into));
        commands.AddRange(nodes.Select((n, i) => (IUndoableCommand)new InsertElementCommand(wrapperId, n.Element, i, "Wrap")));

        var what = nodes.Count == 1 ? nodes[0].DisplayName : $"{nodes.Count} controls";
        return Execute($"Wrap {what} in {wrapperType.Name}", commands) ? wrapperId : null;
    }
}
