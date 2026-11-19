using XDesigner.App.Services;
using XDesigner.Core.Editing;
using XDesigner.Core.Properties;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace XDesigner.App.Controls;

/// <summary>
/// The Grid rows/columns editor (§9.5): one line per row or column with its size (Auto, a share of the space "*",
/// or pixels) and optional minimum/maximum; add, remove and reorder. OK applies both lists as one undo step.
/// </summary>
public static class GridDefinitionsEditor
{
    private sealed class Line
    {
        public GridUnit Unit { get; set; }

        public string Value { get; set; } = "1";

        public string Min { get; set; } = "";

        public string Max { get; set; } = "";

        public int? Original { get; init; }
    }

    /// <summary>Shows the editor; returns (rows, columns), or null when cancelled.</summary>
    public static async Task<(IReadOnlyList<GridDefinition> Rows, IReadOnlyList<GridDefinition> Columns)?> ShowAsync(
        XamlRoot root, string gridName, IReadOnlyList<GridDefinition> rows, IReadOnlyList<GridDefinition> columns)
    {
        var rowLines = rows.Select(ToLine).ToList();
        var columnLines = columns.Select(ToLine).ToList();
        var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["NotifyErrorIcon"] };

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"Rows and columns of {gridName}",
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 720d; // the default 548 cuts off the last button column

        var rowsPanel = new StackPanel { Spacing = 4 };
        var columnsPanel = new StackPanel { Spacing = 4 };
        void Validate()
        {
            var error = rowLines.Concat(columnLines).Select(Problem).FirstOrDefault(p => p is not null);
            problem.Text = error ?? "";
            dialog.IsPrimaryButtonEnabled = error is null;
        }

        void Build(StackPanel panel, List<Line> lines, string noun)
        {
            panel.Children.Clear();
            panel.Children.Add(HeaderRow(noun));
            for (var i = 0; i < lines.Count; i++)
            {
                panel.Children.Add(LineRow(lines, i, noun, () => Build(panel, lines, noun), Validate));
            }

            var add = new Button { Content = $"Add {noun.ToLowerInvariant()}", Margin = new Thickness(0, 4, 0, 0) };
            add.Click += (_, _) =>
            {
                lines.Add(new Line { Unit = GridUnit.Star });
                Build(panel, lines, noun);
            };
            panel.Children.Add(add);
            Validate();
        }

        Build(rowsPanel, rowLines, "Row");
        Build(columnsPanel, columnLines, "Column");

        dialog.Content = new ScrollViewer
        {
            MaxHeight = 520,
            Content = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Auto sizes to the content, * shares the remaining space (2* gets twice as much), a number is a fixed size. Controls in a removed row or column move to the one before it.",
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 520,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    },
                    new TextBlock { Text = "Rows", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
                    rowsPanel,
                    new TextBlock { Text = "Columns", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
                    columnsPanel,
                    problem,
                },
            },
        };

        if (await dialog.ShowOnceAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        return (rowLines.Select(ToDefinition).ToList(), columnLines.Select(ToDefinition).ToList());
    }

    private static Grid HeaderRow(string noun)
    {
        var grid = Columns();
        string[] labels = ["", "Size", "Value", "Min", "Max"];
        for (var i = 0; i < labels.Length; i++)
        {
            var label = new TextBlock { Text = labels[i], Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
            Grid.SetColumn(label, i);
            grid.Children.Add(label);
        }

        return grid;
    }

    private static Grid LineRow(List<Line> lines, int index, string noun, Action rebuild, Action validate)
    {
        var line = lines[index];
        var grid = Columns();
        var number = new TextBlock { Text = $"{noun} {index}", VerticalAlignment = VerticalAlignment.Center };
        var unit = new ComboBox { ItemsSource = new[] { "Auto", "* (share)", "Pixels" }, SelectedIndex = (int)line.Unit, Width = 110 };
        var value = new TextBox { Text = line.Value, Width = 70, IsEnabled = line.Unit != GridUnit.Auto };
        var min = new TextBox { Text = line.Min, Width = 70, PlaceholderText = "—" };
        var max = new TextBox { Text = line.Max, Width = 70, PlaceholderText = "—" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(unit, $"{noun} {index} size");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(value, $"{noun} {index} value");

        unit.SelectionChanged += (_, _) =>
        {
            line.Unit = (GridUnit)Math.Max(0, unit.SelectedIndex);
            value.IsEnabled = line.Unit != GridUnit.Auto;
            if (line.Unit == GridUnit.Pixel && line.Value == "1")
            {
                value.Text = "100";
            }

            validate();
        };
        value.TextChanged += (_, _) => { line.Value = value.Text; validate(); };
        min.TextChanged += (_, _) => { line.Min = min.Text; validate(); };
        max.TextChanged += (_, _) => { line.Max = max.Text; validate(); };

        var up = Small("", "Move up", index > 0, () => { (lines[index - 1], lines[index]) = (lines[index], lines[index - 1]); rebuild(); });
        var down = Small("", "Move down", index < lines.Count - 1, () => { (lines[index + 1], lines[index]) = (lines[index], lines[index + 1]); rebuild(); });
        var remove = Small("", $"Remove {noun.ToLowerInvariant()}", true, () => { lines.RemoveAt(index); rebuild(); });

        UIElement[] cells = [number, unit, value, min, max, up, down, remove];
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn((FrameworkElement)cells[i], i);
            grid.Children.Add(cells[i]);
        }

        return grid;
    }

    private static Grid Columns()
    {
        var grid = new Grid { ColumnSpacing = 6 };
        foreach (var width in new[] { 80d, 110, 70, 70, 70, 34, 34, 34 })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        }

        return grid;
    }

    private static Button Small(string glyph, string tip, bool enabled, Action click)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 11 }, Padding = new Thickness(6), IsEnabled = enabled };
        ToolTipService.SetToolTip(button, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private static Line ToLine(GridDefinition d) => new()
    {
        Unit = d.Unit,
        Value = d.Unit == GridUnit.Auto ? "1" : Protocol.Metadata.XamlValueText.Double(d.Value),
        Min = d.Min is { } min ? Protocol.Metadata.XamlValueText.Double(min) : "",
        Max = d.Max is { } max ? Protocol.Metadata.XamlValueText.Double(max) : "",
        Original = d.OriginalIndex,
    };

    private static string? Problem(Line line)
    {
        if (line.Unit != GridUnit.Auto && (!XamlValueSerializer.TryParseDouble(line.Value, out var v) || v <= 0))
        {
            return "Sizes must be numbers greater than 0.";
        }

        foreach (var bound in new[] { line.Min, line.Max })
        {
            if (bound.Trim().Length > 0 && (!XamlValueSerializer.TryParseDouble(bound, out var b) || b < 0))
            {
                return "Min and Max must be empty or numbers (0 or more).";
            }
        }

        return null;
    }

    private static GridDefinition ToDefinition(Line line)
    {
        // OK is only enabled once every line passed Problem(), so the numbers parse here.
        static double? Optional(string text) => XamlValueSerializer.TryParseDouble(text, out var v) ? v : null;
        var value = line.Unit == GridUnit.Auto ? 1 : Optional(line.Value) ?? 1;
        return new GridDefinition(line.Unit, value, Optional(line.Min), Optional(line.Max), line.Original);
    }
}
