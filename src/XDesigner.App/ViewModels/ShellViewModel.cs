using System.Collections.ObjectModel;
using XDesigner.App.Services;
using System.Xml;
using XDesigner.Core.Project;
using XDesigner.Core.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;

namespace XDesigner.App.ViewModels;

/// <summary>The main window's state: open documents, output log, status (§15.1).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "One instance for the life of the process; file watchers are disposed when a project closes.")]
public sealed partial class ShellViewModel : ObservableObject
{
    private const int MaxLogLines = 500;

    private readonly RenderHostManager _hosts;
    private readonly DispatcherQueue _dispatcher;

    public ShellViewModel(RenderHostManager hosts, DispatcherQueue dispatcher)
    {
        _hosts = hosts;
        _dispatcher = dispatcher;
        _hosts.Log += message => RunOnUi(() => AppendLog(message));
        _hosts.HostReady += _ => RunOnUi(UpdateRendererStatus);
        _hosts.HostRestarted += path => RunOnUi(() =>
        {
            foreach (var doc in Documents.Where(d => string.Equals(d.UserAssemblyPath, path, StringComparison.OrdinalIgnoreCase)))
            {
                doc.RendererRestarted();
            }
        });
    }

    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    public ObservableCollection<LogLine> OutputLines { get; } = [];

    /// <summary>Output ▸ Events: every handler created, connected or disconnected (§9.7, D9).</summary>
    public ObservableCollection<LogLine> EventLines { get; } = [];

    /// <summary>A handler was created or connected (or why not), from any document: the window shows the confirmation (D9).</summary>
    public event Action<XDesigner.Core.Editing.EventHandlerOutcome>? HandlerReported;

    [ObservableProperty]
    public partial DocumentViewModel? SelectedDocument { get; set; }

    [ObservableProperty]
    public partial string RendererStatus { get; private set; } = "Renderer not started";

    partial void OnSelectedDocumentChanged(DocumentViewModel? value) => UpdateRendererStatus();

    /// <summary>The status bar names the renderer of the selected document (several may run: one per framework version).</summary>
    private void UpdateRendererStatus()
    {
        if (SelectedDocument is not { } doc)
        {
            return; // keep the last one shown
        }

        RendererStatus = _hosts.TryGetRunning(doc.HostKey) is { } host
            ? doc.Xaml.Profile.DescribeVersion(host.Info.FrameworkVersion)
            : $"{doc.Xaml.Profile.DisplayName} (starting)";
    }

    /// <summary>Opens a XAML file, or switches to it if already open.</summary>
    public async Task<DocumentViewModel?> OpenFileAsync(string path)
    {
        path = Path.GetFullPath(path);
        var existing = Documents.FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectedDocument = existing;
            return existing;
        }

        XamlDocument xaml;
        try
        {
            xaml = await Task.Run(() => XamlDocument.Load(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendLog($"Could not open {path}: {ex.Message}");
            return null;
        }
        catch (XmlException ex)
        {
            AppendLog($"Could not open {Path.GetFileName(path)}: it isn't valid XAML ({ex.Message}).");
            return null;
        }

        var owner = ProjectOf(path);
        var project = owner is not null ? owner.Info.ToOutput() : ProjectOutputLocator.TryFind(path);
        if (owner is not null)
        {
            AppendLog($"Opened {Path.GetFileName(path)} from project {owner.Info.Name}{(project?.AssemblyPath is null ? " (not built yet)" : "")}.");
        }
        else if (project is null)
        {
            AppendLog($"Opened {Path.GetFileName(path)} (no project found; only built-in controls will render).");
        }
        else if (project.AssemblyPath is null)
        {
            AppendLog($"Opened {Path.GetFileName(path)}. Project {Path.GetFileName(project.ProjectPath)} hasn't been built, so its custom controls and styles can't be shown yet.");
        }
        else
        {
            AppendLog($"Opened {Path.GetFileName(path)} using {project.AssemblyPath} (built {File.GetLastWriteTime(project.AssemblyPath):g}).");
        }

        var document = new DocumentViewModel(path, xaml, project, _hosts) { Theme = Settings.PreviewTheme };
        Attach(document);
        Documents.Add(document);
        SelectedDocument = document;
        return document;
    }

    [RelayCommand]
    private async Task CloseDocumentAsync(DocumentViewModel? document)
    {
        document ??= SelectedDocument;
        if (document is null)
        {
            return;
        }

        var index = Documents.IndexOf(document);
        Detach(document);
        Documents.Remove(document);
        if (SelectedDocument is null && Documents.Count > 0)
        {
            SelectedDocument = Documents[Math.Min(index, Documents.Count - 1)];
        }

        await document.CloseAsync();
    }

    [RelayCommand]
    private void ZoomIn() => StepZoom(+1);

    [RelayCommand]
    private void ZoomOut() => StepZoom(-1);

    [RelayCommand]
    private void ResetZoom()
    {
        if (SelectedDocument is { } doc)
        {
            doc.Zoom = 1;
        }
    }

    public void AppendLog(string message)
    {
        foreach (var line in message.Split('\n'))
        {
            OutputLines.Add(new LogLine($"{DateTime.Now:HH:mm:ss}  {line.TrimEnd('\r')}"));
        }

        while (OutputLines.Count > MaxLogLines)
        {
            OutputLines.RemoveAt(0);
        }
    }

    public void AppendEventLog(string message)
    {
        EventLines.Add(new LogLine($"{DateTime.Now:HH:mm:ss}  {message}"));
        while (EventLines.Count > MaxLogLines)
        {
            EventLines.RemoveAt(0);
        }
    }

    [RelayCommand]
    private void ZoomToFit() => SelectedDocument?.RequestFit();

    private void StepZoom(int direction) => SelectedDocument?.StepZoom(direction);

    /// <summary>
    /// An exception nothing else caught (App's last-chance handlers): logged to Output and the error log, and reported, so
    /// the user can save their work instead of losing it to a crash.
    /// </summary>
    public void ReportUnexpected(string context, Exception exception)
    {
        Core.Settings.ErrorLog.Append(context, exception);
        RunOnUi(() =>
        {
            AppendLog($"Unexpected error ({context}): {exception}");
            RaiseNotify(NotifyKind.Error,
                $"Something went wrong: {exception.Message} Your files are still open; save your work. Details are in Output and in {Core.Settings.AppDataPaths.ErrorLog}.");
        });
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.TryEnqueue(() => action());
        }
    }
}
