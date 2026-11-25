using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using XDesigner.App.Services;
using XDesigner.App.ViewModels;
using XDesigner.Core.Settings;

namespace XDesigner.App;

/// <summary>Application entry point for the designer shell.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The render hosts are disposed when the main window closes; the app object lives as long as the process.")]
public partial class App : Application
{
    /// <summary>Held while the designer runs, so the installer can tell it is open and close it first.</summary>
    public const string RunningMutexName = "xDesigner.Running";

    private MainWindow? _mainWindow;
    private RenderHostManager? _renderHosts;
    private ShellViewModel? _shell;
    private Mutex? _runningMutex;
    private bool _windowShown;

    public App()
    {
        InitializeComponent();

        // Last-chance handlers: an error in an event handler shouldn't take open, unsaved documents down with it. Before
        // the window is up there is nothing to keep alive: let the app end (logged) instead of running invisibly.
        UnhandledException += (_, e) =>
        {
            if (_windowShown)
            {
                e.Handled = true;
                Report("UI", e.Exception);
            }
            else
            {
                ErrorLog.Append("startup", e.Exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            Report("background task", e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ErrorLog.Append("fatal", e.ExceptionObject as Exception);
    }

    private void Report(string context, Exception exception)
    {
        if (_shell is { } shell)
        {
            shell.ReportUnexpected(context, exception);
        }
        else
        {
            ErrorLog.Append(context, exception);
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _runningMutex = new Mutex(initiallyOwned: false, RunningMutexName);
        _renderHosts = new RenderHostManager(RenderHostManager.DefaultHostsDirectory);
        var shell = _shell = new ShellViewModel(_renderHosts, DispatcherQueue.GetForCurrentThread());
        shell.LoadSettings();

        _mainWindow = new MainWindow(shell);
        _mainWindow.Closed += async (_, _) =>
        {
            shell.StopRun(); // an app started with Run doesn't outlive the designer
            shell.CancelBuild(); // nor does a dotnet build it started (review L23)
            await _renderHosts.DisposeAsync();
            _runningMutex?.Dispose();
        };
        _mainWindow.Activate();
        _windowShown = true;

        var installed = Core.Frameworks.FrameworkProfile.All.Where(_renderHosts.IsInstalled).ToList();
        shell.AppendLog(installed.Count > 0
            ? $"Renderers installed: {string.Join(", ", installed.Select(p => p.DisplayName))}."
            : $"No renderer is installed in {RenderHostManager.DefaultHostsDirectory}. Reinstall xDesigner, or rebuild the solution.");

        // Files passed on the command line (or "Open with") open as documents.
        foreach (var path in Environment.GetCommandLineArgs().Skip(1).Where(File.Exists))
        {
            await shell.OpenAnyAsync(path);
        }
    }
}
