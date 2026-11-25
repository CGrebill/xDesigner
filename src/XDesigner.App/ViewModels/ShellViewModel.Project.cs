using System.Collections.ObjectModel;
using System.Xml;
using XDesigner.Core.Frameworks;
using XDesigner.Core.Project;
using XDesigner.Core.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;

namespace XDesigner.App.ViewModels;

/// <summary>Projects, builds and file watching (§11.1, §11.2, §11.6).</summary>
public sealed partial class ShellViewModel
{
    private static readonly TimeSpan ExternalBuildSettle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FileChangeSettle = TimeSpan.FromMilliseconds(400);

    private readonly Dictionary<DocumentViewModel, (FileSystemWatcher Watcher, DispatcherQueueTimer Timer)> _fileWatchers = [];
    private readonly Dictionary<string, FileSystemWatcher> _outputWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DispatcherQueueTimer> _outputTimers = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _ownBuildFinished = DateTime.MinValue;

    /// <summary>The open project, if any (documents of other folders still open on their own).</summary>
    [ObservableProperty]
    public partial ProjectInfo? Project { get; private set; }

    [ObservableProperty]
    public partial bool IsBuilding { get; private set; }

    /// <summary>Output ▸ Build.</summary>
    public ObservableCollection<LogLine> BuildLines { get; } = [];

    /// <summary>The project's <c>.axaml</c> files (Project ▸ Files).</summary>
    public IReadOnlyList<string> ProjectFiles { get; private set; } = [];

    public event Action? ProjectFilesChanged;

    /// <summary>A message for the notification strip.</summary>
    public event Action<NotifyRequest>? Notify;

    /// <summary>
    /// The open solution: all its projects, each with its framework (one project opened on its own is a solution of one).
    /// <see cref="Project"/> is its startup project.
    /// </summary>
    [ObservableProperty]
    public partial SolutionModel? Solution { get; private set; }

    /// <summary>The framework of <see cref="Project"/> (what Project ▸ Add creates), or null without a project.</summary>
    public Core.Frameworks.FrameworkProfile? ProjectProfile =>
        Project is { } project ? Solution?.Find(project.ProjectPath)?.Framework.Profile : null; // Project is always one of Solution's

    /// <summary>Opens a <c>.csproj</c>, <c>.sln</c>/<c>.slnx</c> or a XAML file.</summary>
    public async Task OpenAnyAsync(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".csproj" or ".sln" or ".slnx":
                if (await OpenAsync(path))
                {
                    Remember(path); // only what opened (review M4)
                }

                break;
            default:
                if (await OpenFileAsync(path) is not null)
                {
                    Remember(path);
                }

                break;
        }
    }

    /// <summary>A solution: every project in it, each designed with its own framework's renderer.</summary>
    public Task OpenSolutionAsync(string solutionPath) => OpenAsync(solutionPath);

    /// <summary>A project on its own (a solution of one).</summary>
    public Task OpenProjectAsync(string projectPath) => OpenAsync(projectPath);

    /// <summary>
    /// Opens a solution or project: evaluates every project with MSBuild, picks the startup project, opens its main
    /// window, then builds so the renderers can show the projects' custom controls and styles (D11: build on open).
    /// </summary>
    /// <returns>Whether it opened (false: nothing changed, and the notification says why).</returns>
    private async Task<bool> OpenAsync(string path)
    {
        var name = Path.GetFileName(path);

        // One open at a time, and not into a running build: its results belong to the solution it started for (review M3).
        if (_openBusy)
        {
            RaiseNotify(NotifyKind.Warning, $"Another project is still opening. Open {name} when it's done.");
            return false;
        }

        SetBusy(ref _openBusy, true);
        try
        {
            if (IsBuilding || _buildBusy)
            {
                AppendLog($"Waiting for the running build to finish before opening {name}…");
                while (IsBuilding || _buildBusy)
                {
                    await Task.Delay(200);
                }
            }

            return await OpenCoreAsync(path, name);
        }
        finally
        {
            SetBusy(ref _openBusy, false);
        }
    }

    private bool _openBusy;

    private async Task<bool> OpenCoreAsync(string path, string name)
    {
        AppendLog($"Opening {name}…");
        SolutionModel solution;
        try
        {
            solution = await SolutionModel.OpenAsync(path, problem => RunOnUi(() => AppendLog($"Skipped {problem}")));
        }
        catch (Exception ex) when (ex is ProjectException or System.ComponentModel.Win32Exception or IOException or XmlException or UnauthorizedAccessException)
        {
            AppendLog(ex.Message);
            RaiseNotify(NotifyKind.Error, $"Couldn't open {name}: MSBuild couldn't read it. Details are in Output.");
            return false;
        }

        foreach (var project in solution.Projects)
        {
            AppendLog($"Project {project.Info.Name}: {project.Framework.Profile?.DisplayName ?? "no UI framework the designer knows"}, {project.Info.TargetFramework}, {project.Info.OutputType}");
        }

        if (solution.DefaultStartup is not { } startup)
        {
            // Nothing to open: the solution open before stays as it was (review M4).
            RaiseNotify(NotifyKind.Warning, $"{name} has no C# projects the designer can read, so it wasn't opened.");
            return false;
        }

        // The startup project chosen last time this solution was open, if it's still in it (review L24).
        if (Settings.StartupProjects.GetValueOrDefault(Path.GetFullPath(path)) is { } remembered && solution.Find(remembered) is { } chosen)
        {
            startup = chosen;
        }

        CloseProject();
        Solution = solution;
        if (!solution.DesignableProjects.Any())
        {
            RaiseNotify(NotifyKind.Warning, solution.Projects.Select(p => p.Framework.Problem(p.Info.Name)).FirstOrDefault(p => p is not null)
                ?? $"{name} has no project with XAML files.");
        }

        SetStartupProject(startup, remember: false);
        _ = WarnAboutPreviewSdkAsync(startup.Info);

        foreach (var doc in Documents)
        {
            if (solution.ProjectContaining(doc.FilePath) is { } owner)
            {
                doc.UseProject(owner.Info.ToOutput());
            }
        }

        if (ProjectReader.MainXamlFile(startup.XamlFiles) is { } main)
        {
            await OpenFileAsync(main);
        }

        WatchProjectOutputs(solution);
        if (!await BuildAsync() && !IsBuilding)
        {
            AppendLog($"{name} wasn't built after opening; build it with Project ▸ Rebuild to show its custom controls.");
        }

        StopUnusedHostsSoon(); // the previous solution's renderers, and the "no project" one used before the build
        return true;
    }

    /// <summary>The project Run starts and Project ▸ Add adds to (Solution Explorer ▸ Set as startup project).</summary>
    /// <param name="remember">The user chose it: it's the startup project the next time this solution opens (review L24).</param>
    public void SetStartupProject(SolutionProject project, bool remember = true)
    {
        Project = project.Info;
        RefreshProjectFiles();
        AppendLog($"Startup project: {project.Label}.");
        if (remember && Solution?.SolutionPath is { } solutionPath)
        {
            Settings.StartupProjects[solutionPath] = project.Info.ProjectPath;
            SaveSettings();
        }
    }

    public void CloseProject()
    {
        foreach (var watcher in _outputWatchers.Values)
        {
            watcher.Dispose();
        }

        _outputWatchers.Clear();
        Solution = null;
        Project = null;
        ProjectFiles = [];
        Diagnostics.Clear(); // the closed solution's errors (review L25)
        ProjectFilesChanged?.Invoke();
    }

    /// <summary>The project a file belongs to, in the open solution.</summary>
    public SolutionProject? ProjectOf(string filePath) => Solution?.ProjectContaining(filePath);

    /// <summary>
    /// Builds the project (saving its unsaved XAML first, so the build matches what's on screen). On success the renderer
    /// restarts with the new assemblies and every document of the project re-renders (§11.2).
    /// </summary>
    /// <param name="saveDocuments">
    /// False for builds the user didn't ask for (packaging a newly added image): unsaved documents stay unsaved; the
    /// designer renders their in-memory version anyway.
    /// </param>
    public async Task<bool> BuildAsync(bool saveDocuments = true)
    {
        // Taken before the first await: a second Build (or Run) pressed while the running app is being stopped must not
        // start a second build of the same project.
        if (Project is not { } project || IsBuilding || _buildBusy)
        {
            return false;
        }

        SetBusy(ref _buildBusy, true);
        using var cancel = new CancellationTokenSource();
        _buildCancel = cancel;
        try
        {
            return await BuildCoreAsync(project, saveDocuments, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            IsBuilding = false;
            AppendBuild("Build cancelled.");
            RaiseNotify(NotifyKind.Warning, "The build was cancelled.");
            return false;
        }
        finally
        {
            _buildCancel = null;
            SetBusy(ref _buildBusy, false);
        }
    }

    private bool _buildBusy;
    private CancellationTokenSource? _buildCancel;

    /// <summary>Project ▸ Cancel build, and closing the designer: stops the running <c>dotnet build</c> (review L23).</summary>
    public void CancelBuild() => _buildCancel?.Cancel();

    /// <summary>A build, a run or an open is under way: Run and Rebuild do nothing until it's over, so they show disabled (review L22).</summary>
    public bool IsBusy => IsBuilding || _buildBusy || _runBusy || _openBusy;

    partial void OnIsBuildingChanged(bool value) => OnPropertyChanged(nameof(IsBusy));

    private void SetBusy(ref bool flag, bool value)
    {
        flag = value;
        OnPropertyChanged(nameof(IsBusy));
    }

    private async Task<bool> BuildCoreAsync(ProjectInfo project, bool saveDocuments, CancellationToken cancellationToken)
    {
        await StopRunForBuildAsync();

        // The whole solution: a document of any project in it may be among those saved, and every project's renderer
        // is refreshed afterwards.
        var solution = Solution;
        var target = solution?.BuildTarget ?? project.ProjectPath;
        bool InBuild(DocumentViewModel d) => solution is not null ? solution.ProjectContaining(d.FilePath) is not null : project.Contains(d.FilePath);

        foreach (var doc in Documents.Where(d => saveDocuments && d.IsDirty && InBuild(d)).ToList())
        {
            if (!CanSaveOverDisk(doc))
            {
                return false; // the user decides between their version and the one on disk first
            }

            try
            {
                doc.Save();
                AppendLog($"Saved {doc.Title} before building.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Built anyway, from the file on disk: say so where it's seen (review L25, §16).
                AppendLog($"Couldn't save {doc.Title} before building: {ex.Message}");
                RaiseNotify(NotifyKind.Warning, $"Couldn't save {doc.Title} ({ex.Message}). The build uses the version on disk, without your latest changes.");
            }
        }

        IsBuilding = true;
        BuildLines.Clear();
        AppendBuild($"Building {Path.GetFileName(target)} ({target})…");
        BuildResult result;
        IReadOnlyList<BuildStep> steps = [];
        var stepResults = new List<BuildResult>();
        try
        {
            // A solution with WinUI projects is built project by project, each with its platform (SolutionModel.BuildSteps).
            steps = solution?.BuildSteps ?? [new BuildStep(project.ProjectPath, project.Platform, project.BuildFramework)];
            foreach (var step in steps)
            {
                if (steps.Count > 1)
                {
                    AppendBuild($"Building {Path.GetFileName(step.Path)}{((step.Platform ?? step.Framework) is { } detail ? $" ({detail})" : "")}…");
                }

                stepResults.Add(await BuildRunner.BuildAsync(step.Path, line => RunOnUi(() => AppendBuild(line)), platform: step.Platform,
                    framework: step.Framework, solution: step.Solution, cancellationToken: cancellationToken));
            }

            result = BuildResult.Combine(stepResults);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            IsBuilding = false;
            AppendBuild($"Couldn't run dotnet build: {ex.Message}");
            RaiseNotify(NotifyKind.Error, "Couldn't run dotnet build. Is the .NET SDK installed?");
            return false;
        }
        finally
        {
            _ownBuildFinished = DateTime.UtcNow;
        }

        IsBuilding = false;
        Diagnostics.Clear();
        foreach (var diagnostic in result.Diagnostics.OrderByDescending(d => d.IsError).Distinct())
        {
            Diagnostics.Add(new DiagnosticRow(diagnostic, Path.GetDirectoryName(target)));
        }

        var seconds = $"{result.Duration.TotalSeconds:0.0} s";
        var errors = result.Errors.ToList();
        var warnings = result.Warnings.Count();
        AppendBuild(result.Succeeded
            ? $"Build succeeded in {seconds} ({warnings} warning{(warnings == 1 ? "" : "s")})."
            : $"Build failed in {seconds}: {errors.Count} error{(errors.Count == 1 ? "" : "s")}.");
        RefreshProjectFiles();

        // Every project with XAML files: its build output (and so its framework version) may be new. When the build failed,
        // still the projects that did build: one broken project mustn't keep the others' renderers out of date (review M1).
        var candidates = solution is not null ? solution.Projects.Where(p => p.XamlFiles.Count > 0).Select(p => p.Info).ToList() : [project];
        var built = result.Succeeded ? candidates : candidates.Where(info => BuiltDespiteFailure(info, steps, stepResults)).ToList();
        var problems = new List<string>();
        var updated = Solution;
        foreach (var info in built)
        {
            if (await ApplyBuildAsync(info) is { } problem)
            {
                problems.Add(problem);
            }

            if (updated?.Find(info.ProjectPath) is not null)
            {
                updated = updated.With(SolutionModel.Describe(info));
            }
        }

        if (updated is not null && !ReferenceEquals(updated, Solution) && ReferenceEquals(Solution, solution))
        {
            Solution = updated; // once, so Solution Explorer is rebuilt once per build (review L18)
        }

        StopUnusedHostsSoon(); // documents may have moved to the new build's renderer

        if (!result.Succeeded)
        {
            var first = errors.FirstOrDefault()?.ToString() ?? "see Output ▸ Build";
            var also = built.Count > 0 ? $" ({string.Join(", ", built.Select(b => b.Name))} built and reloaded.)" : "";
            RaiseNotify(NotifyKind.Error, $"Build failed ({errors.Count} error{(errors.Count == 1 ? "" : "s")}): {first}{also}",
                [new NotifyAction("Show build output", () => ShowBuildOutput?.Invoke())]);
            return false;
        }

        if (problems.Count > 0)
        {
            RaiseNotify(NotifyKind.Error, problems.Count == 1 ? problems[0] : $"{problems[0]} ({problems.Count - 1} more in Output.)");
            problems.Skip(1).ToList().ForEach(AppendLog);
            return built.Count > problems.Count; // a designable project still got its build
        }

        var names = built.Count == 1 ? built[0].Name : $"the {built.Count} projects";
        RaiseNotify(NotifyKind.Success, $"Build succeeded in {seconds}. Custom controls and styles from {names} are up to date.");
        return true;
    }

    /// <summary>
    /// Whether a project got a new build although the whole build failed: its own step succeeded (a solution built project by
    /// project), or its assembly was written since the renderer loaded it (one solution build where another project failed).
    /// </summary>
    private bool BuiltDespiteFailure(ProjectInfo info, IReadOnlyList<BuildStep> steps, IReadOnlyList<BuildResult> results)
    {
        var index = steps.ToList().FindIndex(s => string.Equals(s.Path, info.ProjectPath, StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index < results.Count && steps.Count > 1)
        {
            return results[index].Succeeded;
        }

        return File.Exists(info.TargetPath) && !_hosts.IsCurrent(info.TargetPath);
    }

    /// <summary>The last build's errors and warnings (Output ▸ Error List).</summary>
    public ObservableCollection<DiagnosticRow> Diagnostics { get; } = [];

    /// <summary>The window brings the Build tab forward.</summary>
    public event Action? ShowBuildOutput;

    /// <summary>
    /// After a successful build: rejects frameworks and versions the designer can't render (D5), otherwise restarts the
    /// project's renderer so every document of the project shows the new assemblies. Returns why the build can't be used.
    /// </summary>
    private async Task<string?> ApplyBuildAsync(ProjectInfo project)
    {
        bool Owns(DocumentViewModel d) => Solution?.ProjectContaining(d.FilePath) is { } owner
            ? string.Equals(owner.Info.ProjectPath, project.ProjectPath, StringComparison.OrdinalIgnoreCase)
            : project.Contains(d.FilePath);

        var detected = project.DetectFramework(); // the build, checked against what MSBuild evaluated (UseWinUI)
        var platformProblem = detected.Family == FrameworkFamily.WinUI && project.Platform is { } platform && !string.Equals(platform, "x64", StringComparison.OrdinalIgnoreCase)
            ? $"{project.Name} is built for {platform}, and the designer's WinUI renderer is x64, so it can't load the build. Build it for x64 (or remove its <Platform>) to see its controls."
            : null; // review M25
        if ((detected.Family is not null && detected.Problem(project.Name) is { } problem) || (problem = platformProblem) is not null)
        {
            foreach (var doc in Documents.Where(Owns))
            {
                doc.UseProject(project.ToOutput() with { AssemblyPath = null });
            }

            return $"{problem} Its files are shown without its custom controls and styles.";
        }

        // An up-to-date build (the usual case when a project opens) leaves the assembly as the running renderer loaded
        // it: restarting would only interrupt the first render for nothing.
        if (!_hosts.IsCurrent(project.TargetPath))
        {
            await _hosts.RestartAsync(project.TargetPath);
        }

        foreach (var doc in Documents.Where(Owns))
        {
            doc.UseProject(project.ToOutput());
        }

        return null;
    }

    private void RefreshProjectFiles()
    {
        ProjectFiles = Project is { } p ? ProjectReader.XamlFiles(p.Directory) : [];
        ProjectFilesChanged?.Invoke();
    }

    private async Task WarnAboutPreviewSdkAsync(ProjectInfo project)
    {
        if (await BuildRunner.PreviewSdkVersionAsync(project.Directory) is { } preview)
        {
            RunOnUi(() => AppendLog(
                $"Note: dotnet resolves to the preview SDK {preview} in {project.Directory}. Add a global.json pinning a stable SDK if builds behave oddly."));
        }
    }

    /// <summary>
    /// A build outside the designer (VS Code, Visual Studio, a terminal) replaces the assembly: reload it too. The renderer
    /// loads assemblies from memory, so it never blocks those builds.
    /// </summary>
    /// <summary>Every project of the solution with XAML files, not only the startup project (review M2).</summary>
    private void WatchProjectOutputs(SolutionModel solution)
    {
        foreach (var project in solution.Projects.Where(p => p.XamlFiles.Count > 0))
        {
            WatchProjectOutput(project.Info);
        }
    }

    /// <summary>
    /// Any assembly, resource index or compiled XAML written to the project's output folder: a library rebuilt in place
    /// copies its new dll there without the project's own assembly changing (reference assemblies, review M2).
    /// </summary>
    private void WatchProjectOutput(ProjectInfo project)
    {
        if (_outputWatchers.Remove(project.ProjectPath, out var old))
        {
            old.Dispose();
        }

        if (!_outputTimers.ContainsKey(project.ProjectPath))
        {
            _outputTimers[project.ProjectPath] = CreateTimer(ExternalBuildSettle, () => OnExternalBuild(project.ProjectPath));
        }

        var output = Path.GetDirectoryName(project.TargetPath);
        if (string.IsNullOrEmpty(output))
        {
            return;
        }

        FileSystemWatcher watcher;
        try
        {
            // The project folder (the output folder may not exist before the first build), filtered to the output below.
            watcher = new FileSystemWatcher(project.Directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return;
        }

        var prefix = output + Path.DirectorySeparatorChar;
        void Changed(object sender, FileSystemEventArgs e)
        {
            if (e.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(e.FullPath).ToLowerInvariant() is ".dll" or ".pri" or ".xbf" or ".json")
            {
                RunOnUi(() =>
                {
                    if (_outputTimers.TryGetValue(project.ProjectPath, out var timer))
                    {
                        timer.Stop();
                        timer.Start(); // wait until the build has finished writing
                    }
                });
            }
        }

        watcher.Changed += Changed;
        watcher.Created += Changed;
        watcher.Renamed += (s, e) => Changed(s, e);
        _outputWatchers[project.ProjectPath] = watcher;
    }

    private async void OnExternalBuild(string projectPath)
    {
        if (Solution?.Find(projectPath)?.Info is not { } project || IsBuilding || _buildBusy
            || DateTime.UtcNow - _ownBuildFinished < ExternalBuildSettle + TimeSpan.FromSeconds(1))
        {
            return; // our own build already handled it
        }

        var hasDocuments = Documents.Any(d => string.Equals(Solution?.ProjectContaining(d.FilePath)?.Info.ProjectPath, projectPath, StringComparison.OrdinalIgnoreCase));
        if (!hasDocuments || _hosts.IsCurrent(project.TargetPath))
        {
            return; // nothing open uses it, or nothing the renderer loads changed
        }

        AppendLog($"{project.Name} was rebuilt outside the designer; reloading its controls.");
        if (await ApplyBuildAsync(project) is { } problem)
        {
            RaiseNotify(NotifyKind.Error, problem);
        }
        else
        {
            RaiseNotify(NotifyKind.Success, $"{project.Name} was rebuilt outside the designer. Custom controls and styles reloaded.");
        }
    }

    // ---- open documents: events and file watching (§11.6) ----

    private void Attach(DocumentViewModel document)
    {
        document.EventLogged += AppendEventLog;
        document.HandlerConnected += OnHandlerConnected;
        WatchFile(document);
    }

    private void Detach(DocumentViewModel document)
    {
        document.EventLogged -= AppendEventLog;
        document.HandlerConnected -= OnHandlerConnected;
        if (_fileWatchers.Remove(document, out var watch))
        {
            watch.Watcher.Dispose();
            watch.Timer.Stop();
        }
    }

    private void OnHandlerConnected(Core.Editing.EventHandlerOutcome outcome) => HandlerReported?.Invoke(outcome);

    private void WatchFile(DocumentViewModel document)
    {
        FileSystemWatcher watcher;
        try
        {
            watcher = new FileSystemWatcher(Path.GetDirectoryName(document.FilePath)!, Path.GetFileName(document.FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return;
        }

        var timer = CreateTimer(FileChangeSettle, () => _ = CheckDiskAsync(document));
        void Changed(object sender, FileSystemEventArgs e) => RunOnUi(() =>
        {
            timer.Stop();
            timer.Start(); // editors often write in several steps
        });
        watcher.Changed += Changed;
        watcher.Created += Changed;
        watcher.Deleted += Changed; // deleted, or renamed away (a branch switch): review L19
        watcher.Renamed += (s, e) => Changed(s, e);
        watcher.EnableRaisingEvents = true;
        _fileWatchers[document] = (watcher, timer);
    }

    /// <summary>
    /// The file changed on disk: if it differs from what the designer last loaded or saved, reload silently when there are
    /// no unsaved changes, otherwise ask (D6). The designer's own saves match <see cref="DocumentViewModel.DiskText"/>.
    /// </summary>
    private async Task CheckDiskAsync(DocumentViewModel document)
    {
        if (!Documents.Contains(document))
        {
            return;
        }

        if (!File.Exists(document.FilePath))
        {
            // Saving would quietly create it again (in another branch, say): say so once, and ask before saving (review L19).
            if (!document.MissingOnDisk)
            {
                document.MissingOnDisk = true;
                RaiseNotify(NotifyKind.Warning, $"{document.Title} was deleted or renamed outside the designer. It's still open here; saving it would create it again.",
                [
                    new NotifyAction(document.IsDirty ? "Close it (discard changes)" : "Close it", () => _ = CloseDocumentAsync(document)),
                    new NotifyAction("Keep it open", () => { }),
                ], sticky: true);
            }

            return;
        }

        document.MissingOnDisk = false;
        document.RecreateOnSave = false;
        string? text = null;
        for (var attempt = 0; attempt < 5 && text is null; attempt++)
        {
            try
            {
                text = await File.ReadAllTextAsync(document.FilePath);
            }
            catch (IOException)
            {
                await Task.Delay(150); // still being written
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }

        if (text is null || text == document.DiskText)
        {
            return;
        }

        if (!document.IsDirty)
        {
            await ReloadAsync(document);
            RaiseNotify(NotifyKind.Success, $"{document.Title} was changed outside the designer and has been reloaded.");
            return;
        }

        ShowDiskConflict(document, text);
    }

    private void ShowDiskConflict(DocumentViewModel document, string text) =>
        RaiseNotify(NotifyKind.Warning, $"{document.Title} was changed outside the designer, and you have unsaved changes here.",
        [
            new NotifyAction("Reload (discard my changes)", () => _ = ReloadAsync(document)),
            new NotifyAction("Keep mine", () => document.DiskText = text),
        ], sticky: true);

    /// <summary>
    /// Whether <paramref name="document"/> may be written: not when another program changed the file since it was loaded
    /// or saved and nobody chose yet (the notification asking may have been replaced by another one since). Then the
    /// question is shown again and the file is left alone.
    /// </summary>
    public bool CanSaveOverDisk(DocumentViewModel document)
    {
        if (document.MissingOnDisk && !document.RecreateOnSave && !File.Exists(document.FilePath))
        {
            AppendLog($"Didn't save {document.Title}: it was deleted or renamed outside the designer.");
            RaiseNotify(NotifyKind.Warning, $"{document.Title} was deleted or renamed outside the designer. Save it again where it was?",
            [
                new NotifyAction("Save it again", () =>
                {
                    document.RecreateOnSave = true;
                    if (CanSaveOverDisk(document))
                    {
                        SaveDocument(document);
                    }
                }),
                new NotifyAction(document.IsDirty ? "Close it (discard changes)" : "Close it", () => _ = CloseDocumentAsync(document)),
            ], sticky: true);
            return false;
        }

        if (document.TextChangedOnDisk() is not { } text)
        {
            return true;
        }

        AppendLog($"Didn't save {document.Title}: it was changed outside the designer. Choose Reload or Keep mine first.");
        ShowDiskConflict(document, text);
        return false;
    }

    private void SaveDocument(DocumentViewModel document)
    {
        try
        {
            document.Save();
            AppendLog($"Saved {document.Title}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RaiseNotify(NotifyKind.Error, $"Couldn't save {document.Title}: {ex.Message}");
        }
    }

    /// <summary>Replaces a document with a fresh load of its file in the same tab, keeping zoom, theme and (where possible) the selection.</summary>
    private async Task ReloadAsync(DocumentViewModel old)
    {
        var index = Documents.IndexOf(old);
        if (index < 0)
        {
            return;
        }

        XamlDocument xaml;
        try
        {
            xaml = await Task.Run(() => XamlDocument.Load(old.FilePath));
        }
        catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
        {
            RaiseNotify(NotifyKind.Error, $"Couldn't reload {old.Title}: {ex.Message}");
            return;
        }

        var selectedBefore = old.Selection.Selected.Select(id => old.Xaml.FindById(id)).OfType<ElementNode>().ToList();
        var oldNodes = old.Xaml.AllNodes.ToList();
        var newNodes = xaml.AllNodes.ToList();

        // Tabs may have been closed or moved while the file loaded: find this one again.
        index = Documents.IndexOf(old);
        if (index < 0)
        {
            return;
        }

        var document = new DocumentViewModel(old.FilePath, xaml, old.Project, _hosts) { Zoom = old.Zoom, Theme = old.Theme, PreviewSize = old.PreviewSize };
        document.ShowPreviousFrame(old.Frame);
        var wasSelected = ReferenceEquals(SelectedDocument, old);
        Detach(old);
        Attach(document);
        Documents[index] = document;
        if (wasSelected)
        {
            SelectedDocument = document;
        }

        // Same name first, else the same position (in document order) if it's the same type.
        var restored = selectedBefore.Select(n =>
                (n.Name is { } name ? newNodes.FirstOrDefault(m => m.Name == name) : null)
                ?? (oldNodes.IndexOf(n) is var i and >= 0 && i < newNodes.Count && newNodes[i].TypeName == n.TypeName ? newNodes[i] : null))
            .OfType<ElementNode>().Select(n => n.Id).ToList();
        document.Selection.Set(restored);
        AppendLog($"Reloaded {document.Title} from disk.");
        await old.CloseAsync();
    }

    private void AppendBuild(string line)
    {
        BuildLines.Add(new LogLine(line));
        while (BuildLines.Count > 5000)
        {
            BuildLines.RemoveAt(0);
        }
    }

    private void RaiseNotify(NotifyKind kind, string message, IReadOnlyList<NotifyAction>? actions = null, bool sticky = false) =>
        RunOnUi(() => Notify?.Invoke(new NotifyRequest(kind, message, actions, sticky)));

    private DispatcherQueueTimer CreateTimer(TimeSpan interval, Action tick)
    {
        var timer = _dispatcher.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => tick();
        return timer;
    }
}
