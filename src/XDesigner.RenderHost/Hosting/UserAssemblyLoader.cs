using System.Reflection;
using System.Runtime.Loader;

namespace XDesigner.RenderHost.Hosting;

/// <summary>
/// Loads the user's built project assembly and resolves its dependencies (NuGet packages,
/// referenced projects, native libraries) from the user's <c>deps.json</c>.
/// </summary>
/// <remarks>
/// Assemblies go into the default load context, not a custom one: the host process is
/// restarted on every rebuild, so unloading is never needed, and the runtime XAML compiler
/// emits code that must see user types and Avalonia types from the same context.
/// Anything the host already has (Avalonia, Skia, the BCL) is never loaded a second time,
/// because the default context only asks <see cref="AssemblyLoadContext.Resolving"/> for
/// assemblies it cannot find itself. See docs/ARCHITECTURE.md §14.1.
/// </remarks>
internal sealed class UserAssemblyLoader
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _directory;

    private UserAssemblyLoader(string assemblyPath)
    {
        _resolver = new AssemblyDependencyResolver(assemblyPath);
        _directory = Path.GetDirectoryName(assemblyPath)!;
    }

    public Assembly MainAssembly { get; private set; } = null!;

    public static UserAssemblyLoader Load(string assemblyPath)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"User assembly not found: {fullPath}", fullPath);
        }

        var loader = new UserAssemblyLoader(fullPath);
        AssemblyLoadContext.Default.Resolving += loader.OnResolving;
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += loader.OnResolvingUnmanagedDll;
        loader.MainAssembly = loader.Load(AssemblyLoadContext.Default, fullPath);
        return loader;
    }

    /// <summary>
    /// Assemblies from the project's output folder are loaded from memory, not from the file, so they stay unlocked:
    /// a build (in the designer, or in VS Code / Visual Studio while the designer is open) can overwrite them (§11.2).
    /// Package assemblies resolved from the NuGet cache never change, so they load from their path as usual.
    /// </summary>
    private Assembly Load(AssemblyLoadContext context, string path)
    {
        if (!Path.GetFullPath(path).StartsWith(_directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return context.LoadFromAssemblyPath(path);
        }

        using var assembly = new MemoryStream(File.ReadAllBytes(path));
        var pdb = Path.ChangeExtension(path, ".pdb");
        if (File.Exists(pdb))
        {
            using var symbols = new MemoryStream(File.ReadAllBytes(pdb)); // line numbers in the user's stack traces
            return context.LoadFromStream(assembly, symbols);
        }

        return context.LoadFromStream(assembly);
    }

    /// <summary>
    /// Finds the user's <c>Application</c> subclass: prefers one named <c>App</c>, otherwise the only one.
    /// </summary>
    public Type? FindApplicationType()
    {
        var candidates = SafeGetTypes(MainAssembly)
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(Avalonia.Application).IsAssignableFrom(t))
            .ToList();

        return candidates.FirstOrDefault(t => t.Name == "App") ?? (candidates.Count == 1 ? candidates[0] : null);
    }

    /// <summary>Finds a static <c>BuildAvaloniaApp()</c> returning <see cref="Avalonia.AppBuilder"/>, as the Avalonia templates generate.</summary>
    public MethodInfo? FindBuildAvaloniaAppMethod() =>
        SafeGetTypes(MainAssembly)
            .Select(t => t.GetMethod("BuildAvaloniaApp", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes))
            .FirstOrDefault(m => m is not null && m.ReturnType == typeof(Avalonia.AppBuilder));

    internal static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    private Assembly? OnResolving(AssemblyLoadContext context, AssemblyName name)
    {
        var path = _resolver.ResolveAssemblyToPath(name);
        if (path is null)
        {
            // Not listed in deps.json (e.g. a project built without one): try next to the main assembly.
            var candidate = Path.Combine(_directory, name.Name + ".dll");
            path = File.Exists(candidate) ? candidate : null;
        }

        return path is null ? null : Load(context, path);
    }

    private IntPtr OnResolvingUnmanagedDll(Assembly assembly, string name)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(name);
        return path is null ? IntPtr.Zero : System.Runtime.InteropServices.NativeLibrary.Load(path);
    }
}
