using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using XDesigner.Core.Project;
using CommunityToolkit.Mvvm.ComponentModel;

namespace XDesigner.App.ViewModels;

/// <summary>New projects, new items, Run/Stop (§11.3–11.5).</summary>
public sealed partial class ShellViewModel
{
    private AppRunner? _runner;
    private bool _stopRequested;

    /// <summary>The project's app is running (started with Run).</summary>
    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    /// <summary>Output ▸ Run: what the running app writes.</summary>
    public ObservableCollection<LogLine> RunLines { get; } = [];

    /// <summary>The window brings the Run tab forward.</summary>
    public event Action? ShowRunOutput;

    /// <summary>
    /// Run (F5): saves the project's documents, builds, and starts the app with <c>dotnet run --no-build</c>. A running copy
    /// is stopped first (one instance per project; it would also lock the files the build writes).
    /// </summary>
    public async Task RunAsync()
    {
        if (Project is not { } project)
        {
            RaiseNotify(NotifyKind.Warning, "Open or create a project to run it (File ▸ New project, or Project ▸ Open project).");
            return;
        }

        // Set before the first await, so pressing Run (or Restart) twice can't start two builds and two copies of the app.
        if (IsBuilding || _buildBusy || _runBusy)
        {
            return;
        }

        SetBusy(ref _runBusy, true);
        try
        {
            await RunCoreAsync(project);
        }
        finally
        {
            SetBusy(ref _runBusy, false);
        }
    }

    private bool _runBusy;

    private async Task RunCoreAsync(ProjectInfo project)
    {
        if (!await BuildAsync())
        {
            return; // the build notification already says why
        }

        RunLines.Clear();
        AppendRun($"Running {project.Name}…");
        AppRunner runner;
        try
        {
            runner = await AppRunner.StartAsync(project, line => RunOnUi(() => AppendRun(line)));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ProjectException or COMException or ArgumentException)
        {
            AppendRun($"Couldn't start {project.Name}: {ex.Message}");
            RaiseNotify(NotifyKind.Error, $"Couldn't start {project.Name}: {ex.Message}");
            return;
        }

        _runner = runner;
        _stopRequested = false;
        IsRunning = true;
        ShowRunOutput?.Invoke();
        runner.Exited += code => RunOnUi(() =>
        {
            if (!ReferenceEquals(runner, _runner))
            {
                return;
            }

            IsRunning = false;
            _runner = null;
            runner.Dispose();
            AppendRun(_stopRequested ? $"{project.Name} was stopped." : $"{project.Name} exited with code {code}.");
            if (!_stopRequested && code != 0)
            {
                RaiseNotify(NotifyKind.Warning, $"{project.Name} exited with code {code}. Its output is in Output ▸ Run.",
                    [new NotifyAction("Show run output", () => ShowRunOutput?.Invoke())]);
            }
        });
        RaiseNotify(NotifyKind.Success, $"{project.Name} is running. Stop it with Shift+F5 or the Stop button.");
    }

    /// <summary>Stop (Shift+F5): ends the running app.</summary>
    public void StopRun()
    {
        if (_runner is { } runner)
        {
            _stopRequested = true;
            runner.Stop();
        }
    }

    /// <summary>Builds need the app stopped first: a running app keeps its files locked.</summary>
    private async Task StopRunForBuildAsync()
    {
        if (_runner is not { } runner || !runner.IsRunning)
        {
            return;
        }

        AppendLog("Stopped the running app so the project can be rebuilt.");
        StopRun();
        for (var i = 0; i < 20 && runner.IsRunning; i++)
        {
            await Task.Delay(100);
        }
    }

    /// <summary>File ▸ New project: writes the template, opens the project (which builds it) and its main window.</summary>
    /// <param name="packaged">WinUI: create a packaged (MSIX) app.</param>
    public async Task<string?> CreateProjectAsync(string parentDirectory, string name, string title, Core.Frameworks.FrameworkProfile framework, bool packaged = false)
    {
        string projectPath;
        try
        {
            projectPath = await Task.Run(() => ProjectTemplates.CreateApp(parentDirectory, name, title, framework, packaged));
        }
        catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }

        AppendLog($"Created project {name} in {Path.GetDirectoryName(projectPath)}.");
        await OpenProjectAsync(projectPath);
        return null;
    }

    /// <summary>The folder Project ▸ Add puts new files in: the current document's folder if it's in the project, else the project's.</summary>
    public string DefaultItemFolder =>
        SelectedDocument is { } doc && Project is { } p && p.Contains(doc.FilePath)
            ? Path.GetDirectoryName(doc.FilePath)!
            : Project?.Directory ?? "";

    /// <summary>Project ▸ Add window / page / user control: creates the files from the project's framework's template and opens the new one.</summary>
    public async Task<string?> AddItemAsync(ItemKind kind, string className, string folder)
    {
        if (Project is not { } project)
        {
            return "Open a project first.";
        }

        // A library, test or WPF project would get files of a framework it doesn't use (review M5).
        if (ProjectProfile is not { } profile)
        {
            return $"{project.Name} doesn't use a UI framework the designer knows, so the designer can't add items to it. Set a designable project as the startup project first (Solution Explorer).";
        }

        string xamlPath;
        try
        {
            xamlPath = ProjectTemplates.CreateItem(project, folder, className.Trim(), kind, profile);
        }
        catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }

        AppendLog($"Created {Path.GetRelativePath(project.Directory, xamlPath)} and its code-behind.");
        RefreshProjectFiles();
        if (Solution is { } solution && solution.Find(project.ProjectPath) is not null)
        {
            Solution = solution.With(SolutionModel.Describe(project)); // Solution Explorer lists the new file (review L17)
        }
        await OpenFileAsync(xamlPath);
        var winui = profile.Family == Core.Frameworks.FrameworkFamily.WinUI;
        var maui = profile.Family == Core.Frameworks.FrameworkFamily.Maui;
        RaiseNotify(NotifyKind.Success, kind switch
            {
                ItemKind.UserControl => $"Created {className}. Rebuild to use it as a control in your other {(maui ? "pages" : "windows")} (it will appear under Project in the toolbox).",
                ItemKind.Page when maui => $"Created {className}. Show it from code with: await Navigation.PushAsync(new {className}()); (in a NavigationPage) or by making it a window's Page.",
                ItemKind.Page => $"Created {className}. Show it in a Frame from code with: frame.Navigate(typeof({className}));",
                _ => $"Created {className}. Open it from code with: new {className}().{(winui ? "Activate" : "Show")}();",
            },
            kind == ItemKind.UserControl ? [new NotifyAction("Rebuild", () => _ = BuildAsync())] : null);
        return null;
    }

    private void AppendRun(string line)
    {
        RunLines.Add(new LogLine(line));
        while (RunLines.Count > 5000)
        {
            RunLines.RemoveAt(0);
        }
    }
}
