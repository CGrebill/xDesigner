using System.Reflection;
using XDesigner.RenderHost.Server;

namespace XDesigner.RenderHost.Hosting;

/// <summary>
/// The MAUI render host's WinUI Application (§14.6). It starts MAUI with the project's own <c>MauiProgram.CreateMauiApp()</c>,
/// so the project's App (styles, resources), fonts and handlers are what draws the pages. MAUI then opens the App's main
/// window, which <see cref="WindowHider"/> keeps invisible and inactive; its <see cref="IMauiContext"/> is what the pages
/// are turned into WinUI elements with, on a <see cref="RenderSurface"/>.
/// </summary>
public partial class HostApp : MauiWinUIApplication
{
    private readonly RenderHostOptions _options;
    private readonly Func<HostApp, Task<int>> _run;
    private readonly Action<int> _setExitCode;
    private readonly List<string> _warnings = [];
    private RenderSurface? _surface;

    internal HostApp(RenderHostOptions options, Func<HostApp, Task<int>> run, Action<int> setExitCode)
    {
        _options = options;
        _run = run;
        _setExitCode = setExitCode;
        InitializeComponent();

        // User code that throws outside a render (a control's timer, an async void handler) must not end the host.
        UnhandledException += (_, e) =>
        {
            Console.Error.WriteLine($"Unhandled exception in user code (ignored): {e.Exception}");
            e.Handled = true;
        };
    }

    /// <summary>The user's project assembly, when one was given.</summary>
    public Assembly? UserAssembly { get; private set; }

    /// <summary>How MAUI was set up: <c>MauiProgram</c> (the project's) or <c>Default app</c>.</summary>
    public string BuilderSource { get; private set; } = "Default app";

    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>The window-scoped MAUI context pages are drawn with.</summary>
    public IMauiContext MauiContext { get; private set; } = null!;

    /// <summary>The canvas rendered pages are placed on.</summary>
    internal Microsoft.UI.Xaml.Controls.Canvas Surface => (_surface ?? throw new InvalidOperationException("The host is not set up.")).Canvas;

    /// <summary>Physical pixels per DIP of the surface's monitor.</summary>
    internal double RasterizationScale => _surface?.RasterizationScale ?? 1.0;

    /// <summary>Runs <paramref name="work"/> on the UI thread, after the work queued before it (<see cref="RenderSurface.InvokeTaskAsync{T}"/>).</summary>
    internal Task<T> InvokeAsync<T>(Func<T> work) => InvokeTaskAsync(() => Task.FromResult(work()));

    /// <inheritdoc cref="RenderSurface.InvokeTaskAsync{T}(Func{Task{T}})"/>
    internal Task<T> InvokeTaskAsync<T>(Func<Task<T>> work) =>
        _surface?.InvokeTaskAsync(work) ?? Task.FromException<T>(new InvalidOperationException("The host is not set up."));

    protected override MauiApp CreateMauiApp()
    {
        if (_options.UserAssemblyPath is { } assemblyPath)
        {
            UserAssembly = Assembly.LoadFrom(assemblyPath);
            LoadLibraries(UserAssembly);
            if (!_options.NoUserApp)
            {
                // One type that fails to load (a missing dependency) mustn't lose the project's App (review L45).
                var create = XDesigner.RenderHost.Metadata.MetadataProvider.SafeGetTypes(UserAssembly)
                    .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    .FirstOrDefault(m => m.Name == "CreateMauiApp" && m.ReturnType == typeof(MauiApp) && m.GetParameters().Length == 0);
                if (create is not null)
                {
                    BuilderSource = "MauiProgram";
                    return (MauiApp)create.Invoke(null, null)!;
                }

                _warnings.Add("No MauiProgram.CreateMauiApp() found in the project; using the default look.");
            }
        }

        return MauiApp.CreateBuilder().UseMauiApp<DesignApp>().Build();
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            // For the host's lifetime: windows the project opens later stay hidden too (review M29).
            _windowHider = WindowHider.Install();
            try
            {
                base.OnLaunched(args); // creates the project's App and its main window (hidden)
            }
            catch (Exception ex) when (Microsoft.Maui.Controls.Application.Current is { Windows.Count: 0 } app)
            {
                // The App (and its resources, styles, fonts) exists, but its main page failed at design time (a service it
                // needs isn't there): a window of our own gives the MAUI context instead of losing the App (review M28).
                _warnings.Add($"The project's main page couldn't be created in the designer ({(ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message)}); its App's styles and fonts are still used.");
                app.OpenWindow(new Microsoft.Maui.Controls.Window(new ContentPage()));
            }

            var window = Microsoft.Maui.Controls.Application.Current?.Windows is { Count: > 0 } windows
                ? windows[0]
                : throw new InvalidOperationException("The project's App opened no window.");
            MauiContext = window.Handler?.MauiContext ?? throw new InvalidOperationException("The project's window has no MAUI context.");

            // Its page goes: only its context is needed, and the project's MainPage would otherwise keep running unseen
            // (polling, animating, playing media) for the host's lifetime (review M28).
            window.Page = new ContentPage();
            _surface = await RenderSurface.CreateAsync();
        }
        catch (Exception ex)
        {
            // Before the pipe exists: the app sees the exit code and can retry with --no-user-app.
            Console.Error.WriteLine($"Setup failed: {(ex is TargetInvocationException { InnerException: { } inner } ? inner : ex)}");
            Finish(RenderHostExitCodes.SetupFailed);
            return;
        }

        int exitCode;
        try
        {
            exitCode = await _run(this);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            exitCode = RenderHostExitCodes.RenderFailed;
        }

        Finish(exitCode);
    }

    /// <summary>
    /// The project's own libraries in the app folder, loaded now: one nothing has used yet would load only when the XAML
    /// loader meets it, after the sanitizer looked for its types, and be missing from the toolbox (review M24).
    /// </summary>
    private static void LoadLibraries(Assembly userAssembly)
    {
        foreach (var reference in userAssembly.GetReferencedAssemblies())
        {
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, reference.Name + ".dll")))
            {
                try
                {
                    Assembly.Load(reference);
                }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    Console.Error.WriteLine($"Couldn't load {reference.Name}: {ex.Message}");
                }
            }
        }
    }

    private IDisposable? _windowHider;

    private void Finish(int exitCode)
    {
        _setExitCode(exitCode);
        _surface?.Dispose();
        _windowHider?.Dispose();
        Exit();
    }
}
