using System.Reflection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using XDesigner.Protocol;
using XDesigner.Protocol.Rendering;
using XDesigner.RenderHost.Hosting;
using XDesigner.RenderHost.Rendering;
using XDesigner.RenderHost.Server;

namespace XDesigner.RenderHost;

internal static class Program
{
    /// <summary>The Windows App SDK version this host ships with (self-contained, so it is also the one it runs).</summary>
    public static string WindowsAppSdkVersion { get; } =
        typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "WindowsAppSdkVersion")?.Value ?? "";

    [STAThread]
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
            Console.WriteLine($"XDesigner.RenderHost.WinUI protocol {ProtocolInfo.Version}, Windows App SDK {WindowsAppSdkVersion}");
            return RenderHostExitCodes.Ok;
        }

        if (options.RenderFile is null && options.PipeName is null)
        {
            PrintUsage();
            return RenderHostExitCodes.BadArguments;
        }

        // The server watches the designer once it runs; until then a designer that goes away ends this process here.
        using var startup = options.RenderFile is null ? RenderHostServer.ExitIfParentExitsDuringStartup(options.ParentProcessId) : null;

        var exitCode = RenderHostExitCodes.Ok;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new HostApp(
                options,
                host =>
                {
                    startup?.Started();
                    return options.RenderFile is null ? ServeAsync(options, host) : RenderOnceAsync(options, host);
                },
                code => exitCode = code);
        });
        return exitCode;
    }

    /// <summary>Server mode: serve the designer app over the named pipe (off the UI thread) until it goes away.</summary>
    private static Task<int> ServeAsync(RenderHostOptions options, HostApp host)
    {
        var service = new RenderHostService(host);
        return Task.Run(() => RenderHostServer.RunAsync(options, service, service.ShutdownRequested));
    }

    /// <summary>Renders one XAML file to a PNG plus a <c>.layout.json</c>. Used for manual checks and integration tests.</summary>
    private static async Task<int> RenderOnceAsync(RenderHostOptions options, HostApp host)
    {
        Console.WriteLine($"Resources: {host.BuilderSource}");
        foreach (var warning in host.Warnings)
        {
            Console.WriteLine($"Warning: {warning}");
        }

        var request = new RenderRequest(1, await File.ReadAllTextAsync(options.RenderFile!), options.Scale, options.Theme, options.Size);
        var renderer = new XamlRenderer(host, new RenderCopySanitizer(new XamlTypeResolver()));
        var result = await host.InvokeTaskAsync(() => renderer.RenderAsync(request));
        return RenderOnceOutput.Write(result, options.OutputPath!);
    }

    private static void PrintUsage() => Console.Error.WriteLine(
        """
        Usage (run from a shadow folder: the project's build output with this host's files on top):
          XDesigner.RenderHost.WinUI --pipe <name> [--assembly <path>] [--project <dir>] [--parent-pid <pid>] [--no-user-app]
          XDesigner.RenderHost.WinUI --render <file.xaml> --out <file.png> [--assembly <path>] [--project <dir>] [--scale <n>] [--theme Light|Dark] [--size <w>x<h>] [--no-user-app]
          XDesigner.RenderHost.WinUI --version
        """);
}
