using System.Xml.Linq;

namespace XDesigner.Core.Frameworks;

/// <summary>A UI framework family whose XAML the designer understands.</summary>
public enum FrameworkFamily
{
    Avalonia,
    WinUI,
    Maui,
}

/// <summary>A framework at a major version; each one has its own render host (§3, §14).</summary>
public enum FrameworkId
{
    Avalonia11,
    Avalonia12,
    WinUI3,
    Maui10,
    Maui11,
}

/// <summary>How custom-control namespaces are written in XAML.</summary>
public enum ClrNamespaceStyle
{
    /// <summary><c>using:My.Controls</c> (Avalonia, WinUI).</summary>
    Using,

    /// <summary><c>clr-namespace:My.Controls;assembly=My</c> (MAUI).</summary>
    ClrNamespace,
}

/// <summary>
/// What the designer needs to know about one framework to edit its XAML (§5.3): namespaces, file names, the names of the
/// controls it special-cases, how values and assets are written, and which render host draws it. Every document carries
/// one (<see cref="Xaml.XamlDocument.Profile"/>), so editing code asks the profile instead of assuming Avalonia.
/// </summary>
public sealed record FrameworkProfile
{
    /// <summary>Design-time namespace shared by Avalonia and WinUI (<c>d:DesignWidth</c>).</summary>
    public const string BlendDesignNamespace = "http://schemas.microsoft.com/expression/blend/2008";

    /// <summary>The 2006 XAML language namespace (Avalonia, WinUI).</summary>
    public const string Xaml2006Namespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>The 2009 XAML language namespace (MAUI).</summary>
    public const string Xaml2009Namespace = "http://schemas.microsoft.com/winfx/2009/xaml";

    public const string AvaloniaNamespace = "https://github.com/avaloniaui";
    public const string WinUINamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    public const string MauiNamespace = "http://schemas.microsoft.com/dotnet/2021/maui";

    /// <summary>MAUI's global namespace (MAUI 10+): MAUI's controls plus whatever the project registers for it.</summary>
    public const string MauiGlobalNamespace = "http://schemas.microsoft.com/dotnet/maui/global";
    public const string MauiDesignNamespace = "http://schemas.microsoft.com/dotnet/2021/maui/design";

    public required FrameworkId Id { get; init; }

    public required FrameworkFamily Family { get; init; }

    /// <summary>"Avalonia 12", "WinUI 3", ".NET MAUI 10".</summary>
    public required string DisplayName { get; init; }

    public required int MajorVersion { get; init; }

    /// <summary>The oldest version of that major the designer supports, when not x.0 (Avalonia 11.1: older 11s can't run on its renderer).</summary>
    public Version? MinimumVersion { get; init; }

    /// <summary>The default XML namespace of the framework's controls.</summary>
    public required XNamespace DefaultNamespace { get; init; }

    /// <summary>The XAML language namespace (<c>x:Name</c>, <c>x:Class</c>).</summary>
    public required XNamespace XamlNamespace { get; init; }

    /// <summary>The design-time namespace (<c>d:</c>).</summary>
    public required XNamespace DesignNamespace { get; init; }

    /// <summary>The XAML file extension, <c>.axaml</c> or <c>.xaml</c>.</summary>
    public required string FileExtension { get; init; }

    public required ClrNamespaceStyle ClrNamespaceStyle { get; init; }

    /// <summary>The CLR namespace of the built-in controls the designer refers to by name (<c>Avalonia.Controls</c>).</summary>
    public required string ControlsNamespace { get; init; }

    /// <summary>Folder of this framework's render host next to the app (<c>renderhosts\avalonia12</c>).</summary>
    public required string HostFolder { get; init; }

    /// <summary>The render host's executable name.</summary>
    public required string HostExecutable { get; init; }

    /// <summary>
    /// The host runs from a shadow folder of the project's build output (WinUI, §14.5): its XAML and resources resolve
    /// against the folder the host runs from, as in the user's app.
    /// </summary>
    public bool HostRunsFromProjectOutput { get; init; }

    /// <summary>The host runs with the project's resource index as its own (MAUI, §14.6; see <see cref="Rendering.ShadowHostFolder.Prepare"/>).</summary>
    public bool HostUsesProjectResourceIndex { get; init; }

    /// <summary>MSBuild item that packages project images (<c>AvaloniaResource</c>), or null where images need none.</summary>
    public required string? ResourceItemName { get; init; }

    /// <summary>The project folder images are added to (§12): <c>Assets</c>; MAUI <c>Resources\Images</c>.</summary>
    public string AssetsFolder { get; init; } = "Assets";

    /// <summary>Scheme of resource URIs inside the project assembly (<c>avares</c>), or null.</summary>
    public required string? ResourceUriScheme { get; init; }

    /// <summary>The embedded toolbox list for this family.</summary>
    public required string ToolboxResource { get; init; }

    /// <summary>The template folder for new projects, or null when the designer can't create them yet.</summary>
    public required string? ProjectTemplate { get; init; }

    /// <summary>The framework package version new projects reference (the one this framework's render host uses).</summary>
    public string? TemplateVersion { get; init; }

    /// <summary>Whether Grid accepts <c>RowDefinitions="Auto,*"</c> (WinUI needs property elements).</summary>
    public required bool CompactGridDefinitions { get; init; }

    /// <summary>The attribute and value that hide an element in a design-only render copy.</summary>
    public required (string Attribute, string Value) HiddenAttribute { get; init; }

    /// <summary>Base types of things that can't be placed inside a form (windows, pages, popups).</summary>
    public required IReadOnlyList<string> TopLevelTypes { get; init; }

    /// <summary>A Window's XAML sets the size it opens at (<c>Width</c>/<c>Height</c>); WinUI's can't, so it gets <c>d:DesignWidth</c>.</summary>
    public bool WindowSizeInXaml { get; init; } = true;

    /// <summary>The framework's tab control, its tab type and the property holding the tabs (§9.4); null where it has none.</summary>
    public TabsInfo? Tabs { get; init; }

    /// <summary>How the framework spells the layout attributes placement writes (§7.1).</summary>
    public LayoutVocabulary Layout { get; init; } = LayoutVocabulary.Xaml;

    /// <summary>
    /// The corners of a four-number <c>CornerRadius</c>, in the order the framework reads them: clockwise from the top left
    /// (Avalonia, WinUI); MAUI reads top left, top right, bottom left, bottom right.
    /// </summary>
    public IReadOnlyList<string> CornerNames { get; init; } = ["Top left", "Top right", "Bottom right", "Bottom left"];

    /// <summary>Panels to name in messages such as "Select a panel (…)": the framework's own.</summary>
    public string PanelExamples { get; init; } = "Grid, StackPanel, Canvas…";

    /// <summary>The full CLR name of a built-in control: <c>Grid</c> → <c>Avalonia.Controls.Grid</c>.</summary>
    public string BuiltIn(string name) => $"{ControlsNamespace}.{name}";

    /// <summary>The code-behind file name for a XAML file name (<c>.axaml</c> → <c>.axaml.cs</c>).</summary>
    public string CodeBehindExtension => FileExtension + ".cs";

    public override string ToString() => DisplayName;

    /// <summary>
    /// How a renderer's exact framework version reads (§15.1): <c>Avalonia 12.1.6</c>; for WinUI, whose version is that of
    /// the Windows App SDK, <c>WinUI 3, Windows App SDK 2.7.0</c>.
    /// </summary>
    public string DescribeVersion(string frameworkVersion) =>
        Family switch
        {
            FrameworkFamily.WinUI => $"{DisplayName}, Windows App SDK {frameworkVersion}",
            FrameworkFamily.Maui => $".NET MAUI {frameworkVersion}",
            _ => $"{Family} {frameworkVersion}",
        };

    public static readonly FrameworkProfile Avalonia12 = new()
    {
        Id = FrameworkId.Avalonia12,
        Family = FrameworkFamily.Avalonia,
        DisplayName = "Avalonia 12",
        MajorVersion = 12,
        DefaultNamespace = AvaloniaNamespace,
        XamlNamespace = Xaml2006Namespace,
        DesignNamespace = BlendDesignNamespace,
        FileExtension = ".axaml",
        ClrNamespaceStyle = ClrNamespaceStyle.Using,
        ControlsNamespace = "Avalonia.Controls",
        HostFolder = "avalonia12",
        HostExecutable = "XDesigner.RenderHost.Avalonia12.exe",
        ResourceItemName = "AvaloniaResource",
        ResourceUriScheme = "avares",
        ToolboxResource = "toolbox.avalonia.json",
        ProjectTemplate = "AvaloniaApp",
        TemplateVersion = "12.1.6", // = AvaloniaVersion in Directory.Packages.props
        CompactGridDefinitions = true,
        HiddenAttribute = ("IsVisible", "False"),
        TopLevelTypes = ["Avalonia.Controls.TopLevel", "Avalonia.Controls.Primitives.PopupRoot", "Avalonia.Controls.Primitives.OverlayPopupHost"],
        Tabs = new("TabControl", "TabItem", "Items"),
    };

    public static readonly FrameworkProfile Avalonia11 = Avalonia12 with
    {
        Id = FrameworkId.Avalonia11,
        DisplayName = "Avalonia 11",
        MajorVersion = 11,
        MinimumVersion = new(11, 1),
        HostFolder = "avalonia11",
        HostExecutable = "XDesigner.RenderHost.Avalonia11.exe",
        TemplateVersion = "11.3.23", // = Avalonia11Version in Directory.Packages.props; same template as Avalonia 12
    };

    public static readonly FrameworkProfile WinUI3 = new()
    {
        Id = FrameworkId.WinUI3,
        Family = FrameworkFamily.WinUI,
        DisplayName = "WinUI 3",
        MajorVersion = 3,
        DefaultNamespace = WinUINamespace,
        XamlNamespace = Xaml2006Namespace,
        DesignNamespace = BlendDesignNamespace,
        FileExtension = ".xaml",
        ClrNamespaceStyle = ClrNamespaceStyle.Using,
        ControlsNamespace = "Microsoft.UI.Xaml.Controls",
        HostFolder = "winui3",
        HostExecutable = "XDesigner.RenderHost.WinUI.exe",
        HostRunsFromProjectOutput = true,
        ResourceItemName = "Content",
        ResourceUriScheme = "ms-appx",
        ToolboxResource = "toolbox.winui.json",
        ProjectTemplate = "WinUIApp",
        TemplateVersion = "2.7.0", // = WindowsAppSdkVersion in Directory.Packages.props
        CompactGridDefinitions = false,
        HiddenAttribute = ("Visibility", "Collapsed"),
        TopLevelTypes = ["Microsoft.UI.Xaml.Window", "Microsoft.UI.Xaml.Controls.Page", "Microsoft.UI.Xaml.Controls.Primitives.Popup"],
        Tabs = new("TabView", "TabViewItem", "TabItems"),
        WindowSizeInXaml = false,
    };

    public static readonly FrameworkProfile Maui10 = new()
    {
        Id = FrameworkId.Maui10,
        Family = FrameworkFamily.Maui,
        DisplayName = ".NET MAUI 10",
        MajorVersion = 10,
        DefaultNamespace = MauiNamespace,
        XamlNamespace = Xaml2009Namespace,
        DesignNamespace = MauiDesignNamespace,
        FileExtension = ".xaml",
        ClrNamespaceStyle = ClrNamespaceStyle.ClrNamespace,
        ControlsNamespace = "Microsoft.Maui.Controls",
        HostFolder = "maui10",
        HostExecutable = "XDesigner.RenderHost.Maui10.exe",
        HostRunsFromProjectOutput = true,
        HostUsesProjectResourceIndex = true,
        ResourceItemName = "MauiImage",
        AssetsFolder = @"Resources\Images",
        ResourceUriScheme = null,
        ToolboxResource = "toolbox.maui.json",
        ProjectTemplate = "MauiApp",
        TemplateVersion = "10.0.130", // = Maui10Version in Directory.Packages.props
        CompactGridDefinitions = true,
        HiddenAttribute = ("IsVisible", "False"),
        TopLevelTypes = ["Microsoft.Maui.Controls.Page", "Microsoft.Maui.Controls.Window"],
        Layout = LayoutVocabulary.Maui,
        CornerNames = ["Top left", "Top right", "Bottom left", "Bottom right"],
        PanelExamples = "Grid, VerticalStackLayout, AbsoluteLayout…",
    };

    public static readonly FrameworkProfile Maui11 = Maui10 with
    {
        Id = FrameworkId.Maui11,
        DisplayName = ".NET MAUI 11",
        MajorVersion = 11,
        HostFolder = "maui11",
        HostExecutable = "XDesigner.RenderHost.Maui11.exe",
        TemplateVersion = "11.0.0", // = Maui11Version in Directory.Packages.props
    };

    public static IReadOnlyList<FrameworkProfile> All { get; } = [Avalonia12, Avalonia11, WinUI3, Maui10, Maui11];

    public static FrameworkProfile For(FrameworkId id) => All.First(p => p.Id == id);

    /// <summary>The profile for a framework family at a major version, or null when the designer doesn't know that version.</summary>
    public static FrameworkProfile? For(FrameworkFamily family, int majorVersion) =>
        All.FirstOrDefault(p => p.Family == family && p.MajorVersion == majorVersion);

    /// <summary>
    /// The framework family a XAML document is written for, from its root's default namespace; null when it isn't one
    /// the designer knows. A WPF file has WinUI's namespace, so it reads as WinUI: its project's framework (no UseWinUI) decides.
    /// </summary>
    public static FrameworkFamily? FamilyOf(XNamespace rootNamespace) => rootNamespace.NamespaceName switch
    {
        AvaloniaNamespace => FrameworkFamily.Avalonia,
        WinUINamespace => FrameworkFamily.WinUI,
        MauiNamespace or MauiGlobalNamespace => FrameworkFamily.Maui,
        _ => null,
    };

    /// <summary>The newest profile of a family: what a file opened without a project is assumed to be.</summary>
    public static FrameworkProfile Latest(FrameworkFamily family) =>
        All.Where(p => p.Family == family).MaxBy(p => p.MajorVersion)!;

    /// <summary>Whether <paramref name="ns"/> is an XAML language namespace (2006 or 2009).</summary>
    public static bool IsXamlLanguage(XNamespace ns) => ns.NamespaceName is Xaml2006Namespace or Xaml2009Namespace;

    /// <summary>Whether <paramref name="ns"/> is one of the design-time namespaces.</summary>
    public static bool IsDesign(XNamespace ns) => ns.NamespaceName is BlendDesignNamespace or MauiDesignNamespace;
}

/// <summary>A framework's tab control (§9.4).</summary>
/// <param name="Control">The tab control's name (<c>TabControl</c>, <c>TabView</c>).</param>
/// <param name="Item">A tab's element name (<c>TabItem</c>, <c>TabViewItem</c>).</param>
/// <param name="ItemsProperty">The property holding the tabs (<c>Items</c>, <c>TabItems</c>); it is the content property.</param>
public sealed record TabsInfo(string Control, string Item, string ItemsProperty);
