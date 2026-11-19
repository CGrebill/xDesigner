using XDesigner.Core.Properties;
using XDesigner.Protocol.Metadata;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace XDesigner.App.Controls;

/// <summary>One row: label (bold when set in the XAML), editor, and Reset (§9.2). Reused while the same property stays on screen.</summary>
internal sealed class PropertyRowView
{
    private readonly PropertyEditorContext _context;
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _reset;
    private IReadOnlyList<int> _targets = [];

    public PropertyRowView(PropertyRow row, PropertyEditorContext context)
    {
        _context = context;
        Key = row.Key;
        EditorKind = EditorKindOf(row);
        Editor = EditorKind switch
        {
            "bool" => new BoolPropertyEditor(context, row),
            "enum" => new EnumPropertyEditor(context, row),
            "color" => new ColorPropertyEditor(context, row),
            "readonly" => new ReadOnlyPropertyEditor(context),
            _ => (PropertyEditor)new TextPropertyEditor(context, row),
        };

        _reset = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 11 },
            Padding = new Thickness(4),
            MinWidth = 0,
            Width = 26,
            Height = 26,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _reset.Click += (_, _) => _context.ReportProblem(_context.Commit(_targets, Key, null));

        View = new Grid { ColumnSpacing = 6, Padding = new Thickness(12, 2, 6, 2) };
        View.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.42, GridUnitType.Star) });
        View.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.58, GridUnitType.Star) });
        View.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        Grid.SetColumn(Editor.View, 1);
        Grid.SetColumn(_reset, 2);
        View.Children.Add(_label);
        View.Children.Add(Editor.View);
        View.Children.Add(_reset);
    }

    public string Key { get; }

    /// <summary>Rows are reused only while they need the same kind of editor.</summary>
    public string EditorKind { get; }

    public PropertyEditor Editor { get; }

    public Grid View { get; }

    public PropertyRow Row => Editor.Row;

    public static string EditorKindOf(PropertyRow row) => row.IsReadOnly ? "readonly" : row.Kind switch
    {
        ValueKind.Bool => "bool",
        ValueKind.Enum when row.EnumValues is { Length: > 0 } => "enum",
        ValueKind.Brush or ValueKind.Color => "color",
        _ => "text",
    };

    public void Update(PropertyRow row, IReadOnlyList<int> targets)
    {
        _targets = targets;
        _label.Text = row.Key;
        _label.FontWeight = row.IsSet ? FontWeights.SemiBold : FontWeights.Normal;
        ToolTipService.SetToolTip(_label, Describe(row));
        _reset.Visibility = row.IsSet ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(_reset, $"Reset {row.Key} (remove it from the XAML)");
        Editor.Show(row, targets);
    }

    private static string Describe(PropertyRow row)
    {
        var type = row.Info?.ValueTypeName is { } t ? $" ({t})" : "";
        var source = row.Form switch
        {
            PropertyValueForm.NotSet => row.EffectiveSource switch
            {
                ValueSource.Style => "Not set here: the value comes from a style or theme.",
                ValueSource.Inherited => "Not set here: the value is inherited from a parent.",
                ValueSource.Template => "Not set here: the value comes from the control's template.",
                ValueSource.Local => "Set by the control itself.",
                _ => "Not set here: this is the default value.",
            },
            PropertyValueForm.TextContent => "Set as text inside the element.",
            PropertyValueForm.Markup => "Set with a binding or resource (kept as written).",
            PropertyValueForm.PropertyElement => "Set in the XAML in a form the designer doesn't edit.",
            PropertyValueForm.ChildElements => "The content is another control.",
            _ => "Set in the XAML.",
        };
        return $"{row.Key}{type}\n{source}";
    }
}
