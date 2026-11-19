using System.Text.Json;
using System.Text.Json.Serialization;
using XDesigner.Protocol.Rendering;

namespace XDesigner.Core.Settings;

/// <summary>Which program "Open in editor" uses for code-behind files (§10.1 step 7).</summary>
public enum EditorChoice
{
    /// <summary>VS Code when it is installed, otherwise the program Windows opens .cs files with.</summary>
    Automatic,
    VisualStudioCode,
    VisualStudio,
    SystemDefault,
    /// <summary><see cref="DesignerSettings.CustomEditorCommand"/>, with <c>{file}</c> and <c>{line}</c> filled in.</summary>
    Custom,
}

/// <summary>
/// The designer's settings (§15.3), kept in <c>%LOCALAPPDATA%\xDesigner\settings.json</c>: recent files, editor,
/// preview defaults, snapping, and the pane sizes the user dragged.
/// </summary>
public sealed class DesignerSettings
{
    public const int MaxRecent = 10;

    /// <summary>Recently opened files and projects (.axaml, .csproj, .sln), most recent first.</summary>
    public List<string> Recent { get; set; } = [];

    public EditorChoice Editor { get; set; } = EditorChoice.Automatic;

    /// <summary>For <see cref="EditorChoice.Custom"/>: a command line such as <c>notepad++.exe -n{line} "{file}"</c>.</summary>
    public string CustomEditorCommand { get; set; } = "";

    /// <summary>The preview theme new documents start with.</summary>
    public ThemeVariantKind PreviewTheme { get; set; } = ThemeVariantKind.Default;

    /// <summary>Snap to the edges and centres of nearby controls while moving and resizing (Alt turns it off for a drag).</summary>
    public bool SnapToControls { get; set; } = true;

    /// <summary>Snap positions and sizes to a grid of <see cref="GridSize"/> DIPs.</summary>
    public bool SnapToGrid { get; set; }

    public double GridSize { get; set; } = 8;

    public bool ShowAdvancedProperties { get; set; }

    public double? ToolboxWidth { get; set; }

    public double? SidePaneWidth { get; set; }

    public double? OutputHeight { get; set; }

    /// <summary>Puts <paramref name="path"/> first in <see cref="Recent"/>.</summary>
    public void AddRecent(string path)
    {
        var full = Path.GetFullPath(path);
        Recent.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, full);
        if (Recent.Count > MaxRecent)
        {
            Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
        }
    }

    public void RemoveRecent(string path) =>
        Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Loads and saves <see cref="DesignerSettings"/> as JSON. A missing or unreadable file gives the defaults.</summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => AppDataPaths.Settings;

    public string Path { get; } = path;

    public DesignerSettings Load()
    {
        try
        {
            var settings = File.Exists(Path) ? JsonSerializer.Deserialize<DesignerSettings>(File.ReadAllText(Path), Options) ?? new() : new();

            // Valid JSON can still say "null" where the app expects a value (a hand-edited file): use the defaults there.
            settings.Recent = settings.Recent?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? [];
            settings.CustomEditorCommand ??= "";
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new();
        }
    }

    /// <summary>Writes the settings (to a temporary file first, so a crash never leaves half a file). Returns false on failure.</summary>
    public bool Save(DesignerSettings settings)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            // A temporary name of its own, so two running designers saving at once don't write the same file.
            var temp = $"{Path}.{Environment.ProcessId}.tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
                File.Move(temp, Path, overwrite: true);
            }
            finally
            {
                File.Delete(temp);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
