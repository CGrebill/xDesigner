using XDesigner.Protocol.Rendering;

namespace XDesigner.Core.Rendering;

/// <summary>
/// Lookup over one render's <see cref="ElementLayout"/> tree: element bounds by design id, parents,
/// and hit testing (§8.3). Coordinates are DIPs relative to the rendered frame.
/// </summary>
public sealed class LayoutIndex
{
    private readonly Dictionary<int, ElementLayout> _byId = [];
    private readonly Dictionary<int, int> _parentOf = [];

    public LayoutIndex(ElementLayout? root)
    {
        Root = root;
        if (root is not null)
        {
            Index(root, null);
        }
    }

    public static LayoutIndex Empty { get; } = new(null);

    public ElementLayout? Root { get; }

    public bool Contains(int designId) => _byId.ContainsKey(designId);

    public ElementLayout? Find(int designId) => _byId.GetValueOrDefault(designId);

    public int? ParentOf(int designId) => _parentOf.TryGetValue(designId, out var parent) ? parent : null;

    /// <summary>
    /// The deepest visible element under the point. Later siblings are drawn on top, so they are tried first.
    /// Returns the root when the point is inside the frame but over nothing else; null outside it.
    /// </summary>
    public ElementLayout? HitTest(double x, double y, Func<ElementLayout, bool>? isSelectable = null)
    {
        return Root is null ? null : HitTest(Root, x, y, isSelectable ?? (_ => true));
    }

    private static ElementLayout? HitTest(ElementLayout element, double x, double y, Func<ElementLayout, bool> isSelectable)
    {
        if (!element.IsVisible || !element.Bounds.Contains(x, y))
        {
            return null;
        }

        for (var i = element.Children.Length - 1; i >= 0; i--)
        {
            if (HitTest(element.Children[i], x, y, isSelectable) is { } hit)
            {
                return hit;
            }
        }

        return isSelectable(element) ? element : null;
    }

    private void Index(ElementLayout element, int? parent)
    {
        _byId[element.DesignId] = element;
        if (parent is { } p)
        {
            _parentOf[element.DesignId] = p;
        }

        foreach (var child in element.Children)
        {
            Index(child, element.DesignId);
        }
    }
}
