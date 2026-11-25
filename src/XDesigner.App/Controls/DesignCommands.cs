using XDesigner.App.ViewModels;
using XDesigner.Core.Editing;
using Microsoft.UI.Xaml.Controls;

namespace XDesigner.App.Controls;

/// <summary>What the context menu needs from the main window (clipboard, dialogs, messages).</summary>
public interface IDesignCommandHost
{
    void Cut();

    void Copy();

    void Paste();

    Task EditItemsAsync(DocumentViewModel document, int id);

    Task EditGridAsync(DocumentViewModel document, int id);

    void ShowMessage(string message);
}

/// <summary>
/// The right-click menu of the surface and the outline (§8.3, §9.6): clipboard, delete, select parent, z-order,
/// Wrap in, Edit items, Edit rows and columns, and the default event handler.
/// </summary>
public static class DesignCommands
{
    /// <summary>Set by the main window.</summary>
    public static IDesignCommandHost? Host { get; set; }

    private static readonly (string Name, string Glyph)[] Wrappers =
    [
        ("Grid", ""), ("StackPanel", ""), ("Border", ""), ("Canvas", ""), ("DockPanel", ""),
        ("WrapPanel", ""), ("ScrollViewer", ""),

        // .NET MAUI's: only those the framework has are offered (review L10).
        ("VerticalStackLayout", ""), ("HorizontalStackLayout", ""), ("AbsoluteLayout", ""), ("FlexLayout", ""),
        ("ScrollView", ""),
    ];

    /// <summary>The menu for <paramref name="id"/> (which the caller has made part of the selection).</summary>
    public static MenuFlyout BuildMenu(DocumentViewModel doc, int id)
    {
        var node = doc.Xaml.FindById(id);
        var isRoot = node?.Parent is null;
        var type = node is null ? null : doc.Catalog.Resolve(node);
        var targets = doc.Selection.IsSelected(id) ? doc.Selection.Selected.ToList() : [id];
        var menu = new MenuFlyout();

        Add(menu, "Cut", "", () => Host?.Cut(), enabled: !isRoot, shortcut: "Ctrl+X");
        Add(menu, "Copy", "", () => Host?.Copy(), enabled: !isRoot, shortcut: "Ctrl+C");
        Add(menu, "Paste", "", () => Host?.Paste(), shortcut: "Ctrl+V");
        Add(menu, "Delete", "", () => doc.DeleteSelection(), enabled: !isRoot, shortcut: "Del");
        menu.Items.Add(new MenuFlyoutSeparator());

        Add(menu, "Select parent", "", () =>
        {
            if (node?.Parent is { } parent)
            {
                doc.Selection.Select(parent.Id);
            }
        }, enabled: !isRoot, shortcut: "Esc");
        Add(menu, "Bring to front", "", () => doc.Actions.BringToFront(id), enabled: !isRoot);
        Add(menu, "Send to back", "", () => doc.Actions.SendToBack(id), enabled: !isRoot);

        var wrap = new MenuFlyoutSubItem { Text = targets.Count > 1 ? $"Wrap {targets.Count} controls in" : "Wrap in", IsEnabled = !isRoot };
        foreach (var (name, glyph) in Wrappers)
        {
            if (doc.Catalog.Find(doc.Catalog.Profile.BuiltIn(name)) is { } wrapperType)
            {
                var item = new MenuFlyoutItem { Text = name, Icon = new FontIcon { Glyph = glyph } };
                item.Click += (_, _) =>
                {
                    if (doc.Actions.WrapIn(targets, wrapperType) is { } wrapper)
                    {
                        doc.Selection.Select(wrapper);
                    }
                    else
                    {
                        Host?.ShowMessage(targets.Count > 1
                            ? $"To wrap several controls they must be side by side in the same container, and {name} can hold only one control."
                            : $"Couldn't wrap {node?.DisplayName} in {name}.");
                    }
                };
                wrap.Items.Add(item);
            }
        }

        menu.Items.Add(wrap);

        var itemsKind = ItemsEditing.KindOf(doc.Catalog, type);
        var isGrid = doc.Catalog.IsA(type, "Grid");
        if (itemsKind is not null || isGrid)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        if (itemsKind is not null)
        {
            Add(menu, $"Edit {itemsKind.Noun}s…", "", () => _ = Host?.EditItemsAsync(doc, id));
        }

        if (isGrid)
        {
            Add(menu, "Edit rows and columns…", "", () => _ = Host?.EditGridAsync(doc, id));
        }

        if (type?.DefaultEventName is { } evt)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            Add(menu, $"Add {evt} handler", "", () => _ = doc.CreateDefaultHandlerAsync(id));
        }

        return menu;
    }

    private static void Add(MenuFlyout menu, string text, string glyph, Action action, bool enabled = true, string? shortcut = null)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
        if (shortcut is not null)
        {
            item.KeyboardAcceleratorTextOverride = shortcut;
        }

        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
}
