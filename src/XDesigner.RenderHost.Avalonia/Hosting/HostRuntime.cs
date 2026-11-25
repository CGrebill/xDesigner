using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace XDesigner.RenderHost.Hosting;

/// <summary>Options for starting Avalonia inside the render host.</summary>
/// <param name="UserAssemblyPath">The user's built project assembly, or null for "no project" mode.</param>
/// <param name="UseUserApplication">
/// False skips the user's <c>App</c>/<c>BuildAvaloniaApp</c> and uses <see cref="DesignApp"/>.
/// The designer restarts the host this way if the user's app fails to initialise (§16).
/// </param>
internal sealed record HostStartOptions(string? UserAssemblyPath, bool UseUserApplication = true);

/// <summary>
/// Owns Avalonia inside the render host: a dedicated UI thread running the headless platform
/// with Skia rendering, the user's application (styles, theme, fonts) and the user's assemblies.
/// Avalonia can be set up only once per process, so there is at most one instance.
/// See docs/ARCHITECTURE.md §14.1.
/// </summary>
internal sealed class HostRuntime : IDisposable
{
    private static readonly Lock StartLock = new();
    private static HostRuntime? s_current;

    private readonly CancellationTokenSource _shutdown = new();
    private readonly Thread _uiThread;
    private readonly List<string> _warnings = [];
    private TaskCompletionSource? _ready;

    private HostRuntime(HostStartOptions options)
    {
        Options = options;
        _uiThread = new Thread(UiThreadMain) { Name = "Avalonia UI", IsBackground = true };
    }

    public HostStartOptions Options { get; }

    /// <summary>The user's project assembly, when one was given.</summary>
    public Assembly? UserAssembly { get; private set; }

    /// <summary>Name of the application type actually in use (user's or <see cref="DesignApp"/>).</summary>
    public string ApplicationTypeName { get; private set; } = "";

    /// <summary>How the AppBuilder was obtained: <c>BuildAvaloniaApp</c>, <c>App type</c> or <c>DesignApp</c>.</summary>
    public string BuilderSource { get; private set; } = "";

    public bool IsDesignMode { get; private set; }

    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Starts Avalonia on its own thread and waits for setup to finish (or fail).</summary>
    public static HostRuntime Start(HostStartOptions options)
    {
        lock (StartLock)
        {
            if (s_current is not null)
            {
                throw new InvalidOperationException("Avalonia is already running in this process.");
            }

            var runtime = new HostRuntime(options);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime._ready = ready;
            runtime._uiThread.Start();
            ready.Task.GetAwaiter().GetResult(); // rethrows setup failures
            s_current = runtime;
            return runtime;
        }
    }

    /// <summary>Runs <paramref name="work"/> on the Avalonia UI thread (which this runtime owns, so it is an instance member).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Only valid while this runtime's UI thread runs.")]
    public async Task<T> InvokeAsync<T>(Func<T> work) => await Dispatcher.UIThread.InvokeAsync(work);

    public void Dispose()
    {
        _shutdown.Cancel();
        _uiThread.Join(TimeSpan.FromSeconds(5));
    }

    private void UiThreadMain()
    {
        try
        {
            Setup();
            _ready!.SetResult();
        }
        catch (Exception ex)
        {
            _ready!.SetException(ex);
            return;
        }

        Dispatcher.UIThread.MainLoop(_shutdown.Token);
    }

    private void Setup()
    {
        // Before the user's App is constructed, so its code can already see design mode.
        IsDesignMode = DesignModeSwitch.TryEnable();
        if (!IsDesignMode)
        {
            _warnings.Add("Couldn't switch Avalonia into design mode; user controls will behave as at run time.");
        }

        UserAssemblyLoader? loader = null;
        if (Options.UserAssemblyPath is not null)
        {
            loader = UserAssemblyLoader.Load(Options.UserAssemblyPath);
            UserAssembly = loader.MainAssembly;
        }

        var builder = CreateBuilder(loader);
        builder
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        // User code that runs outside a render (a control's timer, an async void handler) must not end the host:
        // without this, its exception leaves MainLoop and the process exits, over and over while the last render lives.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Console.Error.WriteLine($"Unhandled exception in user code (ignored): {e.Exception}");
            e.Handled = true;
        };

        ApplicationTypeName = Application.Current?.GetType().FullName ?? "";
    }

    private AppBuilder CreateBuilder(UserAssemblyLoader? loader)
    {
        if (loader is not null && Options.UseUserApplication)
        {
            // Prefer the user's own BuildAvaloniaApp(): it carries their fonts and options.
            // UseSkia/UseHeadless called afterwards replace its platform choices.
            var buildMethod = loader.FindBuildAvaloniaAppMethod();
            if (buildMethod is not null)
            {
                BuilderSource = "BuildAvaloniaApp";
                return (AppBuilder)buildMethod.Invoke(null, null)!;
            }

            var appType = loader.FindApplicationType();
            if (appType is not null)
            {
                BuilderSource = "App type";
                return AppBuilder.Configure(() => (Application)Activator.CreateInstance(appType)!).WithInterFont();
            }

            _warnings.Add("No Avalonia Application class found in the project; using the default Fluent theme.");
        }

        BuilderSource = nameof(DesignApp);
        return AppBuilder.Configure<DesignApp>().WithInterFont();
    }
}
