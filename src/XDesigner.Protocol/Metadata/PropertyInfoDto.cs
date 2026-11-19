using MessagePack;

namespace XDesigner.Protocol.Metadata;

/// <summary>Which editor the properties window uses for a property (§9.2).</summary>
public enum ValueKind
{
    /// <summary>Free text the XAML loader parses (strings, but also Point, TimeSpan, Geometry, RowDefinitions...).</summary>
    String,
    Bool,
    Int,
    Double,
    Enum,
    Thickness,
    CornerRadius,
    Brush,
    Color,
    FontFamily,
    GridLength,
    ImageSource,

    /// <summary><c>object</c>-typed (Content, Header, Tag, ToolTip.Tip): edited as text.</summary>
    Object,

    /// <summary>Templates, transforms, collections...: shown, never edited.</summary>
    Unsupported,
}

/// <summary>Where an effective value comes from (§14.4).</summary>
public enum ValueSource
{
    Default,
    Local,
    Style,
    Template,
    Inherited,
    Animation,
}

/// <summary>
/// One property a control type can set in XAML: its own and inherited Avalonia properties, plain settable
/// CLR properties of simple types, and attached properties of the common layout owners (Grid, Canvas,
/// DockPanel, ToolTip, ScrollViewer). Categories and the "advanced" flag are decided by the app (Core), not here.
/// </summary>
/// <param name="Name">Property name (<c>Background</c>, <c>Row</c>).</param>
/// <param name="OwnerName">Short name of the owner type; for attached properties the XAML prefix (<c>Grid</c>).</param>
/// <param name="ValueTypeName">Full name of the value type, for tooltips.</param>
/// <param name="IsNullable">The value may be null (reference type or <c>Nullable&lt;T&gt;</c>).</param>
/// <param name="EnumValues">Names of the enum members, for <see cref="ValueKind.Enum"/>.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record PropertyInfoDto(
    string Name,
    string OwnerName,
    string ValueTypeName,
    ValueKind Kind,
    bool IsAttached,
    bool IsNullable,
    string[]? EnumValues = null)
{
    /// <summary>The attribute name in XAML: <c>Background</c>, or <c>Grid.Row</c> for an attached property.</summary>
    [IgnoreMember]
    public string Key => IsAttached ? $"{OwnerName}.{Name}" : Name;
}

/// <summary>
/// The effective value of one property on a rendered element, written the way XAML would write it
/// (invariant culture, <see cref="XamlValueText"/>): <c>"8,4"</c>, <c>"#FF0078D4"</c>, <c>"NaN"</c> for Auto.
/// Values XAML text can't express (a control as content, a gradient) are a description in parentheses.
/// </summary>
/// <param name="Key">Same as <see cref="PropertyInfoDto.Key"/>.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record PropertyValueDto(string Key, string? Value, ValueSource Source);
