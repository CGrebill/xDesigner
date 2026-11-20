using System.Reflection;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using XDesigner.RenderHost.Server;

namespace XDesigner.RenderHost.Hosting;

/// <summary>
/// The render host's WinUI Application (§14.5). It has no App.xaml: it knows the WinUI controls library, its own design-id
/// type and the user's types by aggregating their <see cref="IXamlMetadataProvider"/>s, merges the controls library's
/// resources and the user's App.xaml resources, and draws on a <see cref="RenderSurface"/>.
/// The user's own App class never runs here (a process has one Application).
/// </summary>
internal sealed partial class HostApp : Application, IXamlMetadataProvider
{
    private readonly List<IXamlMetadataProvider> _providers = [];
    private readonly RenderHostOptions _options;
    private readonly Func<HostApp, Task<int>> _run;
    private readonly Action<int> _setExitCode;
    private readonly List<string> _warnings = [];
    private RenderSurface? _surface;

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

    /// <summary>The canvas rendered documents are placed on.</summary>
    public Canvas Surface => (_surface ?? throw new InvalidOperationException("The host is not set up.")).Canvas;

    /// <summary>Physical pixels per DIP of the surface's monitor.</summary>
    public double RasterizationScale => _surface?.RasterizationScale ?? 1.0;

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

    /// <inheritdoc cref="RenderSurface.InvokeAsync{T}(Func{T})"/>
    public Task<T> InvokeAsync<T>(Func<T> work) => InvokeTaskAsync(() => Task.FromResult(work()));

    /// <inheritdoc cref="RenderSurface.InvokeTaskAsync{T}(Func{Task{T}})"/>
    public Task<T> InvokeTaskAsync<T>(Func<Task<T>> work) =>
        _surface?.InvokeTaskAsync(work) ?? Task.FromException<T>(new InvalidOperationException("The host is not set up."));

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
        _surface?.Dispose();
        Exit();
    }

    private async Task SetupAsync()
    {
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

        _surface = await RenderSurface.CreateAsync();
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

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => !l.StartsWith("The text associated with this error code", StringComparison.Ordinal)) ?? text;
}
