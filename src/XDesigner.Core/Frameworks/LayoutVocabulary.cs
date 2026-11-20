using System.Globalization;
using System.Xml.Linq;

namespace XDesigner.Core.Frameworks;

/// <summary>
/// How a framework spells the layout attributes the designer writes (§7.1). Placement code works with canonical names,
/// the Avalonia and WinUI ones: <c>Width</c>/<c>Height</c>, <c>HorizontalAlignment="Left"</c>/<c>VerticalAlignment="Top"</c>
/// (and <c>Stretch</c>), <c>Canvas.Left</c>/<c>Canvas.Top</c>. This translates them to and from the document's own:
/// MAUI writes <c>WidthRequest</c>, <c>HorizontalOptions="Start"</c> (<c>Fill</c>), and positions children of an
/// AbsoluteLayout with one <c>AbsoluteLayout.LayoutBounds="x,y,w,h"</c> attribute.
/// </summary>
public sealed record LayoutVocabulary
{
    public const string CanvasLeft = "Canvas.Left";
    public const string CanvasTop = "Canvas.Top";

    /// <summary>Avalonia and WinUI: the canonical names themselves.</summary>
    public static readonly LayoutVocabulary Xaml = new();

    public static readonly LayoutVocabulary Maui = new()
    {
        Width = "WidthRequest",
        Height = "HeightRequest",
        HorizontalAlignment = "HorizontalOptions",
        VerticalAlignment = "VerticalOptions",
        HorizontalStart = "Start",
        VerticalStart = "Start",
        Stretch = "Fill",
        AbsoluteBounds = "AbsoluteLayout.LayoutBounds",
    };

    public string Width { get; init; } = "Width";

    public string Height { get; init; } = "Height";

    public string HorizontalAlignment { get; init; } = "HorizontalAlignment";

    public string VerticalAlignment { get; init; } = "VerticalAlignment";

    /// <summary>The alignment value for "at the left" (<c>Left</c>; MAUI <c>Start</c>).</summary>
    public string HorizontalStart { get; init; } = "Left";

    /// <summary>The alignment value for "at the top" (<c>Top</c>; MAUI <c>Start</c>).</summary>
    public string VerticalStart { get; init; } = "Top";

    /// <summary>The alignment value for "fill the space" (<c>Stretch</c>; MAUI <c>Fill</c>).</summary>
    public string Stretch { get; init; } = "Stretch";

    /// <summary>
    /// The one attribute holding an absolutely positioned child's <c>x,y,width,height</c> (MAUI's AbsoluteLayout), or null
    /// where <c>Canvas.Left</c>/<c>Canvas.Top</c> position it.
    /// </summary>
    public string? AbsoluteBounds { get; init; }

    /// <summary>Whether this is the canonical vocabulary (nothing to translate).</summary>
    public bool IsCanonical => this == Xaml;

    /// <summary>A canonical attribute's value on <paramref name="element"/>, translated back to canonical values.</summary>
    public string? Get(XElement element, XName canonical)
    {
        if (AbsoluteBounds is { } bounds && IsCanvasPosition(canonical))
        {
            var current = (string?)element.Attribute(bounds);
            return canonical.LocalName switch
            {
                CanvasLeft => BoundsPart(current, 0),
                CanvasTop => BoundsPart(current, 1),
                _ => null, // Canvas.Right/Bottom: AbsoluteLayout has no such thing
            };
        }

        if (AbsoluteBounds is { } sized && canonical.LocalName is "Width" or "Height"
            && ExplicitSize((string?)element.Attribute(sized), canonical.LocalName == "Width" ? 2 : 3) is { } size)
        {
            return size;
        }

        var value = (string?)element.Attribute(NameOf(canonical));
        return value is null ? null : ToCanonicalValue(canonical, value);
    }

    /// <summary>The document's attribute name for a canonical one (Canvas positions map to <see cref="AbsoluteBounds"/>).</summary>
    public XName NameOf(XName canonical) => canonical.LocalName switch
    {
        _ when canonical.Namespace != XNamespace.None => canonical,
        "Width" => Width,
        "Height" => Height,
        "HorizontalAlignment" => HorizontalAlignment,
        "VerticalAlignment" => VerticalAlignment,
        CanvasLeft or CanvasTop when AbsoluteBounds is { } bounds => bounds,
        _ => canonical,
    };

    /// <summary>
    /// The attribute writes that give <paramref name="element"/> the canonical <paramref name="changes"/> (null removes),
    /// in order, one per document attribute: Canvas Left and Top become one LayoutBounds keeping its size part.
    /// Writes that wouldn't change anything are left out.
    /// </summary>
    public List<(XName Name, string? Value)> Translate(XElement element, IEnumerable<KeyValuePair<XName, string?>> changes)
    {
        var result = new List<(XName Name, string? Value)>();
        void Put(XName name, string? value)
        {
            var index = result.FindIndex(r => r.Name == name);
            if (index >= 0)
            {
                result[index] = (name, value);
            }
            else
            {
                result.Add((name, value));
            }
        }

        // MAUI's AbsoluteLayout: x, y and (when the bounds give one) the size are parts of one attribute.
        var current = AbsoluteBounds is { } b ? (string?)element.Attribute(b) : null;
        var parts = new[] { BoundsPart(current, 0), BoundsPart(current, 1), BoundsPart(current, 2), BoundsPart(current, 3) };
        var boundsChanged = false;
        foreach (var (canonical, value) in changes)
        {
            if (AbsoluteBounds is not null && IsCanvasPosition(canonical))
            {
                if (canonical.LocalName is CanvasLeft or CanvasTop)
                {
                    parts[canonical.LocalName == CanvasLeft ? 0 : 1] = value;
                    boundsChanged = true;
                }

                // Canvas.Right/Bottom have no AbsoluteLayout equivalent; reserve the attribute's place in the order.
                Put(AbsoluteBounds, current);
                continue;
            }

            if (AbsoluteBounds is not null && canonical.LocalName is "Width" or "Height"
                && ExplicitSize(current, canonical.LocalName == "Width" ? 2 : 3) is not null && value is not null)
            {
                parts[canonical.LocalName == "Width" ? 2 : 3] = value; // the bounds' size wins over WidthRequest
                boundsChanged = true;
                Put(AbsoluteBounds, current);
                continue;
            }

            Put(NameOf(canonical), value is null ? null : ToDocumentValue(canonical, value));
        }

        if (boundsChanged && AbsoluteBounds is { } bounds && result.FindIndex(r => r.Name == bounds) is var at and >= 0)
        {
            result[at] = (bounds, parts[0] is null && parts[1] is null
                ? null
                : $"{parts[0] ?? "0"},{parts[1] ?? "0"},{parts[2] ?? "-1"},{parts[3] ?? "-1"}");
        }

        return result.Where(r => (string?)element.Attribute(r.Name) != r.Value).ToList();
    }

    /// <summary>Applies canonical attributes to a new element (no undo: it isn't in the document yet).</summary>
    public void Apply(XElement element, IEnumerable<KeyValuePair<XName, string?>> changes)
    {
        foreach (var (name, value) in Translate(element, changes))
        {
            element.SetAttributeValue(name, value);
        }
    }

    private static bool IsCanvasPosition(XName name) => name.Namespace == XNamespace.None && name.LocalName.StartsWith("Canvas.", StringComparison.Ordinal);

    private string ToDocumentValue(XName canonical, string value) => (canonical.LocalName, value) switch
    {
        ("HorizontalAlignment", "Left") => HorizontalStart,
        ("VerticalAlignment", "Top") => VerticalStart,
        ("HorizontalAlignment" or "VerticalAlignment", "Stretch") => Stretch,
        _ => value,
    };

    private string ToCanonicalValue(XName canonical, string value) => canonical.LocalName switch
    {
        "HorizontalAlignment" when value == HorizontalStart => "Left",
        "VerticalAlignment" when value == VerticalStart => "Top",
        "HorizontalAlignment" or "VerticalAlignment" when value == Stretch => "Stretch",
        _ => value,
    };

    /// <summary>The width or height part of <c>x,y,w,h</c> when it sets a size (not <c>-1</c>, AutoSize).</summary>
    private static string? ExplicitSize(string? bounds, int index) =>
        BoundsPart(bounds, index) is { } part && double.Parse(part, CultureInfo.InvariantCulture) >= 0 ? part : null;

    /// <summary>One number of <c>x,y,w,h</c>, or null when absent or not a number.</summary>
    private static string? BoundsPart(string? bounds, int index)
    {
        var parts = bounds?.Split(',', StringSplitOptions.TrimEntries);
        return parts is not null && index < parts.Length
               && double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            ? parts[index]
            : null;
    }
}
