using System.Diagnostics;
using XDesigner.Core.Settings;

namespace XDesigner.App.Services;

/// <summary>
/// "Open in editor" for a created handler (§10.1 step 7), with the editor chosen in Settings (§15.3): VS Code at the line
/// (<c>code -g file:line</c>), Visual Studio (<c>devenv /edit</c>), the program Windows associates with <c>.cs</c> files,
/// or a custom command line with <c>{file}</c> and <c>{line}</c>. Automatic is VS Code when installed, else the default.
/// </summary>
public static class EditorLauncher
{
    /// <summary>Opens <paramref name="path"/> at <paramref name="line"/>; returns why not, or null on success.</summary>
    public static string? Open(string path, int line, DesignerSettings settings)
    {
        try
        {
            switch (settings.Editor)
            {
                case EditorChoice.VisualStudioCode:
                    return FindVsCode() is { } code ? Start(code, ["-g", $"{path}:{line}"]) : "VS Code isn't installed (code.cmd wasn't found). Choose another editor in Settings.";
                case EditorChoice.VisualStudio:
                    return FindVisualStudio() is { } devenv ? Start(devenv, ["/edit", path]) : "Visual Studio wasn't found. Choose another editor in Settings.";
                case EditorChoice.SystemDefault:
                    return OpenWithShell(path);
                case EditorChoice.Custom:
                    return OpenCustom(settings.CustomEditorCommand, path, line);
                default:
                    return FindVsCode() is { } auto ? Start(auto, ["-g", $"{path}:{line}"]) : OpenWithShell(path);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return $"Couldn't open {Path.GetFileName(path)}: {ex.Message}";
        }
    }

    /// <summary>Splits a custom command into program and arguments, filling in <c>{file}</c> and <c>{line}</c>.</summary>
    public static (string Program, string Arguments)? ParseCustom(string command, string path, int line)
    {
        var text = command.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        string program;
        string rest;
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            if (close < 0)
            {
                return null;
            }

            program = text[1..close];
            rest = text[(close + 1)..];
        }
        else
        {
            var space = text.IndexOf(' ');
            program = space < 0 ? text : text[..space];
            rest = space < 0 ? "" : text[space..];
        }

        if (!rest.Contains("{file}", StringComparison.Ordinal))
        {
            rest += " {file}";
        }

        // The path is always passed quoted (it may contain spaces), whether or not the command quoted {file} itself.
        var quoted = $"\"{path}\"";
        var arguments = rest.Replace("\"{file}\"", quoted, StringComparison.Ordinal).Replace("{file}", quoted, StringComparison.Ordinal)
            .Replace("{line}", line.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return (program, arguments.Trim());
    }

    private static string? OpenCustom(string command, string path, int line)
    {
        if (ParseCustom(command, path, line) is not { } parsed)
        {
            return "No custom editor command is set. Enter one in Settings, for example: notepad++.exe -n{line} \"{file}\"";
        }

        Process.Start(new ProcessStartInfo(parsed.Program, parsed.Arguments) { UseShellExecute = true })?.Dispose();
        return null;
    }

    private static string? Start(string program, string[] arguments)
    {
        var info = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Process.Start(info)?.Dispose();
        return null;
    }

    private static string? OpenWithShell(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        return null;
    }

    /// <summary>The newest Visual Studio's devenv.exe, through vswhere (installed with every Visual Studio since 2017).</summary>
    private static string? FindVisualStudio()
    {
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere))
        {
            return null;
        }

        using var process = Process.Start(new ProcessStartInfo(vswhere)
        {
            ArgumentList = { "-latest", "-prerelease", "-property", "productPath" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        });
        if (process is null)
        {
            return null;
        }

        // vswhere prints one short line, so waiting before reading can't fill the pipe. Don't let a stuck vswhere (the
        // Visual Studio installer updating) freeze the designer.
        if (!process.WaitForExit(5000))
        {
            process.Kill();
            return null;
        }

        var output = process.StandardOutput.ReadToEnd().Trim();
        return output.Length > 0 && File.Exists(output) ? output : null;
    }

    /// <summary><c>code.cmd</c> on PATH or in the default per-user/machine install folders.</summary>
    public static string? FindVsCode()
    {
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(dir => Path.Combine(dir, "code.cmd"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "bin", "code.cmd"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "bin", "code.cmd"));
        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }

        return null;
    }
}
