using System.Diagnostics;
using System.IO.Pipes;
using XDesigner.Protocol;

namespace XDesigner.RenderHost.Server;

/// <summary>Exit codes the designer app interprets when it starts a render host (every host uses these).</summary>
public static class RenderHostExitCodes
{
    public const int Ok = 0;
    public const int BadArguments = 1;

    /// <summary>Framework or user app setup failed; the app retries with <c>--no-user-app</c>.</summary>
    public const int SetupFailed = ProtocolInfo.SetupFailedExitCode;

    public const int RenderFailed = 4;
}

/// <summary>
/// Serves <see cref="IRenderHost"/> over a named pipe to exactly one client (the designer app).
/// Exits when the client disconnects, asks to shut down, or the parent process dies (§14.1).
/// </summary>
public static class RenderHostServer
{
    /// <summary>How long to wait for the app to connect before giving up.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <param name="service">The host's implementation of the protocol.</param>
    /// <param name="shutdownRequested">Completes when the client asks the host to stop.</param>
    public static async Task<int> RunAsync(RenderHostOptions options, IRenderHost service, Task shutdownRequested)
    {
        using var stop = new CancellationTokenSource();
        using var parent = options.ParentProcessId is { } parentPid ? WatchParent(parentPid, stop) : null;

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
                return RenderHostExitCodes.Ok;
            }
        }

        using var rpc = RenderHostRpc.CreateConnection(pipe);
        rpc.AddLocalRpcTarget<IRenderHost>(service, null);
        rpc.StartListening();

        // Whichever happens first: client gone, client asked to stop, parent died.
        await Task.WhenAny(rpc.Completion, shutdownRequested, Task.Delay(Timeout.Infinite, stop.Token))
            .ContinueWith(_ => { }, TaskScheduler.Default);

        // Asked to stop: let the reply reach the designer (it then closes the pipe) before the connection is disposed, or
        // the designer sees the connection lost and kills the host instead of letting it exit (review L28).
        if (shutdownRequested.IsCompleted && !rpc.Completion.IsCompleted)
        {
            await rpc.Completion.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { }, TaskScheduler.Default);
        }

        return RenderHostExitCodes.Ok;
    }

    /// <summary>Cancels <paramref name="stop"/> when the designer exits; dispose the result when serving ends.</summary>
    private static Process? WatchParent(int parentPid, CancellationTokenSource stop)
    {
        void Stop()
        {
            try
            {
                stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Serving already ended (the designer exited while the host was shutting down): nothing to stop (review L28).
            }
        }

        try
        {
            var parent = Process.GetProcessById(parentPid);
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) => Stop();
            if (parent.HasExited)
            {
                Stop();
            }

            return parent;
        }
        catch (ArgumentException)
        {
            Stop(); // parent already gone
            return null;
        }
    }

    /// <summary>
    /// Until the host is ready, the server's watch isn't running (the user's App may hang while it's created): this ends
    /// the process if the designer goes away first. Dispose the result once the host is started, or call
    /// <see cref="StartupWatch.Started"/>.
    /// </summary>
    public static StartupWatch? ExitIfParentExitsDuringStartup(int? parentPid) =>
        parentPid is { } pid ? new StartupWatch(pid) : null;

    public sealed class StartupWatch : IDisposable
    {
        private readonly Process? _parent;
        private volatile bool _started;

        internal StartupWatch(int parentPid)
        {
            try
            {
                _parent = Process.GetProcessById(parentPid);
                _parent.EnableRaisingEvents = true;
                _parent.Exited += (_, _) =>
                {
                    if (!_started)
                    {
                        Environment.Exit(RenderHostExitCodes.Ok);
                    }
                };
                if (_parent.HasExited)
                {
                    Environment.Exit(RenderHostExitCodes.Ok);
                }
            }
            catch (ArgumentException)
            {
                Environment.Exit(RenderHostExitCodes.Ok); // already gone
            }
        }

        /// <summary>The host started: from now on the server watches the parent.</summary>
        public void Started() => _started = true;

        public void Dispose()
        {
            _started = true;
            _parent?.Dispose();
        }
    }
}
