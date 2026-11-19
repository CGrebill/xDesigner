using System.Collections.ObjectModel;
using System.Xml;
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

    /// <summary>Asks the user to pick one of a solution's projects (set by the window). Null = cancelled.</summary>
    public Func<IReadOnlyList<string>, Task<string?>>? ChooseProject { get; set; }

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

    /// <summary>A solution: its projects that contain XAML files; the user picks when there are several.</summary>
    public async Task OpenSolutionAsync(string solutionPath)
    {
        IReadOnlyList<string> projects;
        try
        {
            projects = ProjectReader.SolutionProjects(solutionPath)
                .Where(p => ProjectReader.XamlFiles(Path.GetDirectoryName(p)!).Count > 0).ToList();
        }
        catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
        {
            AppendLog($"Could not read {Path.GetFileName(solutionPath)}: {ex.Message}");
            return;
        }

        var choice = projects.Count switch
        {
            0 => null,
            1 => projects[0],
            _ => ChooseProject is { } choose ? await choose(projects) : projects[0],
        };
        if (projects.Count == 0)
        {
            RaiseNotify(NotifyKind.Warning, $"{Path.GetFileName(solutionPath)} has no project with .axaml files.");
        }

        if (choice is not null)
        {
            await OpenProjectAsync(choice);
        }
    }

    /// <summary>
    /// Opens a project: evaluates it with MSBuild, lists its XAML files, opens its main window, then builds it so the
    /// renderer can show its custom controls and styles (D11: build on open).
    /// </summary>
    public async Task OpenProjectAsync(string projectPath)
    {
        var name = Path.GetFileName(projectPath);
        AppendLog($"Opening project {name}…");
        ProjectInfo info;
        try
        {
            info = await ProjectReader.ReadAsync(projectPath);
        }
        catch (Exception ex) when (ex is ProjectException or System.ComponentModel.Win32Exception)
        {
            AppendLog(ex.Message);
            RaiseNotify(NotifyKind.Error, $"Couldn't open {name}: MSBuild couldn't read it. Details are in Output.");
            return;
        }

        Project = info;
        AppendLog($"Project {info.Name}: {info.TargetFramework}, {info.OutputType}, output {info.TargetPath}");
        RefreshProjectFiles();
        WatchProjectOutput(info);
        _ = WarnAboutPreviewSdkAsync(info);

        foreach (var doc in Documents.Where(d => info.Contains(d.FilePath)))
        {
            doc.UseProject(info.ToOutput());
        }

        if (ProjectReader.MainXamlFile(ProjectFiles) is { } main)
        {
            await OpenFileAsync(main);
        }

        await BuildAsync();
    }

    public void CloseProject()
    {
        _outputWatcher?.Dispose();
        _outputWatcher = null;
        Project = null;
        ProjectFiles = [];
        ProjectFilesChanged?.Invoke();
    }

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
        if (Project is not { } project || IsBuilding)
        {
            return false;
        }

        await StopRunForBuildAsync();

        foreach (var doc in Documents.Where(d => saveDocuments && d.IsDirty && project.Contains(d.FilePath)).ToList())
        {
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
        AppendBuild($"Building {project.Name} ({project.ProjectPath})…");
        BuildResult result;
        try
        {
            result = await BuildRunner.BuildAsync(project.ProjectPath, line => RunOnUi(() => AppendBuild(line)));
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

        if (!await ApplyBuildAsync(project))
        {
            return false;
        }

        RaiseNotify(NotifyKind.Success, $"Build succeeded in {seconds}. Custom controls and styles from {project.Name} are up to date.");
        return true;
    }

    /// <summary>The window brings the Build tab forward.</summary>
    public event Action? ShowBuildOutput;

    /// <summary>
    /// After a successful build: rejects non-12 Avalonia (D5), otherwise restarts the renderer so every document of the
    /// project shows the new assemblies. False when the build can't be used.
    /// </summary>
    private async Task<bool> ApplyBuildAsync(ProjectInfo project)
    {
        var version = ProjectReader.AvaloniaVersionOf(project.TargetPath);
        if (version is { Major: not Protocol.ProtocolInfo.SupportedAvaloniaMajorVersion })
        {
            RaiseNotify(NotifyKind.Error,
                $"{project.Name} uses Avalonia {version.ToString(3)}. The designer supports Avalonia 12 only; its files are shown with the default theme and without its custom controls.");
            foreach (var doc in Documents.Where(d => project.Contains(d.FilePath)))
            {
                doc.UseProject(project.ToOutput() with { AssemblyPath = null });
            }

            return false;
        }

        // An up-to-date build (the usual case when a project opens) leaves the assembly as the running renderer loaded
        // it: restarting would only interrupt the first render for nothing.
        if (!_hosts.IsCurrent(project.TargetPath))
        {
            await _hosts.RestartAsync(project.TargetPath);
        }

        foreach (var doc in Documents.Where(d => project.Contains(d.FilePath)))
        {
            doc.UseProject(project.ToOutput());
        }

        return true;
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
        if (await ApplyBuildAsync(project))
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

        RaiseNotify(NotifyKind.Warning, $"{document.Title} was changed outside the designer, and you have unsaved changes here.",
        [
            new NotifyAction("Reload (discard my changes)", () => _ = ReloadAsync(document)),
            new NotifyAction("Keep mine", () => document.DiskText = text),
        ], sticky: true);
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
