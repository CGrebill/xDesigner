using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using XDesigner.RenderHost.Server;

namespace XDesigner.RenderHost.Hosting;

/// <summary>
/// The render host's WinUI Application (§14.5). It has no App.xaml: it knows the WinUI controls library, its own design-id
/// type and the user's types by aggregating their <see cref="IXamlMetadataProvider"/>s, merges the controls library's
/// resources and the user's App.xaml resources, and owns the cloaked window every document renders in.
/// The user's own App class never runs here (a process has one Application).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The Application lives as long as the process.")]
internal sealed partial class HostApp : Application, IXamlMetadataProvider
{
    private const int DwmwaCloak = 13;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;

    private readonly List<IXamlMetadataProvider> _providers = [];
    private readonly RenderHostOptions _options;
    private readonly Func<HostApp, Task<int>> _run;
    private readonly Action<int> _setExitCode;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<string> _warnings = [];
    private DispatcherQueue? _dispatcher;
    private Window? _window;
    private Canvas? _surface;

    /// <param name="run">What to do once set up (serve the pipe, or render once); returns the exit code.</param>
    public HostApp(RenderHostOptions options, Func<HostApp, Task<int>> run, Action<int> setExitCode)
    {
        _options = options;
        _run = run;
        _setExitCode = setExitCode;

        // User code that throws outside a render (a control's timer, an async void handler) must not end the host.
        UnhandledException += (_, e) =>
        {
            Console.Error.WriteLine($"Unhandled exception in user code (ignored): {e.Exception}");
            e.Handled = true;
        };
    }

    /// <summary>The user's project assembly, when one was given.</summary>
    public Assembly? UserAssembly { get; private set; }

    /// <summary>How the user's resources were applied: <c>App.xaml resources</c> or <c>Default theme</c>.</summary>
    public string BuilderSource { get; private set; } = "Default theme";

    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>The canvas inside the cloaked window that rendered documents are placed on (it may be smaller than they are).</summary>
    public Canvas Surface => _surface ?? throw new InvalidOperationException("The host is not set up.");

    /// <summary>Physical pixels per DIP of the cloaked window's monitor.</summary>
    public double RasterizationScale => _surface?.XamlRoot?.RasterizationScale is > 0 and var scale ? scale : 1.0;

    public IXamlType GetXamlType(Type type)
    {
        foreach (var provider in _providers)
        {
            if (provider.GetXamlType(type) is { } xamlType)
            {
                return xamlType;
            }
        }

        return null!;
    }

    public IXamlType GetXamlType(string fullName)
    {
        foreach (var provider in _providers)
        {
            if (provider.GetXamlType(fullName) is { } xamlType)
            {
                return xamlType;
            }
        }

        return null!;
    }

    public XmlnsDefinition[] GetXmlnsDefinitions() => _providers.SelectMany(p => p.GetXmlnsDefinitions()).ToArray();

    /// <summary>
    /// Runs <paramref name="work"/> on the UI thread, one piece of work at a time (a render awaits the GPU in between, and
    /// nothing else may touch the tree meanwhile).
    /// </summary>
    public Task<T> InvokeAsync<T>(Func<T> work) => InvokeTaskAsync(() => Task.FromResult(work()));

    /// <inheritdoc cref="InvokeAsync{T}(Func{T})"/>
    public Task<T> InvokeTaskAsync<T>(Func<Task<T>> work)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _dispatcher?.TryEnqueue(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                result.TrySetResult(await work());
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
            finally
            {
                _gate.Release();
            }
        });
        if (queued != true)
        {
            result.TrySetException(new InvalidOperationException("The renderer's UI thread has stopped."));
        }

        return result.Task;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            await SetupAsync();
        }
        catch (Exception ex)
        {
            // Before the pipe exists: the app sees the exit code and can retry with --no-user-app.
            Console.Error.WriteLine($"Setup failed: {ex}");
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

    private void Finish(int exitCode)
    {
        _setExitCode(exitCode);
        try
        {
            _window?.Close();
        }
        catch
        {
            // Exiting anyway.
        }

        Exit();
    }

    private async Task SetupAsync()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _providers.Add(new Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider());
        // This assembly's generated provider (it knows DesignId). Found by reflection: naming the generated type in code
        // breaks the XAML compiler's first pass, which compiles this code before that type exists.
        if (FindProvider(typeof(HostApp).Assembly) is { } own)
        {
            _providers.Add(own);
        }
        else
        {
            _warnings.Add("The renderer's own XAML type information is missing; selection and layout will not work.");
        }

        Resources.MergedDictionaries.Add(new XamlControlsResources());

        if (_options.UserAssemblyPath is { } assemblyPath)
        {
            UserAssembly = Assembly.LoadFrom(assemblyPath);
            if (FindProvider(UserAssembly) is { } user)
            {
                _providers.Add(user);
            }
            else
            {
                _warnings.Add($"{UserAssembly.GetName().Name} has no XAML type information; its own controls can't be shown.");
            }
        }

        if (!_options.NoUserApp && _options.ProjectDirectory is { } projectDirectory)
        {
            LoadUserAppResources(Path.Combine(projectDirectory, "App.xaml"));
        }

        await CreateWindowAsync();
    }

    /// <summary>The generated XAML metadata provider of an assembly (not its App class, which also implements the interface).</summary>
    private static IXamlMetadataProvider? FindProvider(Assembly assembly)
    {
        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }

        var provider = types.FirstOrDefault(t => t is { IsAbstract: false, IsInterface: false }
            && typeof(IXamlMetadataProvider).IsAssignableFrom(t)
            && !typeof(Application).IsAssignableFrom(t)
            && t.GetConstructor(Type.EmptyTypes) is not null);
        return provider is null ? null : (IXamlMetadataProvider)Activator.CreateInstance(provider)!;
    }

    /// <summary>
    /// The <c>&lt;Application.Resources&gt;</c> of the project's App.xaml, loaded on their own: styles, brushes and merged
    /// dictionaries the documents use. The controls library's resources are already merged, so that entry is skipped.
    /// </summary>
    private void LoadUserAppResources(string appXamlPath)
    {
        if (!File.Exists(appXamlPath))
        {
            return;
        }

        try
        {
            var document = XDocument.Load(appXamlPath);
            var resources = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Application.Resources")?.Elements().FirstOrDefault();
            if (resources is null)
            {
                return;
            }

            foreach (var declaration in document.Root!.Attributes().Where(a => a.IsNamespaceDeclaration || a.Name.LocalName == "Ignorable"))
            {
                if (resources.Attribute(declaration.Name) is null)
                {
                    resources.SetAttributeValue(declaration.Name, declaration.Value);
                }
            }

            foreach (var controls in resources.DescendantsAndSelf().Where(e => e.Name.LocalName == "XamlControlsResources").ToList())
            {
                controls.Remove();
            }

            if (XamlReader.Load(resources.ToString()) is ResourceDictionary dictionary)
            {
                Resources.MergedDictionaries.Add(dictionary);
                BuilderSource = "App.xaml resources";
            }
        }
        catch (Exception ex)
        {
            _warnings.Add($"The project's App.xaml resources could not be loaded, so documents use the default theme: {FirstLine(ex.Message)}");
        }
    }

    /// <summary>A cloaked tool window (never seen, no taskbar button, never activated) holding a canvas to render on.</summary>
    private async Task CreateWindowAsync()
    {
        _surface = new Canvas();
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _surface.Loaded += (_, _) => loaded.TrySetResult();

        _window = new Window { Title = "xDesigner renderer", Content = _surface };
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        var cloak = 1;
        _ = DwmSetWindowAttribute(hwnd, DwmwaCloak, ref cloak, sizeof(int));
        _ = SetWindowLongPtr(hwnd, GwlExStyle, GetWindowLongPtr(hwnd, GwlExStyle) | WsExToolWindow | WsExNoActivate);
        _window.AppWindow.IsShownInSwitchers = false;
        _window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(800, 600));
        _window.AppWindow.Show(activateWindow: false);

        if (await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(10))) != loaded.Task)
        {
            throw new InvalidOperationException("The renderer's window did not load.");
        }
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => !l.StartsWith("The text associated with this error code", StringComparison.Ordinal)) ?? text;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
