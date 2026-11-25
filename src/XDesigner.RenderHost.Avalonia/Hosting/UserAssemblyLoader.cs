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
        loader.LoadLibraries();
        return loader;
    }

    /// <summary>
    /// The project's own libraries (referenced assemblies in its output folder), loaded now: a library nothing has used yet
    /// is otherwise loaded only when the XAML loader meets it, after the sanitizer looked for its types, so its controls'
    /// events stayed in the copy and it was missing from the toolbox (review L11).
    /// </summary>
    private void LoadLibraries()
    {
        foreach (var reference in MainAssembly.GetReferencedAssemblies())
        {
            if (!File.Exists(Path.Combine(_directory, reference.Name + ".dll")))
            {
                continue; // the framework's, or a package resolved from the NuGet cache when it's needed
            }

            try
            {
                AssemblyLoadContext.Default.LoadFromAssemblyName(reference);
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                Console.Error.WriteLine($"Couldn't load {reference.Name}: {ex.Message}");
            }
        }
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
        // A version the host already has another of (a project on Avalonia 12.2, the host on 12.1): loading the project's
        // copy beside it fails ("already loaded"), so the loaded one is used; the designer warns about the versions (review M19).
        if (context.Assemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase)) is { } loaded)
        {
            Console.Error.WriteLine($"Using the renderer's {name.Name} {loaded.GetName().Version} for the project's {name.Version}.");
            return loaded;
        }

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
        return path is null ? IntPtr.Zero : System.Runtime.InteropServices.NativeLibrary.Load(NativeCopy(path));
    }

    /// <summary>
    /// A copy of a native library in this process's own temporary folder: a loaded native library can't be overwritten, so
    /// loading it from the build output would make the next build fail with "file in use" (§11.2, review L14).
    /// </summary>
    private static string NativeCopy(string path)
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "xDesigner-native");
            var folder = Path.Combine(root, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!Directory.Exists(folder))
            {
                RemoveExitedProcessesCopies(root);
            }

            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, Path.GetFileName(path));
            if (!File.Exists(copy))
            {
                File.Copy(path, copy);
            }

            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return path; // loaded in place, as before
        }
    }

    /// <summary>The copies of renderers that have exited (a loaded library can't be deleted by its own process).</summary>
    private static void RemoveExitedProcessesCopies(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if (int.TryParse(Path.GetFileName(folder), out var pid) && !IsRunning(pid))
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still in use (a renderer with that id again): next time.
                }
            }
        }

        static bool IsRunning(int pid)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
