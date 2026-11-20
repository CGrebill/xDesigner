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
    private FileSystemWatcher? _outputWatcher;
    private DispatcherQueueTimer? _outputTimer;
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
        Project is { } project ? Solution?.Find(project.ProjectPath)?.Framework.Profile ?? project.DetectFramework().Profile : null;

    /// <summary>Opens a <c>.csproj</c>, <c>.sln</c>/<c>.slnx</c> or a XAML file.</summary>
    public async Task OpenAnyAsync(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".csproj":
                await OpenProjectAsync(path);
                if (Project is { } project && string.Equals(project.ProjectPath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                {
                    Remember(path);
                }

                break;
            case ".sln" or ".slnx":
                await OpenSolutionAsync(path);
                if (Project is not null)
                {
                    Remember(path);
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
    private async Task OpenAsync(string path)
    {
        var name = Path.GetFileName(path);
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
            return;
        }

        foreach (var project in solution.Projects)
        {
            AppendLog($"Project {project.Info.Name}: {project.Framework.Profile?.DisplayName ?? "no UI framework the designer knows"}, {project.Info.TargetFramework}, {project.Info.OutputType}");
        }

        Solution = solution;
        if (solution.DefaultStartup is not { } startup)
        {
            RaiseNotify(NotifyKind.Warning, $"{name} has no C# projects the designer can read.");
            return;
        }

        if (!solution.DesignableProjects.Any())
        {
            RaiseNotify(NotifyKind.Warning, solution.Projects.Select(p => p.Framework.Problem(p.Info.Name)).FirstOrDefault(p => p is not null)
                ?? $"{name} has no project with XAML files.");
        }

        SetStartupProject(startup);
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

        await BuildAsync();
    }

    /// <summary>The project Run starts and Project ▸ Add adds to (Solution Explorer ▸ Set as startup project).</summary>
    public void SetStartupProject(SolutionProject project)
    {
        Project = project.Info;
        RefreshProjectFiles();
        WatchProjectOutput(project.Info);
        AppendLog($"Startup project: {project.Label}.");
    }

    public void CloseProject()
    {
        _outputWatcher?.Dispose();
        _outputWatcher = null;
        Solution = null;
        Project = null;
        ProjectFiles = [];
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

        _buildBusy = true;
        try
        {
            return await BuildCoreAsync(project, saveDocuments);
        }
        finally
        {
            _buildBusy = false;
        }
    }

    private bool _buildBusy;

    private async Task<bool> BuildCoreAsync(ProjectInfo project, bool saveDocuments)
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
                AppendLog($"Could not save {doc.Title} before building: {ex.Message}");
            }
        }

        IsBuilding = true;
        BuildLines.Clear();
        AppendBuild($"Building {Path.GetFileName(target)} ({target})…");
        BuildResult result;
        try
        {
            // A solution with WinUI projects is built project by project, each with its platform (SolutionModel.BuildSteps).
            var steps = solution?.BuildSteps ?? [new BuildStep(project.ProjectPath, project.Platform)];
            var results = new List<BuildResult>();
            foreach (var step in steps)
            {
                if (steps.Count > 1)
                {
                    AppendBuild($"Building {Path.GetFileName(step.Path)}{(step.Platform is { } p ? $" ({p})" : "")}…");
                }

                results.Add(await BuildRunner.BuildAsync(step.Path, line => RunOnUi(() => AppendBuild(line)), platform: step.Platform));
            }

            result = BuildResult.Combine(results);
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
            Diagnostics.Add(new DiagnosticRow(diagnostic));
        }

        var seconds = $"{result.Duration.TotalSeconds:0.0} s";
        var errors = result.Errors.ToList();
        var warnings = result.Warnings.Count();
        AppendBuild(result.Succeeded
            ? $"Build succeeded in {seconds} ({warnings} warning{(warnings == 1 ? "" : "s")})."
            : $"Build failed in {seconds}: {errors.Count} error{(errors.Count == 1 ? "" : "s")}.");
        RefreshProjectFiles();

        if (!result.Succeeded)
        {
            var first = errors.FirstOrDefault()?.ToString() ?? "see Output ▸ Build";
            RaiseNotify(NotifyKind.Error, $"Build failed ({errors.Count} error{(errors.Count == 1 ? "" : "s")}): {first}",
                [new NotifyAction("Show build output", () => ShowBuildOutput?.Invoke())]);
            return false;
        }

        // Every project with XAML files: its build output (and so its framework version) may be new.
        var built = solution is not null ? solution.Projects.Where(p => p.XamlFiles.Count > 0).Select(p => p.Info).ToList() : [project];
        var problems = new List<string>();
        foreach (var info in built)
        {
            if (await ApplyBuildAsync(info) is { } problem)
            {
                problems.Add(problem);
            }

            if (Solution is { } current && current.Find(info.ProjectPath) is not null)
            {
                Solution = current.With(SolutionModel.Describe(info));
            }
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

        var detected = FrameworkDetector.FromBuildOutput(project.TargetPath);
        if (detected.Family is not null && detected.Problem(project.Name) is { } problem)
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
    private void WatchProjectOutput(ProjectInfo project)
    {
        _outputWatcher?.Dispose();
        _outputTimer ??= CreateTimer(ExternalBuildSettle, OnExternalBuild);
        try
        {
            _outputWatcher = new FileSystemWatcher(project.Directory, Path.GetFileName(project.TargetPath))
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

        void Changed(object sender, FileSystemEventArgs e)
        {
            if (string.Equals(e.FullPath, project.TargetPath, StringComparison.OrdinalIgnoreCase))
            {
                RunOnUi(() =>
                {
                    _outputTimer!.Stop();
                    _outputTimer.Start(); // wait until the build has finished writing
                });
            }
        }

        _outputWatcher.Changed += Changed;
        _outputWatcher.Created += Changed;
        _outputWatcher.Renamed += (s, e) => Changed(s, e);
    }

    private async void OnExternalBuild()
    {
        if (Project is not { } project || IsBuilding || DateTime.UtcNow - _ownBuildFinished < ExternalBuildSettle + TimeSpan.FromSeconds(1))
        {
            return; // our own build already handled it
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
        if (document.TextChangedOnDisk() is not { } text)
        {
            return true;
        }

        AppendLog($"Didn't save {document.Title}: it was changed outside the designer. Choose Reload or Keep mine first.");
        ShowDiskConflict(document, text);
        return false;
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
