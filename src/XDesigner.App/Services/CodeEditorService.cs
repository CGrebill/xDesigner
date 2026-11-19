using XDesigner.Core.Settings;

namespace XDesigner.App.Services;

/// <summary>
/// Opens source files for editing. Everything in the designer that shows code (a handler it created, a build error,
/// a .cs file in Solution Explorer) goes through this, so an editor built into the designer can replace the external
/// one later without touching those places (§15.3).
/// </summary>
public interface ICodeEditorService
{
    /// <summary>Opens <paramref name="path"/> at <paramref name="line"/> (1-based); returns why it couldn't, or null.</summary>
    string? Open(string path, int line);
}

/// <summary>The external editor chosen in Settings (VS Code, Visual Studio, the Windows default, or a custom command).</summary>
public sealed class ExternalCodeEditor(Func<DesignerSettings> settings) : ICodeEditorService
{
    public string? Open(string path, int line) => EditorLauncher.Open(path, Math.Max(1, line), settings());
}
