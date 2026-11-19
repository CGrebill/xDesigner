using XDesigner.Protocol.Metadata;

namespace XDesigner.Core.Properties;

/// <summary>
/// Which category each property is shown in, in what order, and which are "advanced" (hidden unless asked
/// for) — the app-side map of §9.2. Anything not listed goes to <see cref="Misc"/>, ordered by name.
/// </summary>
public static class PropertyCategories
{
    public const string Common = "Common";
    public const string Layout = "Layout";
    public const string Appearance = "Appearance";
    public const string Text = "Text";
    public const string Behavior = "Behavior";
    public const string Window = "Window";
    public const string Misc = "Misc";
    public const string Other = "Other XAML";

    /// <summary>Display order of the categories.</summary>
    public static readonly string[] Order = [Common, Layout, Appearance, Text, Window, Behavior, Misc, Other];

    // Within a category, properties appear in the order listed here, then the rest alphabetically.
    private static readonly (string Category, string[] Keys)[] Map =
    [
        (Common, ["Content", "Text", "Header", "Title", "IsChecked", "Value", "Minimum", "Maximum", "SelectedIndex", "PlaceholderText",
            "Watermark", "Source", "IsEnabled", "IsVisible", "ToolTip.Tip", "Tag"]),
        (Layout, ["Width", "Height", "Grid.Row", "Grid.Column", "Grid.RowSpan", "Grid.ColumnSpan", "Canvas.Left", "Canvas.Top",
            "Canvas.Right", "Canvas.Bottom", "DockPanel.Dock", "Margin", "Padding", "HorizontalAlignment", "VerticalAlignment",
            "HorizontalContentAlignment", "VerticalContentAlignment", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight", "Orientation",
            "Spacing", "RowDefinitions", "ColumnDefinitions", "RowSpacing", "ColumnSpacing", "LastChildFill", "Rows", "Columns",
            "ItemWidth", "ItemHeight", "Stretch", "StretchDirection", "ZIndex", "ClipToBounds"]),
        (Appearance, ["Background", "Foreground", "BorderBrush", "BorderThickness", "CornerRadius", "Opacity", "Fill", "Stroke",
            "StrokeThickness", "BoxShadow", "BackgroundSizing", "CaretBrush", "SelectionBrush", "SelectionForegroundBrush",
            "PlaceholderForeground", "WatermarkForeground"]),
        (Text, ["FontFamily", "FontSize", "FontWeight", "FontStyle", "FontStretch", "TextAlignment", "TextWrapping", "TextTrimming",
            "LineHeight", "LetterSpacing", "MaxLines", "MinLines", "PasswordChar", "TextDecorations"]),
        (Window, ["Icon", "WindowStartupLocation", "SizeToContent", "CanResize", "CanMinimize", "CanMaximize", "WindowState",
            "SystemDecorations", "WindowDecorations", "Topmost", "ShowInTaskbar", "ShowActivated", "ExtendClientAreaToDecorationsHint",
            "ExtendClientAreaTitleBarHeightHint", "ClosingBehavior"]),
        (Behavior, ["IsReadOnly", "AcceptsReturn", "AcceptsTab", "MaxLength", "IsThreeState", "ClickMode", "IsDefault", "IsCancel",
            "HotKey", "SelectionMode", "IsHitTestVisible", "Focusable", "IsTabStop", "TabIndex", "Cursor", "IsUndoEnabled",
            "RevealPassword", "UseFloatingPlaceholder", "IsSnapToTickEnabled", "TickFrequency", "Increment"]),
    ];

    /// <summary>Template/style plumbing and things that only make sense with bindings: hidden unless "Show advanced" (§9.2).</summary>
    private static readonly HashSet<string> AdvancedKeys = new(StringComparer.Ordinal)
    {
        "Template", "Theme", "Styles", "Resources", "DataTemplates", "Classes", "Clip", "RenderTransform", "RenderTransformOrigin",
        "Transitions", "DataContext", "FlowDirection", "ContextMenu", "ContextFlyout", "Flyout", "FocusAdorner", "OpacityMask",
        "Effect", "CacheMode", "Clock", "ItemTemplate", "ItemsPanel", "ContentTemplate", "ItemsSource", "DisplayMemberBinding",
        "SelectedItem", "SelectedValue", "SelectedItems", "Selection", "Command", "CommandParameter", "UseLayoutRounding",
        "TransparencyLevelHint", "TransparencyBackgroundFallback", "RequestedThemeVariant", "ActualThemeVariant", "FontFeatures",
        "IsTemplateFocusTarget", "IsHoldingEnabled", "IsHoldWithMouseEnabled", "Grid.IsSharedSizeScope", "SystemBarColor",
        "AutoSafeAreaPadding", "WindowDecorationsTheme", "SelectionStart", "SelectionEnd", "SelectedText", "CaretIndex",
        "CaretBlinkInterval", "ClearSelectionOnLostFocus", "IsInactiveSelectionHighlightEnabled", "UndoLimit", "NewLine",
        "UseFloatingWatermark", "InnerLeftContent", "InnerRightContent",
    };

    private static readonly Dictionary<string, (string Category, int Order)> ByKey = Build();

    public static string CategoryOf(string key) =>
        ByKey.TryGetValue(key, out var entry) ? entry.Category
        : key.StartsWith("ToolTip.", StringComparison.Ordinal) ? Behavior
        : key.StartsWith("ScrollViewer.", StringComparison.Ordinal) ? Behavior
        : Misc;

    /// <summary>Sort position within the category (listed keys first).</summary>
    public static int OrderOf(string key) => ByKey.TryGetValue(key, out var entry) ? entry.Order : int.MaxValue;

    public static bool IsAdvanced(string key, ValueKind kind) =>
        kind == ValueKind.Unsupported
        || AdvancedKeys.Contains(key)
        || key.EndsWith("Template", StringComparison.Ordinal)
        || key.StartsWith("ScrollViewer.", StringComparison.Ordinal)
        || (key.StartsWith("ToolTip.", StringComparison.Ordinal) && key != "ToolTip.Tip");

    /// <summary>
    /// Whether an attached property applies to a child of <paramref name="parent"/>: <c>Grid.*</c> only inside a Grid,
    /// <c>Canvas.*</c> inside a Canvas, <c>DockPanel.Dock</c> inside a DockPanel. ToolTip/ScrollViewer apply anywhere.
    /// </summary>
    public static bool AppliesTo(PropertyInfoDto property, ControlTypeInfo? parent) => property.OwnerName switch
    {
        _ when !property.IsAttached => true,
        "Grid" => parent?.DerivesFrom("Avalonia.Controls.Grid") == true,
        "Canvas" => parent?.DerivesFrom("Avalonia.Controls.Canvas") == true,
        "DockPanel" => parent?.DerivesFrom("Avalonia.Controls.DockPanel") == true,
        _ => true,
    };

    private static Dictionary<string, (string, int)> Build()
    {
        var result = new Dictionary<string, (string, int)>(StringComparer.Ordinal);
        foreach (var (category, keys) in Map)
        {
            for (var i = 0; i < keys.Length; i++)
            {
                result.TryAdd(keys[i], (category, i));
            }
        }

        return result;
    }
}
