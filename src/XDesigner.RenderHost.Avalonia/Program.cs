using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using XDesigner.Protocol;
using XDesigner.Protocol.Rendering;
using XDesigner.RenderHost.Hosting;
using XDesigner.RenderHost.Rendering;
using XDesigner.RenderHost.Server;

namespace XDesigner.RenderHost;

internal static class Program
{
    public static int Main(string[] args)
    {
        RenderHostOptions options;
        try
        {
            options = RenderHostOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            return RenderHostExitCodes.BadArguments;
        }

        if (options.ShowVersion)
        {
            Console.WriteLine($"XDesigner.RenderHost protocol {ProtocolInfo.Version}, Avalonia {HostVersions.AvaloniaVersion}");
            return RenderHostExitCodes.Ok;
        }

        if (options.RenderFile is not null)
        {
            return RenderOnce(options);
        }

        if (options.PipeName is null)
        {
            PrintUsage();
            return RenderHostExitCodes.BadArguments;
        }

        return Serve(options);
    }

    /// <summary>Server mode: start Avalonia, then serve the designer app over the named pipe.</summary>
    private static int Serve(RenderHostOptions options)
    {
        // The server watches the designer once it runs; until then (the project's App may hang while it's created),
        // a designer that goes away ends this process here.
        using var startup = RenderHostServer.ExitIfParentExitsDuringStartup(options.ParentProcessId);

        HostRuntime runtime;
        try
        {
            runtime = HostRuntime.Start(new HostStartOptions(options.UserAssemblyPath, !options.NoUserApp));
        }
        catch (Exception ex)
        {
            // Before the pipe exists: the app sees the exit code and can retry with --no-user-app.
            Console.Error.WriteLine($"Setup failed: {ex}");
            return RenderHostExitCodes.SetupFailed;
        }
        finally
        {
            startup?.Started();
        }

        using (runtime)
        {
            var service = new RenderHostService(runtime);
            return RenderHostServer.RunAsync(options, service, service.ShutdownRequested).GetAwaiter().GetResult();
        }
    }

    /// <summary>Renders one XAML file to a PNG plus a <c>.layout.json</c>. Used for manual checks and integration tests.</summary>
    private static int RenderOnce(RenderHostOptions options)
    {
        HostRuntime runtime;
        try
        {
            runtime = HostRuntime.Start(new HostStartOptions(options.UserAssemblyPath, !options.NoUserApp));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Setup failed: {ex}");
            return RenderHostExitCodes.SetupFailed;
        }

        using (runtime)
        {
            Console.WriteLine($"Application: {runtime.ApplicationTypeName} (via {runtime.BuilderSource}); design mode: {runtime.IsDesignMode}");
            foreach (var warning in runtime.Warnings)
            {
                Console.WriteLine($"Warning: {warning}");
            }

            var request = new RenderRequest(1, File.ReadAllText(options.RenderFile!), options.Scale, options.Theme, options.Size);
            var renderer = new XamlRenderer(runtime.UserAssembly);
            var result = runtime.InvokeAsync(() => renderer.Render(request)).GetAwaiter().GetResult();
            return RenderOnceOutput.Write(result, options.OutputPath!);
        }
    }

    private static void PrintUsage() => Console.Error.WriteLine(
        """
        Usage:
          XDesigner.RenderHost.Avalonia12 --pipe <name> [--assembly <path>] [--parent-pid <pid>] [--no-user-app]
          XDesigner.RenderHost.Avalonia12 --render <file.axaml> --out <file.png> [--assembly <path>] [--scale <n>] [--theme Light|Dark] [--size <w>x<h>] [--no-user-app]
          XDesigner.RenderHost.Avalonia12 --version
        (XDesigner.RenderHost.Avalonia11 takes the same arguments.)
        """);
}
