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

    /// <summary>Several builds (a solution built project by project) as one result.</summary>
    public static BuildResult Combine(IReadOnlyList<BuildResult> results) => new(
        results.All(r => r.Succeeded),
        results.FirstOrDefault(r => r.ExitCode != 0)?.ExitCode ?? 0,
        results.SelectMany(r => r.Diagnostics).Distinct().ToList(),
        TimeSpan.FromTicks(results.Sum(r => r.Duration.Ticks)));
}

/// <summary>
/// One <c>dotnet build</c>: a solution or project, the platform it needs (<c>x64</c> for WinUI) and the target framework to
/// build (MAUI's Windows one), if any.
/// </summary>
/// <param name="Solution">The solution a project is built for when it is built on its own (its <c>$(SolutionDir)</c> and friends).</param>
public sealed record BuildStep(string Path, string? Platform, string? Framework = null, string? Solution = null);

/// <summary><c>dotnet build</c> of the open project, with output streamed to the Build tab and diagnostics parsed (§11.2).</summary>
public static partial class BuildRunner
{
    /// <param name="platform">Passed as <c>-p:Platform</c> (WinUI projects have no AnyCPU build).</param>
    /// <param name="framework">Passed as <c>-f</c> (a MAUI project is built for Windows only).</param>
    public static async Task<BuildResult> BuildAsync(string projectPath, Action<string>? onLine = null, string? platform = null, string? framework = null, string? solution = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        List<string> args = ["build", projectPath, "-c", "Debug", "-nologo", "-v:minimal", "-tl:off", "-p:GenerateFullPaths=true"];
        if (platform is not null)
        {
            args.Add($"-p:Platform={platform}");
        }

        if (framework is not null)
        {
            args.Add("-f");
            args.Add(framework);
        }

        if (solution is not null)
        {
            // Built on its own for its platform or framework, but as part of the solution: projects may use these (review L16).
            var full = Path.GetFullPath(solution);
            args.Add($"-p:SolutionDir={Path.GetDirectoryName(full)}{Path.DirectorySeparatorChar}");
            args.Add($"-p:SolutionPath={full}");
            args.Add($"-p:SolutionName={Path.GetFileNameWithoutExtension(full)}");
            args.Add($"-p:SolutionFileName={Path.GetFileName(full)}");
            args.Add($"-p:SolutionExt={Path.GetExtension(full)}");
        }

        var result = await ProcessRunner.RunAsync(
            "dotnet",
            args,
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

    // The file may contain parentheses ("App - Copy (2)") and drive colons; MSBuild may print a subcategory before the
    // severity ("Avalonia error AVLN2000").
    [GeneratedRegex(@"^\s*(?<file>\S.*?)\((?<line>\d+)(?:,(?<col>\d+))?(?:,\d+,\d+)?\)\s*:\s*(?:[\w.-]+\s+)?(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d*)\s*:\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?\s*$")]
    private static partial Regex WithPosition();

    [GeneratedRegex(@"^\s*(?<file>\S.*?)\s*:\s*(?:[\w.-]+\s+)?(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?\s*$")]
    private static partial Regex WithoutPosition();
}
