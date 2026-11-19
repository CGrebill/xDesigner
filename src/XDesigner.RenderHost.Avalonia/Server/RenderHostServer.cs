using System.Diagnostics;
using System.IO.Pipes;
using XDesigner.Protocol;
using XDesigner.RenderHost.Hosting;

namespace XDesigner.RenderHost.Server;

/// <summary>
/// Serves <see cref="IRenderHost"/> over a named pipe to exactly one client (the designer app).
/// Exits when the client disconnects, asks to shut down, or the parent process dies (§14.1).
/// </summary>
internal static class RenderHostServer
{
    /// <summary>How long to wait for the app to connect before giving up.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    public static async Task<int> RunAsync(RenderHostOptions options, HostRuntime runtime)
    {
        using var stop = new CancellationTokenSource();
        if (options.ParentProcessId is { } parentPid)
        {
            WatchParent(parentPid, stop);
        }

        await using var pipe = new NamedPipeServerStream(
            options.PipeName!,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            connectTimeout.CancelAfter(ConnectTimeout);
            try
            {
                await pipe.WaitForConnectionAsync(connectTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("No client connected; exiting.");
                return Program.ExitCodes.Ok;
            }
        }

        var service = new RenderHostService(runtime);
        using var rpc = RenderHostRpc.CreateConnection(pipe);
        rpc.AddLocalRpcTarget<IRenderHost>(service, null);
        rpc.StartListening();

        // Whichever happens first: client gone, client asked to stop, parent died.
        await Task.WhenAny(rpc.Completion, service.ShutdownRequested, Task.Delay(Timeout.Infinite, stop.Token))
            .ContinueWith(_ => { }, TaskScheduler.Default);
        return Program.ExitCodes.Ok;
    }

    private static void WatchParent(int parentPid, CancellationTokenSource stop)
    {
        try
        {
            var parent = Process.GetProcessById(parentPid);
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) => stop.Cancel();
            if (parent.HasExited)
            {
                stop.Cancel();
            }
        }
        catch (ArgumentException)
        {
            stop.Cancel(); // parent already gone
        }
    }
}
