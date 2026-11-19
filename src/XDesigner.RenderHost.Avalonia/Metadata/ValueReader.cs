using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Diagnostics;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using XDesigner.Protocol.Metadata;
using XDesigner.RenderHost.DesignTime;

namespace XDesigner.RenderHost.Metadata;

/// <summary>Reads effective property values of a rendered element, formatted as XAML text (§14.4). UI thread only.</summary>
internal static class ValueReader
{
    public static PropertyValueDto[] Read(AvaloniaObject element, IEnumerable<HostProperty> properties)
    {
        var values = new List<PropertyValueDto>();
        foreach (var property in properties)
        {
            try
            {
                if (property.Avalonia is { } avalonia)
                {
                    var diagnostic = element.GetDiagnostic(avalonia);
                    values.Add(new PropertyValueDto(property.Info.Key, Format(diagnostic.Value), SourceOf(diagnostic.Priority)));
                }
                else if (property.Clr is { } clr)
                {
                    values.Add(new PropertyValueDto(property.Info.Key, Format(clr.GetValue(element)), ValueSource.Default));
                }
            }
            catch
            {
                // A property getter that throws on a design-time instance just has no value to show.
            }
        }

        return values.ToArray();
    }

    /// <summary>Finds the element carrying <paramref name="designId"/> in a rendered root (logical tree first, then visual).</summary>
    public static AvaloniaObject? Find(object? root, int designId)
    {
        if (root is not AvaloniaObject start)
        {
            return null;
        }

        if (start.GetValue(DesignId.ValueProperty) == designId)
        {
            return start;
        }

        if (start is ILogical logical)
        {
            foreach (var d in logical.GetLogicalDescendants())
            {
                if (d is AvaloniaObject o && o.GetValue(DesignId.ValueProperty) == designId)
                {
                    return o;
                }
            }
        }

        if (start is Visual visual)
        {
            foreach (var d in visual.GetVisualDescendants())
            {
                if (d.GetValue(DesignId.ValueProperty) == designId)
                {
                    return d;
                }
            }
        }

        return null;
    }

    private static ValueSource SourceOf(BindingPriority priority) => priority switch
    {
        BindingPriority.Animation => ValueSource.Animation,
        BindingPriority.LocalValue => ValueSource.Local,
        BindingPriority.StyleTrigger or BindingPriority.Style => ValueSource.Style,
        BindingPriority.Template => ValueSource.Template,
        BindingPriority.Inherited => ValueSource.Inherited,
        _ => ValueSource.Default,
    };

    /// <summary>XAML text for a value (same rules as the designer's own serializer, via <see cref="XamlValueText"/>).</summary>
    internal static string? Format(object? value) => value switch
    {
        null => null,
        UnsetValueType => null,
        string s => s,
        bool b => b ? "True" : "False",
        double d => XamlValueText.Double(d),
        float f => XamlValueText.Double(f),
        Thickness t => XamlValueText.Thickness(t.Left, t.Top, t.Right, t.Bottom),
        CornerRadius c => XamlValueText.CornerRadius(c.TopLeft, c.TopRight, c.BottomRight, c.BottomLeft),
        Color c => XamlValueText.Color(c.A, c.R, c.G, c.B),
        ISolidColorBrush b => FormatBrush(b),
        IBrush b => $"({b.GetType().Name})",
        FontFamily f => f.ToString(),
        GridLength g => g.ToString(),
        Enum e => e.ToString(),
        IFormattable f when value.GetType().IsPrimitive => f.ToString(null, CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        StyledElement element => $"({element.GetType().Name})",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static string FormatBrush(ISolidColorBrush brush)
    {
        var c = brush.Color;
        var alpha = (byte)Math.Round(c.A * Math.Clamp(brush.Opacity, 0, 1));
        return XamlValueText.Color(alpha, c.R, c.G, c.B);
    }
}
