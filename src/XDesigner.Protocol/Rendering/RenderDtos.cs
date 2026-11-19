using MessagePack;

namespace XDesigner.Protocol.Rendering;

// Render request/response contracts. See docs/ARCHITECTURE.md §4.
// All DTOs use property-name keys so adding a member never shifts existing ones.

/// <summary>Theme variant to preview with.</summary>
public enum ThemeVariantKind
{
    Default,
    Light,
    Dark,
}

/// <summary>What kind of element the document root turned out to be.</summary>
public enum RootKind
{
    Window,
    UserControl,
    Other,
}

/// <summary>Layout panel kinds the designer has placement rules for (§7.1).</summary>
public enum PanelKind
{
    Other,
    Canvas,
    Grid,
    StackPanel,
    WrapPanel,
    DockPanel,
    UniformGrid,
}

/// <summary>Orientation of a StackPanel or WrapPanel.</summary>
public enum LayoutOrientation
{
    Horizontal,
    Vertical,
}

[MessagePackObject(keyAsPropertyName: true)]
public sealed record SizeDto(double Width, double Height);

[MessagePackObject(keyAsPropertyName: true)]
public sealed record RectDto(double X, double Y, double Width, double Height)
{
    [IgnoreMember]
    public double Right => X + Width;

    [IgnoreMember]
    public double Bottom => Y + Height;

    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;
}

[MessagePackObject(keyAsPropertyName: true)]
public sealed record ThicknessDto(double Left, double Top, double Right, double Bottom)
{
    public static readonly ThicknessDto Zero = new(0, 0, 0, 0);
}

/// <summary>A request to render one XAML document.</summary>
/// <param name="Version">Monotonically increasing per document; older results are discarded by the app.</param>
/// <param name="Xaml">
/// The render copy of the document (design ids added, §5.4). The host itself removes <c>x:Class</c>,
/// event attributes, and design ids on non-AvaloniaObject elements, since only it can resolve every type.
/// </param>
/// <param name="Scale">Pixels per DIP: the screen's rasterization scale × zoom.</param>
/// <param name="DesignSize">Preview size override in DIPs; null uses the document's own size.</param>
/// <param name="DocumentId">Identifies the open document; the host keeps one live render per document.</param>
/// <param name="BaseUri">
/// Where the document lives inside the project assembly (<c>avares://App/Views/MainWindow.axaml</c>), so relative paths
/// such as <c>/Assets/logo.png</c> resolve to the project's resources (§12). Null for a file without a project.
/// </param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record RenderRequest(
    long Version,
    string Xaml,
    double Scale = 1.0,
    ThemeVariantKind Theme = ThemeVariantKind.Default,
    SizeDto? DesignSize = null,
    string DocumentId = "",
    string? BaseUri = null);

/// <summary>The rendered frame and the layout of every designable element in it.</summary>
/// <param name="Bgra">Premultiplied BGRA8 pixels, row-major, stride = <c>PixelWidth * 4</c>.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record RenderResult(
    long Version,
    bool Success,
    int PixelWidth,
    int PixelHeight,
    double Scale,
    byte[]? Bgra,
    ElementLayout? Root,
    RootKind RootKind,
    string? WindowTitle,
    RenderError? Error)
{
    public static RenderResult Failed(long version, RenderError error) =>
        new(version, false, 0, 0, 0, null, null, RootKind.Other, null, error);
}

/// <summary>One element from the XAML document (one that carries a design id) and where it was laid out.</summary>
/// <param name="Bounds">In DIPs, relative to the top-left of the rendered frame.</param>
/// <param name="Children">Nearest descendants that also carry a design id (template parts are skipped).</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record ElementLayout(
    int DesignId,
    string TypeName,
    RectDto Bounds,
    bool IsVisible,
    ThicknessDto Margin,
    ThicknessDto Padding,
    PanelInfo? Panel,
    ElementLayout[] Children);

/// <summary>Panel-specific layout facts needed for placement and adorners.</summary>
/// <param name="RowOffsets">Grid only: top edge of each row plus the bottom edge of the last, relative to the grid.</param>
/// <param name="ColumnOffsets">Grid only: left edge of each column plus the right edge of the last, relative to the grid.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record PanelInfo(
    PanelKind Kind,
    LayoutOrientation? Orientation = null,
    double[]? RowOffsets = null,
    double[]? ColumnOffsets = null,
    int? Rows = null,
    int? Columns = null);

/// <summary>Why a render failed.</summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record RenderError(
    string Message,
    string? ExceptionType = null,
    int? Line = null,
    int? Column = null,
    string? OffendingType = null);
