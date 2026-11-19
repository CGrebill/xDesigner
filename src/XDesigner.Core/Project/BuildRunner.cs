using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace XDesigner.Core.Project;

/// <summary>One compiler/MSBuild error or warning from a build (§11.2).</summary>
public sealed record BuildDiagnostic(string? File, int? Line, int? Column, bool IsError, string Code, string Message)
{
    public override string ToString() =>
        $"{(File is null ? "" : $"{Path.GetFileName(File)}({Line}{(Column is { } c ? $",{c}" : "")}): ")}{(IsError ? "error" : "warning")} {Code}: {Message}";
}

public sealed record BuildResult(bool Succeeded, int ExitCode, IReadOnlyList<BuildDiagnostic> Diagnostics, TimeSpan Duration)
{
    public IEnumerable<BuildDiagnostic> Errors => Diagnostics.Where(d => d.IsError);

    public IEnumerable<BuildDiagnostic> Warnings => Diagnostics.Where(d => !d.IsError);
}

/// <summary><c>dotnet build</c> of the open project, with output streamed to the Build tab and diagnostics parsed (§11.2).</summary>
public static partial class BuildRunner
{
    public static async Task<BuildResult> BuildAsync(string projectPath, Action<string>? onLine = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await ProcessRunner.RunAsync(
            "dotnet",
            ["build", projectPath, "-c", "Debug", "-nologo", "-v:minimal", "-tl:off", "-p:GenerateFullPaths=true"],
            Path.GetDirectoryName(Path.GetFullPath(projectPath))!,
            onLine,
            cancellationToken).ConfigureAwait(false);

        var diagnostics = result.Output.Split('\n').Select(l => ParseLine(l.TrimEnd('\r'))).OfType<BuildDiagnostic>().Distinct().ToList();
        return new BuildResult(result.ExitCode == 0, result.ExitCode, diagnostics, stopwatch.Elapsed);
    }

    /// <summary>
    /// <c>C:\App\MainWindow.axaml.cs(12,5): error CS1002: ; expected [C:\App\App.csproj]</c>, or a file-less
    /// <c>MSBUILD : error MSB1009: Project file does not exist.</c> Null for other lines.
    /// </summary>
    public static BuildDiagnostic? ParseLine(string line)
    {
        var m = WithPosition().Match(line);
        if (m.Success)
        {
            return new BuildDiagnostic(m.Groups["file"].Value.Trim(), int.Parse(m.Groups["line"].Value, CultureInfo.InvariantCulture),
                m.Groups["col"].Success ? int.Parse(m.Groups["col"].Value, CultureInfo.InvariantCulture) : null,
                m.Groups["sev"].Value == "error", m.Groups["code"].Value, m.Groups["msg"].Value.Trim());
        }

        m = WithoutPosition().Match(line);
        return m.Success
            ? new BuildDiagnostic(null, null, null, m.Groups["sev"].Value == "error", m.Groups["code"].Value, m.Groups["msg"].Value.Trim())
            : null;
    }

    /// <summary>
    /// Whether <c>dotnet</c> in the project's folder resolves to a preview SDK (e.g. the installed .NET 11 preview), which
    /// may build differently; the designer warns, it doesn't refuse. Returns the version when it is a preview.
    /// </summary>
    public static async Task<string?> PreviewSdkVersionAsync(string projectDirectory, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("dotnet", ["--version"], projectDirectory, null, cancellationToken).ConfigureAwait(false);
            var version = result.Output.Trim();
            return result.ExitCode == 0 && version.Contains('-', StringComparison.Ordinal) ? version : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^\s*(?<file>[^\s(][^(]*?)\((?<line>\d+)(?:,(?<col>\d+))?(?:,\d+,\d+)?\)\s*:\s*(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d*)\s*:\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?\s*$")]
    private static partial Regex WithPosition();

    [GeneratedRegex(@"^\s*(?<file>[^:(]+?)\s*:\s*(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?\s*$")]
    private static partial Regex WithoutPosition();
}
