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
    /// <summary>Exit codes the designer app interprets when it starts the host.</summary>
    internal static class ExitCodes
    {
        public const int Ok = 0;
        public const int BadArguments = 1;

        /// <summary>Avalonia/user app setup failed; the app retries with <c>--no-user-app</c>.</summary>
        public const int SetupFailed = 3;

        public const int RenderFailed = 4;
    }

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
            return ExitCodes.BadArguments;
        }

        if (options.ShowVersion)
        {
            Console.WriteLine($"XDesigner.RenderHost protocol {ProtocolInfo.Version}, Avalonia {HostVersions.AvaloniaVersion}");
            return ExitCodes.Ok;
        }

        if (options.RenderFile is not null)
        {
            return RenderOnce(options);
        }

        if (options.PipeName is null)
        {
            PrintUsage();
            return ExitCodes.BadArguments;
        }

        return Serve(options);
    }

    /// <summary>Server mode: start Avalonia, then serve the designer app over the named pipe.</summary>
    private static int Serve(RenderHostOptions options)
    {
        HostRuntime runtime;
        try
        {
            runtime = HostRuntime.Start(new HostStartOptions(options.UserAssemblyPath, !options.NoUserApp));
        }
        catch (Exception ex)
        {
            // Before the pipe exists: the app sees the exit code and can retry with --no-user-app.
            Console.Error.WriteLine($"Setup failed: {ex}");
            return ExitCodes.SetupFailed;
        }

        using (runtime)
        {
            return RenderHostServer.RunAsync(options, runtime).GetAwaiter().GetResult();
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
            return ExitCodes.SetupFailed;
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
            var (result, saveError) = runtime.InvokeAsync(() =>
            {
                var r = renderer.Render(request);
                return (r, r.Success ? FramePng.TrySave(r, options.OutputPath!) : null);
            }).GetAwaiter().GetResult();

            if (!result.Success)
            {
                var e = result.Error!;
                Console.Error.WriteLine($"Render failed: {e.Message} ({e.ExceptionType}) line {e.Line?.ToString(CultureInfo.InvariantCulture) ?? "?"}, column {e.Column?.ToString(CultureInfo.InvariantCulture) ?? "?"}{(e.OffendingType is null ? "" : $", in {e.OffendingType}")}");
                return ExitCodes.RenderFailed;
            }

            if (saveError is not null)
            {
                Console.Error.WriteLine($"Could not save PNG: {saveError}");
                return ExitCodes.RenderFailed;
            }

            var layoutPath = Path.ChangeExtension(options.OutputPath!, ".layout.json");
            File.WriteAllText(layoutPath, JsonSerializer.Serialize(result.Root, JsonOptions));
            Console.WriteLine($"Rendered {result.PixelWidth}x{result.PixelHeight} px ({result.RootKind}) to {options.OutputPath}; layout in {layoutPath}");
            return ExitCodes.Ok;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static void PrintUsage() => Console.Error.WriteLine(
        """
        Usage:
          XDesigner.RenderHost --pipe <name> [--assembly <path>] [--parent-pid <pid>] [--no-user-app]
          XDesigner.RenderHost --render <file.axaml> --out <file.png> [--assembly <path>] [--scale <n>] [--theme Light|Dark] [--size <w>x<h>] [--no-user-app]
          XDesigner.RenderHost --version
        """);
}
