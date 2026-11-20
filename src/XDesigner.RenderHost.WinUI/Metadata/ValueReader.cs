using System.Collections;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using XDesigner.Protocol.Metadata;
using XDesigner.RenderHost.DesignTime;
using XDesigner.RenderHost.Rendering;

namespace XDesigner.RenderHost.Metadata;

/// <summary>
/// Reads effective property values of a rendered element, formatted as XAML text (§14.4). WinUI can't tell a style's value
/// from a default, so the source is <see cref="ValueSource.Local"/> (set on the element) or <see cref="ValueSource.Default"/>.
/// UI thread only.
/// </summary>
internal static class ValueReader
{
    public static PropertyValueDto[] Read(DependencyObject element, IEnumerable<HostProperty> properties)
    {
        var values = new List<PropertyValueDto>();
        foreach (var property in properties)
        {
            try
            {
                if (property.Dependency is { } dependency)
                {
                    var local = element.ReadLocalValue(dependency);
                    values.Add(new PropertyValueDto(property.Info.Key, Format(element.GetValue(dependency)),
                        local == DependencyProperty.UnsetValue ? ValueSource.Default : ValueSource.Local));
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

    /// <summary>
    /// Finds the element carrying <paramref name="designId"/> under a rendered root: the visual tree first, then content
    /// that isn't realized there (an unselected tab's content, items of a closed menu).
    /// </summary>
    public static DependencyObject? Find(DependencyObject? root, int designId)
    {
        if (root is null)
        {
            return null;
        }

        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        foreach (var element in LayoutTreeBuilder.SelfAndDescendants(root))
        {
            seen.Add(element);
            if (DesignId.GetValue(element) == designId)
            {
                return element;
            }
        }

        var stack = new Stack<DependencyObject>([root]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (DesignId.GetValue(current) == designId)
            {
                return current;
            }

            foreach (var child in ContentOf(current))
            {
                if (seen.Add(child))
                {
                    stack.Push(child);
                }
            }
        }

        return null;
    }

    /// <summary>What an element holds as written in XAML, whether or not it is in the visual tree.</summary>
    private static IEnumerable<DependencyObject> ContentOf(DependencyObject element)
    {
        object? content = element switch
        {
            Panel panel => panel.Children,
            ItemsControl items => items.Items,
            ContentControl control => control.Content,
            Border border => border.Child,
            UserControl user => user.Content,
            Viewbox viewbox => viewbox.Child,
            _ => element is UIElement ui && MetadataProvider.ContentPropertyOf(ui.GetType()) is { } property ? property.GetValue(ui) : null,
        };

        switch (content)
        {
            case DependencyObject single:
                yield return single;
                break;
            case IEnumerable many and not string:
                foreach (var item in many)
                {
                    if (item is DependencyObject d)
                    {
                        yield return d;
                    }
                }

                break;
        }
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
        CornerRadius c => XamlValueText.CornerRadius(c.TopLeft, c.TopRight, c.BottomRight, c.BottomLeft),
        Windows.UI.Color c => XamlValueText.Color(c.A, c.R, c.G, c.B),
        SolidColorBrush b => XamlValueText.Color((byte)Math.Round(b.Color.A * Math.Clamp(b.Opacity, 0, 1)), b.Color.R, b.Color.G, b.Color.B),
        Brush b => $"({b.GetType().Name})",
        FontFamily f => f.Source,
        GridLength g => g.GridUnitType switch
        {
            GridUnitType.Auto => "Auto",
            GridUnitType.Star => g.Value == 1 ? "*" : XamlValueText.Double(g.Value) + "*",
            _ => XamlValueText.Double(g.Value),
        },
        Windows.UI.Text.FontWeight w => FontWeightName(w.Weight),
        Windows.Foundation.Point p => $"{XamlValueText.Double(p.X)},{XamlValueText.Double(p.Y)}",
        Windows.Foundation.Size s => $"{XamlValueText.Double(s.Width)},{XamlValueText.Double(s.Height)}",
        BitmapImage { UriSource: { } uri } => uri.OriginalString,
        Uri uri => uri.OriginalString,
        Enum e => e.ToString(),
        IFormattable f when value.GetType().IsPrimitive => f.ToString(null, CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        DependencyObject d => $"({d.GetType().Name})",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static string FontWeightName(ushort weight) => weight switch
    {
        100 => "Thin",
        200 => "ExtraLight",
        300 => "Light",
        350 => "SemiLight",
        400 => "Normal",
        500 => "Medium",
        600 => "SemiBold",
        700 => "Bold",
        800 => "ExtraBold",
        900 => "Black",
        950 => "ExtraBlack",
        _ => weight.ToString(CultureInfo.InvariantCulture),
    };
}
