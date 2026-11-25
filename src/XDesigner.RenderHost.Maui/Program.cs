using Microsoft.UI.Dispatching;
using XDesigner.Protocol;
using XDesigner.Protocol.Rendering;
using XDesigner.RenderHost.Hosting;
using XDesigner.RenderHost.Rendering;
using XDesigner.RenderHost.Server;

namespace XDesigner.RenderHost;

internal static class Program
{
    /// <summary>The MAUI version this host runs (its Microsoft.Maui.Controls).</summary>
    public static Version MauiVersion { get; } = typeof(Microsoft.Maui.Controls.Application).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>The Microsoft.Maui.Controls package version (<c>10.0.130</c>), for the handshake.</summary>
    public static string MauiPackageVersion { get; } =
        (typeof(Microsoft.Maui.Controls.Application).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? MauiVersion.ToString(3))
        .Split('+')[0];

    /// <summary>The framework id the designer knows this host by (<c>Maui10</c>, <c>Maui11</c>).</summary>
    public static string FrameworkId { get; } = $"Maui{MauiVersion.Major}";

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
            Console.WriteLine($"XDesigner.RenderHost.{FrameworkId} protocol {ProtocolInfo.Version}, .NET MAUI {MauiPackageVersion}");
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
        Microsoft.UI.Xaml.Application.Start(p =>
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
        Console.WriteLine($"App: {host.BuilderSource}");
        foreach (var warning in host.Warnings)
        {
            Console.WriteLine($"Warning: {warning}");
        }

        var request = new RenderRequest(1, await File.ReadAllTextAsync(options.RenderFile!), options.Scale, options.Theme, options.Size);
        var renderer = new MauiRenderer(host, new MauiSanitizer(new MauiTypeResolver(), host.UserAssembly));
        var result = await host.InvokeTaskAsync(() => renderer.RenderAsync(request));
        return RenderOnceOutput.Write(result, options.OutputPath!);
    }

    private static void PrintUsage() => Console.Error.WriteLine(
        $"""
        Usage (run from a shadow folder: the project's build output with this host's files on top):
          XDesigner.RenderHost.{FrameworkId} --pipe <name> [--assembly <path>] [--parent-pid <pid>] [--no-user-app]
          XDesigner.RenderHost.{FrameworkId} --render <file.xaml> --out <file.png> [--assembly <path>] [--scale <n>] [--theme Light|Dark] [--size <w>x<h>] [--no-user-app]
          XDesigner.RenderHost.{FrameworkId} --version
        """);
}
