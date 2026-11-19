using XDesigner.Core.Xaml;

namespace XDesigner.Core.Editing;

/// <summary>Keyboard selection moves on the design surface (§8.3): Esc selects the parent, Tab cycles siblings.</summary>
public static class SelectionNavigator
{
    /// <summary>The parent of the primary selection, or null at the root.</summary>
    public static int? Parent(XamlDocument document, SelectionService selection) =>
        selection.Primary is { } id ? document.FindById(id)?.Parent?.Id : null;

    /// <summary>The next (or previous) sibling of the primary selection, wrapping around; the root if nothing is selected.</summary>
    public static int? Sibling(XamlDocument document, SelectionService selection, bool forward)
    {
        if (selection.Primary is not { } id || document.FindById(id) is not { } node)
        {
            return document.Root.Id;
        }

        if (node.Parent is not { } parent)
        {
            return node.Children.Count > 0 ? node.Children[0].Id : node.Id;
        }

        var siblings = parent.Children;
        var index = siblings.ToList().FindIndex(s => s.Id == id);
        var next = (index + (forward ? 1 : -1) + siblings.Count) % siblings.Count;
        return siblings[next].Id;
    }
}
