using XDesigner.Core.Project;

namespace XDesigner.App.ViewModels;

/// <summary>One row of Output ▸ Error List: a build error or warning, and where it is.</summary>
/// <param name="baseDirectory">The solution's folder: files are shown relative to it (a solution has many MainWindow.axaml.cs, review L54).</param>
public sealed class DiagnosticRow(BuildDiagnostic diagnostic, string? baseDirectory = null)
{
    public BuildDiagnostic Diagnostic { get; } = diagnostic;

    public string Glyph => Diagnostic.IsError ? "" : "";

    public string Code => Diagnostic.Code;

    public string Message => Diagnostic.Message;

    /// <summary><c>App\Views\MainWindow.axaml.cs, line 12</c> (relative to the solution), or nothing for a diagnostic without a file.</summary>
    public string Location => Diagnostic.File is { } file
        ? $"{ShownPath(file)}{(Diagnostic.Line is { } line ? $", line {line}" : "")}"
        : "";

    private string ShownPath(string file) =>
        baseDirectory is { Length: > 0 } dir && Path.GetFullPath(file).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(dir, file)
            : Path.GetFileName(file);

    /// <summary>Screen readers read the whole row (lists of records would read their type name).</summary>
    public override string ToString() => $"{(Diagnostic.IsError ? "Error" : "Warning")} {Code}: {Message} {Location}";
}
