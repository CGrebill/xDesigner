using System.Globalization;
using System.Xml.Linq;
using XDesigner.Core.Properties;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;

namespace XDesigner.Core.Editing;

public enum GridUnit
{
    Auto,
    Star,
    Pixel,
}

/// <summary>One row or column of a Grid (§9.5).</summary>
/// <param name="Value">Star weight or pixel size (ignored for Auto).</param>
/// <param name="OriginalIndex">Its index before editing, or null for a new one (children follow their row/column).</param>
public sealed record GridDefinition(GridUnit Unit, double Value = 1, double? Min = null, double? Max = null, int? OriginalIndex = null)
{
    public string LengthText => Unit switch
    {
        GridUnit.Auto => "Auto",
        GridUnit.Star => Value == 1 ? "*" : XamlValueText.Double(Value) + "*",
        _ => XamlValueText.Double(Value),
    };

    public static bool TryParse(string text, out GridUnit unit, out double value)
    {
        text = text.Trim();
        unit = GridUnit.Pixel;
        value = 1;
        if (text.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            unit = GridUnit.Auto;
            return true;
        }

        if (text.EndsWith('*'))
        {
            unit = GridUnit.Star;
            return text.Length == 1 || (XamlValueSerializer.TryParseDouble(text[..^1], out value) && value > 0);
        }

        return XamlValueSerializer.TryParseDouble(text, out value) && value >= 0;
    }
}

/// <summary>Reads and writes a Grid's <c>RowDefinitions</c>/<c>ColumnDefinitions</c> (compact attribute or property elements).</summary>
public static class GridDefinitions
{
    /// <summary>
    /// The grid's rows (<paramref name="rows"/>) or columns as written; null when they can't be edited here (a binding,
    /// or definitions the designer doesn't understand).
    /// </summary>
    public static IReadOnlyList<GridDefinition>? Read(XElement grid, bool rows)
    {
        var axis = rows ? "Row" : "Column";
        var size = rows ? "Height" : "Width";
        if (grid.Attribute($"{axis}Definitions") is { } attribute)
        {
            if (XamlValueSerializer.IsMarkupExtension(attribute.Value))
            {
                return null;
            }

            var list = new List<GridDefinition>();
            foreach (var part in attribute.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!GridDefinition.TryParse(part, out var unit, out var value))
                {
                    return null;
                }

                list.Add(new GridDefinition(unit, value, OriginalIndex: list.Count));
            }

            return list;
        }

        var element = PropertyElement(grid, rows);
        if (element is null)
        {
            return [];
        }

        var result = new List<GridDefinition>();
        foreach (var definition in element.Elements())
        {
            if (definition.Name.LocalName != $"{axis}Definition")
            {
                return null;
            }

            var length = (string?)definition.Attribute(size) ?? "*";
            if (!GridDefinition.TryParse(length, out var unit, out var value))
            {
                return null;
            }

            result.Add(new GridDefinition(unit, value, Number(definition, $"Min{size}"), Number(definition, $"Max{size}"), result.Count));
        }

        return result;
    }

    internal static XElement? PropertyElement(XElement grid, bool rows) =>
        grid.Elements().FirstOrDefault(e => e.Name.LocalName == $"{grid.Name.LocalName}.{(rows ? "Row" : "Column")}Definitions");

    /// <summary>Where a child in row/column <paramref name="old"/> goes: its row's new index, else the nearest earlier surviving one.</summary>
    internal static int MapIndex(IReadOnlyList<GridDefinition> edited, int old)
    {
        for (var i = 0; i < edited.Count; i++)
        {
            if (edited[i].OriginalIndex == old)
            {
                return i;
            }
        }

        var earlier = edited.Select((d, i) => (d.OriginalIndex, i)).Where(x => x.OriginalIndex < old).Select(x => x.i).DefaultIfEmpty(0).Max();
        return earlier;
    }

    private static double? Number(XElement element, string attribute) =>
        (string?)element.Attribute(attribute) is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
}

public sealed partial class DesignerActions
{
    /// <summary>
    /// Replaces a Grid's rows and columns (§9.5) as one undo step. Written as the compact attribute
    /// (<c>RowDefinitions="Auto,*,40"</c>) unless a Min/Max is used, then as <c>&lt;Grid.RowDefinitions&gt;</c> elements.
    /// Children keep their row/column; children of a deleted one move to the nearest earlier one, and spans are clamped.
    /// </summary>
    public bool SetGridDefinitions(int gridId, IReadOnlyList<GridDefinition> rows, IReadOnlyList<GridDefinition> columns)
    {
        if (_document.FindById(gridId) is not { } grid)
        {
            return false;
        }

        var commands = new List<IUndoableCommand>();
        foreach (var (isRows, edited) in new[] { (true, rows), (false, columns) })
        {
            var current = GridDefinitions.Read(grid.Element, isRows);
            if (current is not null && current.Select(d => (d.Unit, d.Value, d.Min, d.Max)).SequenceEqual(edited.Select(d => (d.Unit, d.Value, d.Min, d.Max)))
                && edited.Select(d => d.OriginalIndex).SequenceEqual(Enumerable.Range(0, edited.Count).Select(i => (int?)i)))
            {
                continue; // this axis is unchanged
            }

            commands.AddRange(WriteDefinitions(grid, isRows, edited));
            commands.AddRange(RemapChildren(grid, isRows, edited));
        }

        return Execute($"Edit rows and columns of {grid.DisplayName}", commands);
    }

    private static List<IUndoableCommand> WriteDefinitions(ElementNode grid, bool rows, IReadOnlyList<GridDefinition> edited)
    {
        var axis = rows ? "Row" : "Column";
        var size = rows ? "Height" : "Width";
        var commands = new List<IUndoableCommand>();
        var attribute = $"{axis}Definitions";
        var propertyElement = GridDefinitions.PropertyElement(grid.Element, rows);
        if (propertyElement is not null)
        {
            commands.Add(new RemoveElementCommand(grid.Id, propertyElement.Name, $"Edit {axis.ToLowerInvariant()}s"));
        }

        if (edited.Any(d => d.Min is not null || d.Max is not null))
        {
            if (grid.Element.Attribute(attribute) is not null)
            {
                commands.Add(new SetAttributeCommand(grid.Id, attribute, null));
            }

            var ns = grid.Element.Name.Namespace;
            var holder = new XElement(ns + $"{grid.TypeName}.{attribute}");
            commands.Add(new InsertElementCommand(grid.Id, holder, 0, $"Edit {axis.ToLowerInvariant()}s"));
            foreach (var d in edited)
            {
                var definition = new XElement(ns + $"{axis}Definition", new XAttribute(size, d.LengthText));
                if (d.Min is { } min)
                {
                    definition.SetAttributeValue($"Min{size}", XamlValueText.Double(min));
                }

                if (d.Max is { } max)
                {
                    definition.SetAttributeValue($"Max{size}", XamlValueText.Double(max));
                }

                commands.Add(new InsertElementCommand(grid.Id, definition, int.MaxValue, $"Edit {axis.ToLowerInvariant()}s", holder.Name));
            }
        }
        else
        {
            var compact = edited.Count == 0 ? null : string.Join(',', edited.Select(d => d.LengthText));
            if ((string?)grid.Element.Attribute(attribute) != compact)
            {
                commands.Add(new SetAttributeCommand(grid.Id, attribute, compact));
            }
        }

        return commands;
    }

    private static List<IUndoableCommand> RemapChildren(ElementNode grid, bool rows, IReadOnlyList<GridDefinition> edited)
    {
        var axis = rows ? "Grid.Row" : "Grid.Column";
        var span = rows ? "Grid.RowSpan" : "Grid.ColumnSpan";
        var count = Math.Max(1, edited.Count);
        var commands = new List<IUndoableCommand>();
        foreach (var child in grid.Children)
        {
            var old = int.TryParse((string?)child.Element.Attribute(axis), NumberStyles.Integer, CultureInfo.InvariantCulture, out var o) ? o : 0;
            var index = edited.Count == 0 ? 0 : GridDefinitions.MapIndex(edited, old);
            // Unchanged indexes are left as written; a control moving to the first row/column loses the attribute.
            var text = index == old ? (string?)child.Element.Attribute(axis) : index == 0 ? null : index.ToString(CultureInfo.InvariantCulture);
            if ((string?)child.Element.Attribute(axis) != text)
            {
                commands.Add(new SetAttributeCommand(child.Id, axis, text));
            }

            if (int.TryParse((string?)child.Element.Attribute(span), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && index + s > count)
            {
                var clamped = Math.Max(1, count - index);
                commands.Add(new SetAttributeCommand(child.Id, span, clamped == 1 ? null : clamped.ToString(CultureInfo.InvariantCulture)));
            }
        }

        return commands;
    }
}
