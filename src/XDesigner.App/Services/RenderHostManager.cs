using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using XDesigner.Protocol.Metadata;
using XDesigner.Core.Metadata;
using XDesigner.Core.Rendering;

namespace XDesigner.App.Services;

/// <summary>
/// Owns the render host processes: one per user project assembly (or one for "no project"), started on
/// first use, restarted automatically after a crash (at most <see cref="MaxRestartsPerMinute"/> times a
/// minute), and falling back to the default theme when the project's own App can't start (§15.2, §16).
/// </summary>
public sealed class RenderHostManager : IAsyncDisposable
{
    public const int MaxRestartsPerMinute = 3;

    private readonly string _hostExecutable;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Task<RenderHostConnection>> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<DateTime>> _starts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _loadedStamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConditionalWeakTable<RenderHostConnection, Task<ControlCatalog>> _catalogs = new();
    private readonly ConditionalWeakTable<RenderHostConnection, ConcurrentDictionary<string, Task<PropertyInfoDto[]>>> _properties = new();
    private readonly ConditionalWeakTable<RenderHostConnection, ConcurrentDictionary<string, Task<EventInfoDto[]>>> _events = new();
    private readonly ConditionalWeakTable<RenderHostConnection, Task<string[]>> _fonts = new();
    private bool _disposed;

    public RenderHostManager(string hostExecutable)
    {
        _hostExecutable = hostExecutable;
    }

    /// <summary>Default location: <c>renderhost\</c> next to the app (see the CopyRenderHost build target).</summary>
    public static string DefaultHostExecutable =>
        Path.Combine(AppContext.BaseDirectory, "renderhost", "XDesigner.RenderHost.exe");

    /// <summary>Human-readable progress and problems, raised on any thread.</summary>
    public event Action<string>? Log;

    /// <summary>A host was started (or restarted) and is ready.</summary>
    public event Action<RenderHostConnection>? HostReady;

    /// <summary>Returns the live host for <paramref name="userAssemblyPath"/>, starting it if needed.</summary>
    public async Task<RenderHostConnection> GetAsync(string? userAssemblyPath, CancellationToken cancellationToken = default)
    {
        var key = userAssemblyPath ?? "";
        Task<RenderHostConnection> task;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_hosts.TryGetValue(key, out task!) || IsDead(task))
            {
                ThrowIfRestartingTooOften(key);
                if (userAssemblyPath is not null)
                {
                    _loadedStamps[key] = StampOf(userAssemblyPath); // the version this host will load
                }

                task = StartAsync(userAssemblyPath);
                _hosts[key] = task;
            }
        }

        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The control types a running host knows about (fetched once per host process).</summary>
    public Task<ControlCatalog> GetCatalogAsync(RenderHostConnection connection, CancellationToken cancellationToken = default) =>
        _catalogs.GetValue(connection, async c => new ControlCatalog(await c.Host.GetControlTypesAsync(CancellationToken.None).ConfigureAwait(false)))
            .WaitAsync(cancellationToken);

    /// <summary>Properties of a control type (fetched once per type per host process).</summary>
    public Task<PropertyInfoDto[]> GetPropertiesAsync(RenderHostConnection connection, string typeFullName) =>
        _properties.GetValue(connection, _ => new ConcurrentDictionary<string, Task<PropertyInfoDto[]>>())
            .GetOrAdd(typeFullName, name => connection.Host.GetPropertiesAsync(name, CancellationToken.None));

    /// <summary>Events of a control type (fetched once per type per host process).</summary>
    public Task<EventInfoDto[]> GetEventsAsync(RenderHostConnection connection, string typeFullName) =>
        _events.GetValue(connection, _ => new ConcurrentDictionary<string, Task<EventInfoDto[]>>())
            .GetOrAdd(typeFullName, name => connection.Host.GetEventsAsync(name, CancellationToken.None));

    /// <summary>Font names for the FontFamily editor (fetched once per host process).</summary>
    public Task<string[]> GetFontFamiliesAsync(RenderHostConnection connection) =>
        _fonts.GetValue(connection, c => c.Host.GetFontFamiliesAsync(CancellationToken.None));

    /// <summary>
    /// Raised (on any thread) after the host for an assembly path was stopped so the next request starts a fresh one
    /// with the rebuilt assemblies (§11.2). Documents using it re-render and reload their control catalog.
    /// </summary>
    public event Action<string?>? HostRestarted;

    /// <summary>
    /// True when a host for <paramref name="userAssemblyPath"/> is running (or starting) with the assembly as it is on disk
    /// now: a build that didn't change the assembly (up to date) needs no restart.
    /// </summary>
    public bool IsCurrent(string userAssemblyPath)
    {
        lock (_lock)
        {
            return _hosts.TryGetValue(userAssemblyPath, out var task) && !IsDead(task)
                && _loadedStamps.TryGetValue(userAssemblyPath, out var stamp) && stamp == StampOf(userAssemblyPath);
        }
    }

    private static DateTime StampOf(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Stops the host for <paramref name="userAssemblyPath"/> (after a rebuild): the next render starts a new process that
    /// loads the new assemblies. A planned restart doesn't count towards the crash limit.
    /// </summary>
    public async Task RestartAsync(string? userAssemblyPath)
    {
        var key = userAssemblyPath ?? "";
        Task<RenderHostConnection>? old;
        lock (_lock)
        {
            _hosts.Remove(key, out old);
            _starts.Remove(key);
        }

        if (old is not null)
        {
            try
            {
                await (await old.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // It had failed to start or already stopped.
            }
        }

        HostRestarted?.Invoke(userAssemblyPath);
    }

    /// <summary>
    /// Stops a host that stopped responding (a render ran past the watchdog, §14.2). Unlike <see cref="RestartAsync"/> this
    /// counts towards the crash limit and doesn't re-render by itself, so a control that hangs every time can't make the
    /// renderer restart in a loop; the next render (an edit, or Retry) starts a fresh host.
    /// </summary>
    public async Task AbandonAsync(RenderHostConnection connection, string? userAssemblyPath)
    {
        var key = userAssemblyPath ?? "";
        lock (_lock)
        {
            if (_hosts.TryGetValue(key, out var task) && task.IsCompletedSuccessfully && task.Result == connection)
            {
                _hosts.Remove(key);
            }
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        Log?.Invoke("The renderer stopped responding and was stopped.");
    }

    /// <summary>Clears the crash throttle so the next request may start the host again (user pressed Retry).</summary>
    public void ResetRestartLimit(string? userAssemblyPath)
    {
        lock (_lock)
        {
            _starts.Remove(userAssemblyPath ?? "");
        }
    }

    /// <summary>The host for an assembly, only if it is already running.</summary>
    public RenderHostConnection? TryGetRunning(string? userAssemblyPath)
    {
        lock (_lock)
        {
            return _hosts.TryGetValue(userAssemblyPath ?? "", out var task) && task.IsCompletedSuccessfully && task.Result.IsAlive
                ? task.Result
                : null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Task<RenderHostConnection>> hosts;
        lock (_lock)
        {
            _disposed = true;
            hosts = [.. _hosts.Values];
            _hosts.Clear();
        }

        foreach (var task in hosts)
        {
            try
            {
                await (await task.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Failed starts have nothing to dispose.
            }
        }
    }

    private static bool IsDead(Task<RenderHostConnection> task) =>
        task.IsFaulted || task.IsCanceled || (task.IsCompletedSuccessfully && !task.Result.IsAlive);

    private void ThrowIfRestartingTooOften(string key)
    {
        if (!_starts.TryGetValue(key, out var starts))
        {
            _starts[key] = starts = new Queue<DateTime>();
        }

        var now = DateTime.UtcNow;
        while (starts.Count > 0 && now - starts.Peek() > TimeSpan.FromMinutes(1))
        {
            starts.Dequeue();
        }

        // The first start doesn't count as a restart.
        if (starts.Count > MaxRestartsPerMinute)
        {
            throw new InvalidOperationException("The renderer keeps stopping, so it won't be restarted automatically. Press Retry to try again.");
        }

        starts.Enqueue(now);
    }

    private async Task<RenderHostConnection> StartAsync(string? userAssemblyPath)
    {
        var label = userAssemblyPath is null ? "no project" : Path.GetFileName(userAssemblyPath);
        Log?.Invoke($"Starting renderer ({label})…");

        RenderHostConnection connection;
        try
        {
            connection = await RenderHostConnection.StartAsync(new RenderHostStartInfo(_hostExecutable, userAssemblyPath)).ConfigureAwait(false);
        }
        catch (RenderHostStartException ex) when (ex.IsSetupFailure && userAssemblyPath is not null)
        {
            Log?.Invoke($"The project's App could not start inside the renderer; using the default Fluent theme instead.{Environment.NewLine}{FirstLines(ex.ErrorOutput, 3)}");
            try
            {
                connection = await RenderHostConnection.StartAsync(new RenderHostStartInfo(_hostExecutable, userAssemblyPath, UseUserApplication: false)).ConfigureAwait(false);
            }
            catch (RenderHostStartException fallback)
            {
                // The sibling catch below doesn't see exceptions thrown here, so log this one's diagnostics too.
                Log?.Invoke($"{fallback.Message}{Environment.NewLine}{FirstLines(fallback.ErrorOutput, 5)}");
                throw;
            }
        }
        catch (RenderHostStartException ex)
        {
            Log?.Invoke($"{ex.Message}{Environment.NewLine}{FirstLines(ex.ErrorOutput, 5)}");
            throw;
        }

        var info = connection.Info;
        Log?.Invoke($"Renderer ready ({label}): Avalonia {info.AvaloniaVersion}, app {info.ApplicationType}, design mode {(info.IsDesignMode ? "on" : "off")}.");
        foreach (var warning in info.Warnings)
        {
            Log?.Invoke($"Renderer warning: {warning}");
        }

        connection.Disconnected += (c, reason) =>
        {
            var details = FirstLines(c.RecentErrorOutput, 5);
            Log?.Invoke(string.IsNullOrWhiteSpace(details) ? reason : $"{reason}{Environment.NewLine}{details}");
        };
        HostReady?.Invoke(connection);
        return connection;
    }

    private static string FirstLines(string text, int count) =>
        string.Join(Environment.NewLine, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(count).Select(l => "  " + l.TrimEnd('\r')));
}
