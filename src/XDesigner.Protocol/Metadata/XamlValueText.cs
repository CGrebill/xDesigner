using System.Globalization;

namespace XDesigner.Protocol.Metadata;

/// <summary>
/// How both processes write values as XAML attribute text (§5.6), so the render host's effective values and
/// the designer's edits look the same: invariant culture, shortest round-tripping numbers, collapsed thicknesses.
/// </summary>
public static class XamlValueText
{
    public static string Double(double value) => value switch
    {
        double.NaN => "NaN",
        double.PositiveInfinity => "Infinity",
        double.NegativeInfinity => "-Infinity",
        _ => Math.Round(value, 6).ToString("R", CultureInfo.InvariantCulture),
    };

    /// <summary><c>"8"</c> when uniform, <c>"8,4"</c> when left=right and top=bottom, else <c>"l,t,r,b"</c>.</summary>
    public static string Thickness(double left, double top, double right, double bottom)
    {
        if (left == top && top == right && right == bottom)
        {
            return Double(left);
        }

        return left == right && top == bottom
            ? $"{Double(left)},{Double(top)}"
            : $"{Double(left)},{Double(top)},{Double(right)},{Double(bottom)}";
    }

    /// <summary>
    /// Corner radius: the four corners in the framework's order (Avalonia and WinUI top-left, top-right, bottom-right,
    /// bottom-left; MAUI top-left, top-right, bottom-left, bottom-right), collapsed to one number when uniform.
    /// </summary>
    public static string CornerRadius(double first, double second, double third, double fourth) =>
        first == second && second == third && third == fourth
            ? Double(first)
            : $"{Double(first)},{Double(second)},{Double(third)},{Double(fourth)}";

    /// <summary><c>#RRGGBB</c> when opaque, otherwise <c>#AARRGGBB</c>.</summary>
    public static string Color(byte a, byte r, byte g, byte b) =>
        a == 255 ? $"#{r:X2}{g:X2}{b:X2}" : $"#{a:X2}{r:X2}{g:X2}{b:X2}";
}
