using System.Globalization;
using XDesigner.Protocol.Metadata;

namespace XDesigner.Core.Properties;

/// <summary>
/// Typed values ⇄ XAML attribute text (§5.6). The properties window validates and normalizes what the user
/// types through <see cref="TryNormalize"/>, so nothing unparseable is ever written to the file.
/// </summary>
public static class XamlValueSerializer
{
    private static readonly char[] Separators = [',', ' '];

    /// <summary>Named colors XAML accepts for colors and brushes (Avalonia's <c>KnownColor</c> names).</summary>
    public static readonly IReadOnlySet<string> ColorNames = new HashSet<string>(
        ("AliceBlue AntiqueWhite Aqua Aquamarine Azure Beige Bisque Black BlanchedAlmond Blue BlueViolet Brown BurlyWood " +
         "CadetBlue Chartreuse Chocolate Coral CornflowerBlue Cornsilk Crimson Cyan DarkBlue DarkCyan DarkGoldenrod DarkGray " +
         "DarkGreen DarkKhaki DarkMagenta DarkOliveGreen DarkOrange DarkOrchid DarkRed DarkSalmon DarkSeaGreen DarkSlateBlue " +
         "DarkSlateGray DarkTurquoise DarkViolet DeepPink DeepSkyBlue DimGray DodgerBlue Firebrick FloralWhite ForestGreen " +
         "Fuchsia Gainsboro GhostWhite Gold Goldenrod Gray Green GreenYellow Honeydew HotPink IndianRed Indigo Ivory Khaki " +
         "Lavender LavenderBlush LawnGreen LemonChiffon LightBlue LightCoral LightCyan LightGoldenrodYellow LightGray LightGreen " +
         "LightPink LightSalmon LightSeaGreen LightSkyBlue LightSlateGray LightSteelBlue LightYellow Lime LimeGreen Linen " +
         "Magenta Maroon MediumAquamarine MediumBlue MediumOrchid MediumPurple MediumSeaGreen MediumSlateBlue MediumSpringGreen " +
         "MediumTurquoise MediumVioletRed MidnightBlue MintCream MistyRose Moccasin NavajoWhite Navy OldLace Olive OliveDrab " +
         "Orange OrangeRed Orchid PaleGoldenrod PaleGreen PaleTurquoise PaleVioletRed PapayaWhip PeachPuff Peru Pink Plum " +
         "PowderBlue Purple Red RosyBrown RoyalBlue SaddleBrown Salmon SandyBrown SeaGreen SeaShell Sienna Silver SkyBlue " +
         "SlateBlue SlateGray Snow SpringGreen SteelBlue Tan Teal Thistle Tomato Transparent Turquoise Violet Wheat White " +
         "WhiteSmoke Yellow YellowGreen").Split(' '),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>A short palette for the color editor (name, opaque RGB).</summary>
    public static readonly IReadOnlyList<(string Name, uint Rgb)> Palette =
    [
        ("Transparent", 0x000000), ("Black", 0x000000), ("White", 0xFFFFFF), ("Gray", 0x808080), ("LightGray", 0xD3D3D3),
        ("DarkGray", 0xA9A9A9), ("Red", 0xFF0000), ("Crimson", 0xDC143C), ("Orange", 0xFFA500), ("Gold", 0xFFD700),
        ("Yellow", 0xFFFF00), ("LimeGreen", 0x32CD32), ("Green", 0x008000), ("Teal", 0x008080), ("SkyBlue", 0x87CEEB),
        ("DodgerBlue", 0x1E90FF), ("Blue", 0x0000FF), ("Navy", 0x000080), ("Purple", 0x800080), ("HotPink", 0xFF69B4),
        ("Brown", 0xA52A2A), ("Beige", 0xF5F5DC), ("WhiteSmoke", 0xF5F5F5), ("SlateGray", 0x708090),
    ];

    /// <summary><c>{Binding …}</c>, <c>{StaticResource …}</c>: not a plain value (D7). <c>{}</c> escapes a literal brace.</summary>
    public static bool IsMarkupExtension(string attributeValue) =>
        attributeValue.StartsWith('{') && !attributeValue.StartsWith("{}", StringComparison.Ordinal);

    /// <summary>Attribute text → the value the user sees (drops the <c>{}</c> escape).</summary>
    public static string FromAttribute(string attributeValue) =>
        attributeValue.StartsWith("{}", StringComparison.Ordinal) ? attributeValue[2..] : attributeValue;

    /// <summary>User text → attribute text (a leading brace would otherwise start a markup extension).</summary>
    public static string ToAttribute(string value) => value.StartsWith('{') ? "{}" + value : value;

    /// <summary>
    /// Validates user input for a property and returns the text to write: <c>null</c> means "reset" (for
    /// non-text kinds an empty box clears the property). False with a message when the input can't be used.
    /// </summary>
    public static bool TryNormalize(ValueKind kind, string input, string[]? enumValues, out string? normalized, out string? problem)
    {
        problem = null;
        normalized = null;
        var text = input.Trim();

        if (kind is ValueKind.String or ValueKind.Object or ValueKind.ImageSource)
        {
            normalized = input; // text keeps its spaces; empty is a real (empty) value
            return true;
        }

        if (text.Length == 0)
        {
            return true; // reset
        }

        switch (kind)
        {
            case ValueKind.Bool:
                if (bool.TryParse(text, out var b))
                {
                    normalized = b ? "True" : "False";
                    return true;
                }

                problem = "Use True or False.";
                return false;

            case ValueKind.Int:
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                    || int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out i))
                {
                    normalized = i.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                problem = "Enter a whole number.";
                return false;

            case ValueKind.Double:
                if (text.Equals("Auto", StringComparison.OrdinalIgnoreCase) || text.Equals("NaN", StringComparison.OrdinalIgnoreCase))
                {
                    return true; // Auto = not set
                }

                if (TryParseDouble(text, out var d))
                {
                    normalized = XamlValueText.Double(d);
                    return true;
                }

                problem = "Enter a number.";
                return false;

            case ValueKind.Enum:
                var match = enumValues?.FirstOrDefault(v => v.Equals(text, StringComparison.OrdinalIgnoreCase));
                if (match is not null || enumValues is null)
                {
                    normalized = match ?? text;
                    return true;
                }

                problem = $"Choose one of: {string.Join(", ", enumValues)}.";
                return false;

            case ValueKind.Thickness:
                if (TryParseNumbers(text, out var t) && t.Length is 1 or 2 or 4)
                {
                    normalized = t.Length switch
                    {
                        1 => XamlValueText.Thickness(t[0], t[0], t[0], t[0]),
                        2 => XamlValueText.Thickness(t[0], t[1], t[0], t[1]),
                        _ => XamlValueText.Thickness(t[0], t[1], t[2], t[3]),
                    };
                    return true;
                }

                problem = "Enter one number (all sides), two (left/right, top/bottom) or four (left, top, right, bottom).";
                return false;

            case ValueKind.CornerRadius:
                if (TryParseNumbers(text, out var c) && c.Length is 1 or 4)
                {
                    normalized = c.Length == 1 ? XamlValueText.CornerRadius(c[0], c[0], c[0], c[0]) : XamlValueText.CornerRadius(c[0], c[1], c[2], c[3]);
                    return true;
                }

                problem = "Enter one number (all corners) or four, one per corner (the … button shows their order).";
                return false;

            case ValueKind.Color:
            case ValueKind.Brush:
                if (TryParseColor(text, out _, out var canonical))
                {
                    normalized = canonical;
                    return true;
                }

                problem = "Enter a color name (Red, CornflowerBlue) or #RRGGBB / #AARRGGBB.";
                return false;

            case ValueKind.GridLength:
                if (text.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                {
                    normalized = "Auto";
                    return true;
                }

                if (text == "*")
                {
                    normalized = "*";
                    return true;
                }

                if (text.EndsWith('*') && TryParseDouble(text[..^1], out var star) && star > 0)
                {
                    normalized = XamlValueText.Double(star) + "*";
                    return true;
                }

                if (TryParseDouble(text, out var px) && px >= 0)
                {
                    normalized = XamlValueText.Double(px);
                    return true;
                }

                problem = "Enter Auto, *, 2* or a number that isn't negative.";
                return false;

            case ValueKind.FontFamily:
                normalized = text;
                return true;

            default:
                problem = "This property can't be edited here.";
                return false;
        }
    }

    /// <summary>Invariant first (what the file uses), then the user's own culture.</summary>
    public static bool TryParseDouble(string text, out double value) =>
        (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
         || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        && !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>Numbers separated by commas and/or spaces (Thickness, CornerRadius).</summary>
    public static bool TryParseNumbers(string text, out double[] values)
    {
        var parts = text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                || double.IsNaN(values[i]) || double.IsInfinity(values[i]))
            {
                return false;
            }
        }

        return parts.Length > 0;
    }

    /// <summary>
    /// Parses <c>#RGB</c>, <c>#ARGB</c>, <c>#RRGGBB</c>, <c>#AARRGGBB</c> or a known color name. <paramref name="argb"/> is
    /// only known for hex input (names resolve in the renderer); <paramref name="canonical"/> is the text to write.
    /// </summary>
    public static bool TryParseColor(string text, out uint? argb, out string canonical)
    {
        argb = null;
        canonical = text.Trim();
        if (canonical.StartsWith('#'))
        {
            var hex = canonical[1..];
            if (hex.Length is 3 or 4)
            {
                hex = string.Concat(hex.Select(ch => new string(ch, 2)));
            }

            if (hex.Length == 6)
            {
                hex = "FF" + hex;
            }

            if (hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                argb = value;
                canonical = XamlValueText.Color((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
                return true;
            }

            return false;
        }

        var input = canonical;
        if (ColorNames.Contains(input))
        {
            canonical = ColorNames.First(n => n.Equals(input, StringComparison.OrdinalIgnoreCase)); // the stored spelling
            return true;
        }

        return false;
    }

    /// <summary>Splits Thickness text into left, top, right, bottom (for the four-box editor).</summary>
    public static bool TryParseThickness(string text, out (double Left, double Top, double Right, double Bottom) value)
    {
        value = default;
        if (!TryParseNumbers(text, out var t))
        {
            return false;
        }

        value = t.Length switch
        {
            1 => (t[0], t[0], t[0], t[0]),
            2 => (t[0], t[1], t[0], t[1]),
            4 => (t[0], t[1], t[2], t[3]),
            _ => default,
        };
        return t.Length is 1 or 2 or 4;
    }
}
