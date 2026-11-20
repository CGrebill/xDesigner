using System.Diagnostics;
using System.IO.Pipes;
using XDesigner.Protocol;
using StreamJsonRpc;

namespace XDesigner.Core.Rendering;

/// <summary>How to start a render host process.</summary>
/// <param name="HostExecutablePath">Path to the render host exe.</param>
/// <param name="UserAssemblyPath">The user's built project assembly, or null for "no project" mode.</param>
/// <param name="UseUserApplication">False passes <c>--no-user-app</c> (fallback after a setup failure).</param>
/// <param name="ProjectDirectory">The project folder (<c>--project</c>; the WinUI host reads App.xaml from it).</param>
public sealed record RenderHostStartInfo(string HostExecutablePath, string? UserAssemblyPath, bool UseUserApplication = true, string? ProjectDirectory = null);

/// <summary>The host process exited before it could serve requests (e.g. the user's App failed to start).</summary>
public sealed class RenderHostStartException(string message, int? exitCode, string errorOutput) : Exception(message)
{
    /// <summary>Process exit code, if it exited. 3 means Avalonia/user app setup failed.</summary>
    public int? ExitCode { get; } = exitCode;

    /// <summary>What the host wrote to stderr (diagnostics).</summary>
    public string ErrorOutput { get; } = errorOutput;

    public bool IsSetupFailure => ExitCode == 3;
}

/// <summary>
/// One running render host process and the RPC connection to it (§15.2). Dispose to shut it down.
/// </summary>
public sealed class RenderHostConnection : IAsyncDisposable
{
    private static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(60);

    private readonly Process _process;
    private readonly NamedPipeClientStream _pipe;
    private readonly JsonRpc _rpc;
    private readonly StderrBuffer _stderr;
    private int _disposed;

    private RenderHostConnection(RenderHostStartInfo startInfo, Process process, NamedPipeClientStream pipe, JsonRpc rpc, StderrBuffer stderr, HostInfo info)
    {
        StartInfo = startInfo;
        _process = process;
        _pipe = pipe;
        _rpc = rpc;
        _stderr = stderr;
        Info = info;
        Host = rpc.Attach<IRenderHost>();
        _ = WatchAsync();
    }

    public RenderHostStartInfo StartInfo { get; }

    /// <summary>The host's handshake answer.</summary>
    public HostInfo Info { get; }

    /// <summary>RPC proxy.</summary>
    public IRenderHost Host { get; }

    public int ProcessId => _process.Id;

    /// <summary>Raised once when the host goes away unexpectedly (crash, killed, pipe broken). Not raised after <see cref="DisposeAsync"/>.</summary>
    public event Action<RenderHostConnection, string>? Disconnected;

    public bool IsAlive => Volatile.Read(ref _disposed) == 0 && !_process.HasExited && !_rpc.Completion.IsCompleted;

    /// <summary>Starts the host, connects, and performs the handshake.</summary>
    public static async Task<RenderHostConnection> StartAsync(RenderHostStartInfo startInfo, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(startInfo.HostExecutablePath))
        {
            throw new FileNotFoundException("Render host executable not found.", startInfo.HostExecutablePath);
        }

        var pipeName = ProtocolInfo.CreatePipeName(Environment.ProcessId);
        var psi = new ProcessStartInfo(startInfo.HostExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add("--parent-pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (startInfo.UserAssemblyPath is not null)
        {
            psi.ArgumentList.Add("--assembly");
            psi.ArgumentList.Add(startInfo.UserAssemblyPath);
        }

        if (startInfo.ProjectDirectory is not null)
        {
            psi.ArgumentList.Add("--project");
            psi.ArgumentList.Add(startInfo.ProjectDirectory);
        }

        if (!startInfo.UseUserApplication)
        {
            psi.ArgumentList.Add("--no-user-app");
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the render host.");
        var stderr = new StderrBuffer(process);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout ?? DefaultStartTimeout);

        // CurrentUserOnly: only connect to a pipe server running as this user.
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Task? connect = null;
        try
        {
            // The host creates the pipe only after Avalonia is set up; if it exits first, report why.
            connect = pipe.ConnectAsync(timeoutCts.Token);
            var exited = process.WaitForExitAsync(timeoutCts.Token);
            if (await Task.WhenAny(connect, exited).ConfigureAwait(false) == exited && !connect.IsCompletedSuccessfully)
            {
                await exited.ConfigureAwait(false);
                throw new RenderHostStartException(
                    $"The render host exited during startup (exit code {process.ExitCode}).", process.ExitCode, await stderr.ReadAllAsync().ConfigureAwait(false));
            }

            await connect.ConfigureAwait(false);

            var rpc = RenderHostRpc.CreateConnection(pipe);
            rpc.StartListening();
            var proxy = rpc.Attach<IRenderHost>();
            var info = await proxy.HandshakeAsync(ProtocolInfo.Version, timeoutCts.Token).ConfigureAwait(false);
            if (info.ProtocolVersion != ProtocolInfo.Version)
            {
                rpc.Dispose();
                throw new RenderHostStartException(
                    $"Render host speaks protocol {info.ProtocolVersion}, the designer expects {ProtocolInfo.Version}.", null, "");
            }

            return new RenderHostConnection(startInfo, process, pipe, rpc, stderr, info);
        }
        catch (Exception ex) when (ex is not RenderHostStartException)
        {
            await AbandonAsync(timeoutCts, connect, pipe, process).ConfigureAwait(false);
            if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new RenderHostStartException("The render host did not start in time.", null, await stderr.ReadAllAsync().ConfigureAwait(false));
            }

            throw;
        }
        catch
        {
            await AbandonAsync(timeoutCts, connect, pipe, process).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Cleans up a start that failed. The pending connect is cancelled first: otherwise, once the host has exited,
    /// <see cref="NamedPipeClientStream.ConnectAsync(CancellationToken)"/> keeps polling for a pipe that will never appear.
    /// </summary>
    private static async Task AbandonAsync(CancellationTokenSource timeoutCts, Task? connect, NamedPipeClientStream pipe, Process process)
    {
        await CancelConnectAsync(timeoutCts, connect).ConfigureAwait(false);
        await pipe.DisposeAsync().ConfigureAwait(false);
        KillQuietly(process);
    }

    /// <summary>Cancels a pending pipe connect and waits for it to end.</summary>
    internal static async Task CancelConnectAsync(CancellationTokenSource cts, Task? connect)
    {
        await cts.CancelAsync().ConfigureAwait(false);
        if (connect is not null)
        {
            try
            {
                await connect.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or ObjectDisposedException)
            {
                // Expected: the connect was abandoned.
            }
        }
    }

    /// <summary>Recent stderr lines from the host (crash diagnostics).</summary>
    public string RecentErrorOutput => _stderr.Snapshot();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (!_process.HasExited && !_rpc.Completion.IsCompleted)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await Host.ShutdownAsync().WaitAsync(cts.Token).ConfigureAwait(false);
                await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Fall through to kill.
        }
        finally
        {
            _rpc.Dispose();
            await _pipe.DisposeAsync().ConfigureAwait(false);
            KillQuietly(_process);
            _process.Dispose();
        }
    }

    private async Task WatchAsync()
    {
        try
        {
            await Task.WhenAny(_rpc.Completion, _process.WaitForExitAsync()).ConfigureAwait(false);
        }
        catch
        {
            // Completion faults when the connection breaks; that is the signal we want.
        }

        if (Volatile.Read(ref _disposed) == 0)
        {
            var reason = _process.HasExited
                ? $"The render host stopped (exit code {_process.ExitCode})."
                : "The connection to the render host was lost.";
            Disconnected?.Invoke(this, reason);
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Already gone.
        }
    }

    /// <summary>Keeps the last stderr lines; stdout is drained so the host never blocks on a full pipe.</summary>
    private sealed class StderrBuffer
    {
        private const int MaxLines = 200;
        private readonly Queue<string> _lines = new();
        private readonly Task _done;

        public StderrBuffer(Process process)
        {
            process.OutputDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    done.TrySetResult();
                    return;
                }

                lock (_lines)
                {
                    _lines.Enqueue(e.Data);
                    while (_lines.Count > MaxLines)
                    {
                        _lines.Dequeue();
                    }
                }
            };
            process.BeginErrorReadLine();
            _done = done.Task;
        }

        public string Snapshot()
        {
            lock (_lines)
            {
                return string.Join(Environment.NewLine, _lines);
            }
        }

        public async Task<string> ReadAllAsync()
        {
            await Task.WhenAny(_done, Task.Delay(1000)).ConfigureAwait(false);
            return Snapshot();
        }
    }
}
