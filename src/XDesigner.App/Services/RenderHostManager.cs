using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using XDesigner.Core.Frameworks;
using XDesigner.Core.Metadata;
using XDesigner.Core.Rendering;
using XDesigner.Protocol.Metadata;

namespace XDesigner.App.Services;

/// <summary>Which render host a document needs: its framework version, and the project assembly to load (if any).</summary>
/// <param name="ProjectDirectory">The project's folder (the WinUI host reads App.xaml there); not part of the identity.</param>
public readonly record struct HostKey(FrameworkProfile Profile, string? AssemblyPath, string? ProjectDirectory = null)
{
    internal string Id => $"{Profile.Id}|{AssemblyPath}";

    public override string ToString() => AssemblyPath is null ? $"{Profile.DisplayName}, no project" : $"{Profile.DisplayName}, {Path.GetFileName(AssemblyPath)}";
}

/// <summary>
/// Owns the render host processes: one per framework version and user project assembly (or "no project"), started on
/// first use, restarted automatically after a crash (at most <see cref="MaxRestartsPerMinute"/> times a minute), and
/// falling back to the default theme when the project's own App can't start (§15.2, §16). Each framework version's host
/// is a separate exe in <c>renderhosts\{folder}</c> (<see cref="FrameworkProfile.HostFolder"/>).
/// </summary>
public sealed class RenderHostManager : IAsyncDisposable
{
    public const int MaxRestartsPerMinute = 3;

    private readonly string _hostsDirectory;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (HostKey Key, Task<RenderHostConnection> Task)> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<DateTime>> _starts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _loadedStamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConditionalWeakTable<RenderHostConnection, Task<ControlCatalog>> _catalogs = new();
    private readonly ConditionalWeakTable<RenderHostConnection, ConcurrentDictionary<string, Task<PropertyInfoDto[]>>> _properties = new();
    private readonly ConditionalWeakTable<RenderHostConnection, ConcurrentDictionary<string, Task<EventInfoDto[]>>> _events = new();
    private readonly ConditionalWeakTable<RenderHostConnection, Task<string[]>> _fonts = new();
    private readonly Dictionary<string, ShadowHostFolder> _shadows = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _shadowRoot;
    private bool _disposed;

    /// <param name="hostsDirectory">The folder holding one subfolder per render host (<c>renderhosts</c>).</param>
    /// <param name="shadowRoot">Where shadow folders for WinUI hosts go (§14.5); <see cref="ShadowHostFolder.DefaultRoot"/> by default.</param>
    public RenderHostManager(string hostsDirectory, string? shadowRoot = null)
    {
        _hostsDirectory = hostsDirectory;
        _shadowRoot = shadowRoot ?? ShadowHostFolder.DefaultRoot;

        // Folders left by a designer that was ended (or crashed) with a WinUI renderer running.
        _ = Task.Run(() =>
        {
            try
            {
                ShadowHostFolder.RemoveUnused(_shadowRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Tried again when a WinUI renderer starts.
            }
        });
    }

    /// <summary>Default location: <c>renderhosts\</c> next to the app (see the CopyRenderHosts build target).</summary>
    public static string DefaultHostsDirectory => Path.Combine(AppContext.BaseDirectory, "renderhosts");

    /// <summary>Where the render host for a framework version is.</summary>
    public string ExecutableFor(FrameworkProfile profile) => Path.Combine(_hostsDirectory, profile.HostFolder, profile.HostExecutable);

    /// <summary>Whether the render host for a framework version is installed.</summary>
    public bool IsInstalled(FrameworkProfile profile) => File.Exists(ExecutableFor(profile));

    /// <summary>Human-readable progress and problems, raised on any thread.</summary>
    public event Action<string>? Log;

    /// <summary>A host was started (or restarted) and is ready.</summary>
    public event Action<RenderHostConnection>? HostReady;

    /// <summary>Returns the live host for <paramref name="key"/>, starting it if needed.</summary>
    public async Task<RenderHostConnection> GetAsync(HostKey key, CancellationToken cancellationToken = default)
    {
        Task<RenderHostConnection> task;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_hosts.TryGetValue(key.Id, out var entry) || IsDead(entry.Task))
            {
                if (!key.Profile.IsSupported)
                {
                    throw new InvalidOperationException($"Designing {key.Profile.DisplayName} is coming in a later version of xDesigner.");
                }

                if (!IsInstalled(key.Profile))
                {
                    throw new InvalidOperationException($"The {key.Profile.DisplayName} renderer isn't installed. Run the xDesigner installer again and include {key.Profile.DisplayName}.");
                }

                ThrowIfRestartingTooOften(key.Id);
                if (key.AssemblyPath is not null)
                {
                    _loadedStamps[key.Id] = StampOf(key.AssemblyPath); // the version this host will load
                }

                entry = (key, StartAsync(key));
                _hosts[key.Id] = entry;
            }

            task = entry.Task;
        }

        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The control types a running host knows about (fetched once per host process).</summary>
    public Task<ControlCatalog> GetCatalogAsync(RenderHostConnection connection, CancellationToken cancellationToken = default) =>
        _catalogs.GetValue(connection, async c => new ControlCatalog(
                await c.Host.GetControlTypesAsync(CancellationToken.None).ConfigureAwait(false), ProfileOf(c)))
            .WaitAsync(cancellationToken);

    /// <summary>Properties of a control type (fetched once per type per host process).</summary>
    public Task<PropertyInfoDto[]> GetPropertiesAsync(RenderHostConnection connection, string typeFullName) =>
        _properties.GetValue(connection, _ => new ConcurrentDictionary<string, Task<PropertyInfoDto[]>>())
            .GetOrAdd(typeFullName, name => connection.Host.GetPropertiesAsync(name, CancellationToken.None));

    /// <summary>Events of a control type (fetched once per type per host process).</summary>
    public Task<EventInfoDto[]> GetEventsAsync(RenderHostConnection connection, string typeFullName) =>
        _events.GetValue(connection, _ => new ConcurrentDictionary<string, Task<EventInfoDto[]>>())
            .GetOrAdd(typeFullName, name => connection.Host.GetEventsAsync(name, CancellationToken.None));

    /// <summary>Font names the host can render (fetched once per host process).</summary>
    public Task<string[]> GetFontFamiliesAsync(RenderHostConnection connection) =>
        _fonts.GetValue(connection, c => c.Host.GetFontFamiliesAsync(CancellationToken.None));

    /// <summary>The profile a running host reported in its handshake.</summary>
    public static FrameworkProfile? ProfileOf(RenderHostConnection connection) =>
        FrameworkProfile.All.FirstOrDefault(p => p.Id.ToString() == connection.Info.FrameworkId);

    /// <summary>
    /// Raised (on any thread) after the hosts for an assembly path were stopped so the next request starts fresh ones
    /// with the rebuilt assemblies (§11.2). Documents using them re-render and reload their control catalog.
    /// </summary>
    public event Action<string?>? HostRestarted;

    /// <summary>
    /// True when every host for <paramref name="userAssemblyPath"/> (there is one per framework version that uses it) is
    /// running, or starting, with the assembly as it is on disk now: a build that didn't change it needs no restart.
    /// </summary>
    public bool IsCurrent(string userAssemblyPath)
    {
        lock (_lock)
        {
            var hosts = _hosts.Values.Where(h => string.Equals(h.Key.AssemblyPath, userAssemblyPath, StringComparison.OrdinalIgnoreCase)).ToList();
            return hosts.Count > 0 && hosts.All(h => !IsDead(h.Task)
                && _loadedStamps.TryGetValue(h.Key.Id, out var stamp) && stamp == StampOf(userAssemblyPath));
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
    /// Stops the hosts for <paramref name="userAssemblyPath"/> (after a rebuild): the next render starts new processes that
    /// load the new assemblies. A planned restart doesn't count towards the crash limit.
    /// </summary>
    public async Task RestartAsync(string? userAssemblyPath)
    {
        List<Task<RenderHostConnection>> old;
        lock (_lock)
        {
            var keys = _hosts.Where(h => string.Equals(h.Value.Key.AssemblyPath, userAssemblyPath, StringComparison.OrdinalIgnoreCase)).Select(h => h.Key).ToList();
            old = keys.Select(k => _hosts[k].Task).ToList();
            foreach (var key in keys)
            {
                _hosts.Remove(key);
                _starts.Remove(key);
            }
        }

        foreach (var task in old)
        {
            try
            {
                await (await task.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
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
    public async Task AbandonAsync(RenderHostConnection connection, HostKey key)
    {
        lock (_lock)
        {
            if (_hosts.TryGetValue(key.Id, out var entry) && entry.Task.IsCompletedSuccessfully && entry.Task.Result == connection)
            {
                _hosts.Remove(key.Id);
            }
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        Log?.Invoke("The renderer stopped responding and was stopped.");
    }

    /// <summary>Clears the crash throttle so the next request may start the host again (user pressed Retry).</summary>
    public void ResetRestartLimit(HostKey key)
    {
        lock (_lock)
        {
            _starts.Remove(key.Id);
        }
    }

    /// <summary>The host for a key, only if it is already running.</summary>
    public RenderHostConnection? TryGetRunning(HostKey key)
    {
        lock (_lock)
        {
            return _hosts.TryGetValue(key.Id, out var entry) && entry.Task.IsCompletedSuccessfully && entry.Task.Result.IsAlive
                ? entry.Task.Result
                : null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Task<RenderHostConnection>> hosts;
        lock (_lock)
        {
            _disposed = true;
            hosts = [.. _hosts.Values.Select(h => h.Task)];
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

        List<ShadowHostFolder> shadows;
        lock (_lock)
        {
            shadows = [.. _shadows.Values];
            _shadows.Clear();
        }

        shadows.ForEach(s => s.Dispose());
    }

    /// <summary>
    /// Where to start the host for <paramref name="key"/>: its installed exe, or for a framework whose host runs from the
    /// project's output (WinUI), a fresh shadow folder of that output (replacing the key's previous one).
    /// </summary>
    private async Task<(string Executable, string? AssemblyPath)> PrepareHostAsync(HostKey key)
    {
        var executable = ExecutableFor(key.Profile);
        if (!key.Profile.HostRunsFromProjectOutput || key.AssemblyPath is null)
        {
            return (executable, key.AssemblyPath);
        }

        var shadow = await Task.Run(() => ShadowHostFolder.Prepare(
            Path.GetDirectoryName(executable)!, Path.GetDirectoryName(key.AssemblyPath)!, _shadowRoot)).ConfigureAwait(false);
        ShadowHostFolder? previous;
        lock (_lock)
        {
            _shadows.Remove(key.Id, out previous);
            _shadows[key.Id] = shadow;
        }

        previous?.Dispose();
        return (Path.Combine(shadow.Path, key.Profile.HostExecutable), Path.Combine(shadow.Path, Path.GetFileName(key.AssemblyPath)));
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

    private async Task<RenderHostConnection> StartAsync(HostKey key)
    {
        Log?.Invoke($"Starting renderer ({key})…");
        var (executable, assemblyPath) = await PrepareHostAsync(key).ConfigureAwait(false);

        RenderHostConnection connection;
        try
        {
            connection = await RenderHostConnection.StartAsync(new RenderHostStartInfo(executable, assemblyPath, ProjectDirectory: key.ProjectDirectory)).ConfigureAwait(false);
        }
        catch (RenderHostStartException ex) when (ex.IsSetupFailure && key.AssemblyPath is not null)
        {
            Log?.Invoke($"The project's App could not start inside the renderer; using the default theme instead.{Environment.NewLine}{FirstLines(ex.ErrorOutput, 3)}");
            try
            {
                connection = await RenderHostConnection.StartAsync(new RenderHostStartInfo(executable, assemblyPath, UseUserApplication: false, key.ProjectDirectory)).ConfigureAwait(false);
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
        if (info.FrameworkId != key.Profile.Id.ToString())
        {
            // A damaged install (one host's files in another's folder) must not render a project with the wrong version.
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"The renderer in {Path.GetDirectoryName(executable)} renders {info.FrameworkId}, not {key.Profile.DisplayName}. Reinstall xDesigner.");
        }

        Log?.Invoke($"Renderer ready ({key}): {key.Profile.DescribeVersion(info.FrameworkVersion)}, app {info.ApplicationType}, design mode {(info.IsDesignMode ? "on" : "off")}.");
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
