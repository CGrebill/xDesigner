using System.Text.Json;
using XDesigner.Core.Metadata;
using XDesigner.Protocol.Metadata;

namespace XDesigner.Core.Toolbox;

/// <summary>One toolbox entry.</summary>
/// <param name="Glyph">Segoe Fluent Icons character.</param>
/// <param name="Attributes">Set on every drop; <c>{name}</c> becomes the generated control name (§7.2).</param>
/// <param name="SizeWhenPositioned">Set only when dropped at a point (Canvas/Grid/plain panel), so panels aren't 0×0.</param>
public sealed record ToolboxItem(
    ControlTypeInfo Type,
    string Category,
    string Glyph,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyDictionary<string, string> SizeWhenPositioned)
{
    public string DisplayName => Type.Name;

    /// <summary>The toolbox list uses this as the row's accessible name (screen readers say "Button", not a record dump).</summary>
    public override string ToString() => DisplayName;
}

public sealed record ToolboxCategory(string Name, IReadOnlyList<ToolboxItem> Items, bool StartsCollapsed = false);

/// <summary>
/// Builds the toolbox (§9.1) from the curated <c>toolbox.json</c> plus whatever the render host found:
/// the user's own controls (Project), controls from NuGet packages (Packages) and any built-in control
/// not in the curated list (Other).
/// </summary>
public static class ToolboxCatalog
{
    private const string DefaultGlyph = "";
    private static readonly Lazy<CuratedFile> Curated = new(LoadCurated);

    public static IReadOnlyList<ToolboxCategory> Build(ControlCatalog catalog, string? userAssemblyName)
    {
        var placeable = catalog.All.Where(IsPlaceable).ToList();
        var categories = new List<ToolboxCategory>();
        var listed = new HashSet<string>();

        foreach (var category in Curated.Value.Categories)
        {
            var items = new List<ToolboxItem>();
            foreach (var entry in category.Items)
            {
                var type = placeable.FirstOrDefault(t => t.IsBuiltIn && t.Name == entry.Type && t.XmlNamespace == ControlTypeInfo.AvaloniaXmlNamespace)
                    ?? placeable.FirstOrDefault(t => t.Name == entry.Type && t.XmlNamespace == ControlTypeInfo.AvaloniaXmlNamespace);
                if (type is not null && listed.Add(type.FullName))
                {
                    items.Add(new ToolboxItem(type, category.Name, Glyph(entry.Glyph), entry.Attributes ?? [], entry.SizeWhenPositioned ?? []));
                }
            }

            if (items.Count > 0)
            {
                categories.Add(new ToolboxCategory(category.Name, items));
            }
        }

        AddGroup(categories, "Project", placeable.Where(t => !t.IsBuiltIn && t.AssemblyName == userAssemblyName));
        AddGroup(categories, "Packages", placeable.Where(t => !t.IsBuiltIn && t.AssemblyName != userAssemblyName && !IsDesignerAssembly(t.AssemblyName)));
        AddGroup(categories, "Other", placeable.Where(t => t.IsBuiltIn && !listed.Contains(t.FullName)), collapsed: true);
        return categories;
    }

    /// <summary>Default attributes for a type that isn't in <c>toolbox.json</c> (custom controls).</summary>
    public static ToolboxItem ItemFor(ControlTypeInfo type) => new(type, "", DefaultGlyph, new Dictionary<string, string>(), new Dictionary<string, string>());

    /// <summary>Creatable, and something you can drop inside a form (not a window or popup).</summary>
    public static bool IsPlaceable(ControlTypeInfo type) =>
        type.IsCreatable
        && !type.DerivesFrom("Avalonia.Controls.TopLevel")
        && !type.DerivesFrom("Avalonia.Controls.Primitives.PopupRoot")
        && !type.DerivesFrom("Avalonia.Controls.Primitives.OverlayPopupHost");

    private static void AddGroup(List<ToolboxCategory> categories, string name, IEnumerable<ControlTypeInfo> types, bool collapsed = false)
    {
        var items = types.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => ItemFor(t) with { Category = name })
            .ToList();
        if (items.Count > 0)
        {
            categories.Add(new ToolboxCategory(name, items, collapsed));
        }
    }

    private static bool IsDesignerAssembly(string assemblyName) =>
        assemblyName.StartsWith("XDesigner.", StringComparison.Ordinal);

    private static string Glyph(string? hex) =>
        hex is not null && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var code)
            ? char.ConvertFromUtf32(code)
            : DefaultGlyph;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static CuratedFile LoadCurated()
    {
        using var stream = typeof(ToolboxCatalog).Assembly.GetManifestResourceStream("XDesigner.Core.Toolbox.toolbox.json")
            ?? throw new InvalidOperationException("toolbox.json is not embedded.");
        return JsonSerializer.Deserialize<CuratedFile>(stream, JsonOptions)
            ?? throw new InvalidOperationException("toolbox.json is empty.");
    }

    private sealed record CuratedFile(List<CuratedCategory> Categories);

    private sealed record CuratedCategory(string Name, List<CuratedItem> Items);

    private sealed record CuratedItem(string Type, string? Glyph, Dictionary<string, string>? Attributes, Dictionary<string, string>? SizeWhenPositioned);
}
