using System.Diagnostics;

namespace XDesigner.Core.Project;

/// <summary>
/// Runs the user's built app (§11.3): <c>dotnet run --no-build</c> in the project folder, output streamed to Output ▸ Run.
/// <see cref="Stop"/> ends the whole process tree (dotnet and the app it started).
/// </summary>
public sealed class AppRunner : IDisposable
{
    private readonly Process _process;

    private AppRunner(Process process)
    {
        _process = process;
    }

    /// <summary>Raised once when the app has exited, with its exit code (any thread).</summary>
    public event Action<int>? Exited;

    public bool IsRunning
    {
        get
        {
            try
            {
                return !_process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public static AppRunner Start(string projectPath, Action<string> onLine)
    {
        var start = ProcessRunner.CreateStartInfo("dotnet", ["run", "--no-build", "--project", projectPath],
            Path.GetDirectoryName(Path.GetFullPath(projectPath))!);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var runner = new AppRunner(process);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                onLine(e.Data);
            }
        };
        process.Exited += (_, _) =>
        {
            process.WaitForExit(); // let the output readers finish
            runner.Exited?.Invoke(process.ExitCode);
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return runner;
    }

    /// <summary>Ends the app and the <c>dotnet</c> process that started it.</summary>
    public void Stop()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    public void Dispose()
    {
        Stop();
        _process.Dispose();
    }
}
