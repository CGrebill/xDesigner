using System.Xml.Linq;
using XDesigner.Core.Metadata;
using XDesigner.Core.Properties;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;

namespace XDesigner.Core.Editing;

/// <summary>One item in the Collection Editor (§9.4): an existing element (by id) or a new one, with its text and sub-items.</summary>
/// <param name="ExistingId">The item's node id, or null for an item added in the editor.</param>
/// <param name="TextEditable">False when the item's content is a control or complex XAML (its text is shown, not edited).</param>
public sealed class ItemDraft(int? existingId, string text, bool textEditable = true)
{
    public int? ExistingId { get; } = existingId;

    public string Text { get; set; } = text;

    public bool TextEditable { get; } = textEditable;

    /// <summary>Sub-items (menu items in a menu, tree nodes); unused for flat lists.</summary>
    public List<ItemDraft> Children { get; } = [];

    public override string ToString() => Text;
}

/// <summary>What the Collection Editor creates for an items control.</summary>
/// <param name="ItemType">Element name of a new item: <c>ListBoxItem</c>, <c>TabItem</c>, <c>MenuItem</c>…</param>
/// <param name="TextProperty"><c>Content</c> or <c>Header</c>.</param>
/// <param name="Nested">Items can have sub-items (Menu, TreeView).</param>
public sealed record ItemsKind(string ItemType, string TextProperty, bool Nested, string Noun);

public static class ItemsEditing
{
    /// <summary>
    /// The kind of items an Items control holds, or null when the editor doesn't support it. The table is the
    /// framework's (§9.4); WinUI and MAUI get theirs with their render hosts.
    /// </summary>
    public static ItemsKind? KindOf(ControlCatalog catalog, ControlTypeInfo? type)
    {
        if (type is null || catalog.Profile.Family != Frameworks.FrameworkFamily.Avalonia)
        {
            return null;
        }

        return true switch
        {
            _ when catalog.IsA(type, "TabControl") => new("TabItem", "Header", false, "tab"),
            _ when catalog.IsA(type, "Menu") || catalog.IsA(type, "ContextMenu") => new("MenuItem", "Header", true, "menu item"),
            _ when catalog.IsA(type, "TreeView") => new("TreeViewItem", "Header", true, "node"),
            _ when catalog.IsA(type, "ComboBox") => new("ComboBoxItem", "Content", false, "item"),
            _ when catalog.IsA(type, "ListBox") => new("ListBoxItem", "Content", false, "item"),
            _ => null,
        };
    }

    /// <summary>The control's current items as drafts (with sub-items for nested kinds).</summary>
    public static List<ItemDraft> Read(ElementNode control, ItemsKind kind)
    {
        var drafts = new List<ItemDraft>();
        foreach (var item in Items(control.Element))
        {
            var node = control.Document.NodeFor(item);
            var draft = DraftOf(node, kind);
            if (kind.Nested)
            {
                draft.Children.AddRange(Read(node, kind));
            }

            drafts.Add(draft);
        }

        return drafts;
    }

    /// <summary>Item elements of an items control: object children, or those inside its <c>&lt;X.Items&gt;</c> element.</summary>
    internal static List<XElement> Items(XElement control) =>
        (ItemsElement(control) ?? control).Elements().Where(RenderCopyBuilder.IsObjectElement).ToList();

    internal static XElement? ItemsElement(XElement control) =>
        control.Elements().FirstOrDefault(e => e.Name.LocalName == $"{control.Name.LocalName}.Items");

    private static ItemDraft DraftOf(ElementNode item, ItemsKind kind)
    {
        var e = item.Element;
        if (e.Attribute(kind.TextProperty) is { } a)
        {
            return new ItemDraft(item.Id, XamlValueSerializer.FromAttribute(a.Value), !XamlValueSerializer.IsMarkupExtension(a.Value));
        }

        // Text inside a Content item; a content control or other child makes it read-only.
        var hasObjectContent = e.Elements().Any(c => RenderCopyBuilder.IsObjectElement(c) && c.Name.LocalName != kind.ItemType)
            || e.Elements().Any(c => c.Name.LocalName == $"{e.Name.LocalName}.{kind.TextProperty}");
        if (kind.TextProperty == "Content" && PropertyGridBuilder.TextContentOf(e) is { } text)
        {
            return new ItemDraft(item.Id, text, !hasObjectContent);
        }

        return new ItemDraft(item.Id, hasObjectContent ? $"({item.DisplayName})" : "", !hasObjectContent);
    }
}

public sealed partial class DesignerActions
{
    /// <summary>
    /// Applies the Collection Editor (§9.4) as one undo step: renamed items get their new text, removed ones are deleted,
    /// added ones are created (a new tab gets a Grid to drop controls into), and the order follows the list.
    /// </summary>
    public bool SetItems(int controlId, IReadOnlyList<ItemDraft> items, ItemsKind kind)
    {
        if (_document.FindById(controlId) is not { } control)
        {
            return false;
        }

        var commands = new List<IUndoableCommand>();
        ApplyLevel(control, items, kind, commands);
        return Execute($"Edit {kind.Noun}s of {control.DisplayName}", commands);
    }

    private void ApplyLevel(ElementNode container, IReadOnlyList<ItemDraft> drafts, ItemsKind kind, List<IUndoableCommand> commands)
    {
        var existing = ItemsEditing.Items(container.Element);
        var into = ItemsEditing.ItemsElement(container.Element)?.Name;
        var ns = _document.Profile.DefaultNamespace;

        // Text changes on items that stay.
        foreach (var draft in drafts.Where(d => d.ExistingId is not null && d.TextEditable))
        {
            if (_document.FindById(draft.ExistingId!.Value) is { } node)
            {
                var attribute = node.Element.Attribute(kind.TextProperty);
                var textContent = kind.TextProperty == "Content" && attribute is null ? PropertyGridBuilder.TextContentOf(node.Element) : null;
                if (textContent is not null && textContent != draft.Text)
                {
                    commands.Add(new SetTextContentCommand(node.Id, draft.Text, "Rename"));
                }
                else if (textContent is null && (attribute is null ? "" : XamlValueSerializer.FromAttribute(attribute.Value)) != draft.Text)
                {
                    commands.Add(new SetAttributeCommand(node.Id, kind.TextProperty, draft.Text.Length == 0 ? null : XamlValueSerializer.ToAttribute(draft.Text)));
                }
            }
        }

        // Order, additions and removals: when the list changed, take every item out and put the list back in order.
        var sameOrder = drafts.All(d => d.ExistingId is not null)
            && drafts.Select(d => d.ExistingId!.Value).SequenceEqual(existing.Select(e => _document.NodeFor(e).Id));
        var elements = new List<(ItemDraft Draft, XElement Element, bool IsNew)>();
        foreach (var draft in drafts)
        {
            if (draft.ExistingId is { } id && _document.FindById(id) is { } node)
            {
                elements.Add((draft, node.Element, false));
            }
            else
            {
                var element = new XElement(ns + kind.ItemType);
                if (draft.Text.Length > 0)
                {
                    element.SetAttributeValue(kind.TextProperty, XamlValueSerializer.ToAttribute(draft.Text));
                }

                elements.Add((draft, element, true));
            }
        }

        if (!sameOrder)
        {
            commands.AddRange(existing.Select(e => (IUndoableCommand)new RemoveElementCommand(_document.NodeFor(e).Id, "Edit items")));
            for (var i = 0; i < elements.Count; i++)
            {
                commands.Add(new InsertElementCommand(container.Id, elements[i].Element, i, "Edit items", into));
                if (elements[i].IsNew && kind.ItemType == "TabItem")
                {
                    // A tab's content area, so controls can be dropped into the tab on the surface.
                    commands.Add(new InsertElementCommand(_document.NodeFor(elements[i].Element).Id, new XElement(ns + "Grid"), 0, "Edit items"));
                }
            }
        }

        if (kind.Nested)
        {
            foreach (var (draft, element, _) in elements)
            {
                if (draft.Children.Count > 0 || ItemsEditing.Items(element).Count > 0)
                {
                    ApplyLevel(_document.NodeFor(element), draft.Children, kind, commands);
                }
            }
        }
    }
}
