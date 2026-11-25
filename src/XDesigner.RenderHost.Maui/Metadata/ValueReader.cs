using System.Globalization;
using XDesigner.Protocol.Metadata;
using XDesigner.RenderHost.DesignTime;
using MauiColor = Microsoft.Maui.Graphics.Color;

namespace XDesigner.RenderHost.Metadata;

/// <summary>
/// Reads effective property values of a rendered element, formatted as XAML text (§14.4). MAUI tells whether a value was set
/// on the element (<c>IsSet</c>), not by what: a set value the XAML writes is Local, one it doesn't (a style's, review L42)
/// is Style, and an unset one Default. UI thread only.
/// </summary>
internal static class ValueReader
{
    /// <param name="written">The properties the element's XAML writes; null counts every set value as Local.</param>
    public static PropertyValueDto[] Read(BindableObject element, IEnumerable<HostProperty> properties, IReadOnlySet<string>? written = null)
    {
        var values = new List<PropertyValueDto>();
        foreach (var property in properties)
        {
            try
            {
                if (property.Bindable is { } bindable)
                {
                    values.Add(new PropertyValueDto(property.Info.Key, Format(element.GetValue(bindable)),
                        !element.IsSet(bindable) ? ValueSource.Default
                        : written is null || written.Contains(property.Info.Key) ? ValueSource.Local
                        : ValueSource.Style));
                }
                else if (property.Clr is { } clr)
                {
                    values.Add(new PropertyValueDto(property.Info.Key, Format(clr.GetValue(element)), ValueSource.Default));
                }
            }
            catch
            {
                // A property that can't be read on this element (or throws at design time) just has no value to show.
            }
        }

        return values.ToArray();
    }

    /// <summary>Finds the element carrying <paramref name="designId"/> under a rendered root (the logical tree, so closed content counts too).</summary>
    public static BindableObject? Find(Element? root, int designId)
    {
        if (root is null)
        {
            return null;
        }

        var stack = new Stack<Element>();
        stack.Push(root);
        var seen = new HashSet<Element>(ReferenceEqualityComparer.Instance);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            if (DesignId.GetValue(current) == designId)
            {
                return current;
            }

            foreach (var child in ((IElementController)current).LogicalChildren)
            {
                stack.Push(child);
            }

            if (current is IVisualTreeElement visual)
            {
                foreach (var child in visual.GetVisualChildren().OfType<Element>())
                {
                    stack.Push(child);
                }
            }
        }

        return null;
    }

    /// <summary>XAML text for a value (same rules as the designer's own serializer, via <see cref="XamlValueText"/>).</summary>
    internal static string? Format(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? "True" : "False",
        double d => XamlValueText.Double(d),
        float f => XamlValueText.Double(f),
        Thickness t => XamlValueText.Thickness(t.Left, t.Top, t.Right, t.Bottom),
        // MAUI's own order: top left, top right, bottom left, bottom right (review M16).
        CornerRadius c => XamlValueText.CornerRadius(c.TopLeft, c.TopRight, c.BottomLeft, c.BottomRight),
        MauiColor c => Color(c),
        SolidColorBrush { Color: { } c } => Color(c),
        Brush b => $"({b.GetType().Name})",
        LayoutOptions o => o.Alignment switch
        {
            LayoutAlignment.Start => "Start",
            LayoutAlignment.Center => "Center",
            LayoutAlignment.End => "End",
            _ => "Fill",
        },
        GridLength g => g.IsAuto ? "Auto" : g.IsStar ? (g.Value == 1 ? "*" : XamlValueText.Double(g.Value) + "*") : XamlValueText.Double(g.Value),
        Rect r => string.Join(',', new[] { r.X, r.Y, r.Width, r.Height }.Select(XamlValueText.Double)),
        FileImageSource { File: { } file } => file,
        UriImageSource { Uri: { } uri } => uri.OriginalString,
        Enum e => e.ToString(),
        IFormattable f when value.GetType().IsPrimitive => f.ToString(null, CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        BindableObject o => $"({o.GetType().Name})",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static string Color(MauiColor color)
    {
        color.ToRgba(out var r, out var g, out var b, out var a);
        return XamlValueText.Color(a, r, g, b);
    }
}
