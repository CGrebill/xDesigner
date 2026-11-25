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
    // Each host process's own shadow folder: deleted when that process is disposed, never by another start (review L26).
    private readonly Dictionary<RenderHostConnection, ShadowHostFolder> _shadows = [];
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UserAppStartTimeout = TimeSpan.FromSeconds(45);
    private readonly ConcurrentDictionary<string, bool> _versionWarned = new(StringComparer.OrdinalIgnoreCase); // once per project
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
        Task<RenderHostConnection>? dead = null;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_hosts.TryGetValue(key.Id, out var entry) || IsDead(entry.Task))
            {
                if (!IsInstalled(key.Profile))
                {
                    throw new InvalidOperationException($"The {key.Profile.DisplayName} renderer isn't installed. Run the xDesigner installer again and include {key.Profile.DisplayName}.");
                }

                ThrowIfRestartingTooOften(key.Id);
                if (key.AssemblyPath is not null)
                {
                    _loadedStamps[key.Id] = StampOf(key.AssemblyPath); // the version this host will load
                }

                dead = entry.Task; // a crashed host's process handle, pipe and shadow folder are released (review L7)

                // Started off the lock: starting a process and preparing a shadow folder take a while, and the UI thread
                // asks TryGetRunning meanwhile (review L12).
                entry = (key, Task.Run(() => StartAsync(key)));
                _hosts[key.Id] = entry;
            }

            task = entry.Task;
        }

        if (dead is not null)
        {
            _ = DisposeHostAsync(dead);
        }

        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The control types a running host knows about (fetched once per host process; a failure is asked again, review L10).</summary>
    public Task<ControlCatalog> GetCatalogAsync(RenderHostConnection connection, CancellationToken cancellationToken = default)
    {
        if (_catalogs.TryGetValue(connection, out var cached) && (cached.IsFaulted || cached.IsCanceled))
        {
            _catalogs.Remove(connection);
        }

        return _catalogs.GetValue(connection, async c => new ControlCatalog(
                await c.Host.GetControlTypesAsync(CancellationToken.None).WaitAsync(MetadataTimeout).ConfigureAwait(false), ProfileOf(c)))
            .WaitAsync(cancellationToken);
    }

    /// <summary>Properties of a control type (fetched once per type per host process).</summary>
    public Task<PropertyInfoDto[]> GetPropertiesAsync(RenderHostConnection connection, string typeFullName) =>
        Cached(_properties.GetValue(connection, _ => new ConcurrentDictionary<string, Task<PropertyInfoDto[]>>()), typeFullName,
            name => connection.Host.GetPropertiesAsync(name, CancellationToken.None));

    /// <summary>Events of a control type (fetched once per type per host process).</summary>
    public Task<EventInfoDto[]> GetEventsAsync(RenderHostConnection connection, string typeFullName) =>
        Cached(_events.GetValue(connection, _ => new ConcurrentDictionary<string, Task<EventInfoDto[]>>()), typeFullName,
            name => connection.Host.GetEventsAsync(name, CancellationToken.None));

    /// <summary>Font names the host can render (fetched once per host process).</summary>
    public Task<string[]> GetFontFamiliesAsync(RenderHostConnection connection)
    {
        if (_fonts.TryGetValue(connection, out var cached) && (cached.IsFaulted || cached.IsCanceled))
        {
            _fonts.Remove(connection);
        }

        return _fonts.GetValue(connection, c => c.Host.GetFontFamiliesAsync(CancellationToken.None).WaitAsync(MetadataTimeout));
    }

    /// <summary>
    /// A metadata request, asked once and kept, but asked again after a failure; a host that doesn't answer gives up after
    /// <see cref="MetadataTimeout"/> instead of leaving the pane waiting (review L10, L13).
    /// </summary>
    private static Task<T> Cached<T>(ConcurrentDictionary<string, Task<T>> cache, string key, Func<string, Task<T>> fetch)
    {
        if (cache.TryGetValue(key, out var cached) && (cached.IsFaulted || cached.IsCanceled))
        {
            cache.TryRemove(new KeyValuePair<string, Task<T>>(key, cached));
        }

        return cache.GetOrAdd(key, k => fetch(k).WaitAsync(MetadataTimeout));
    }

    /// <summary>
    /// Stops the hosts no open document uses any more (its documents closed, another solution opened), with their shadow
    /// folders (review M17). <paramref name="inUse"/> are the keys of the open documents.
    /// </summary>
    public async Task StopUnusedAsync(IEnumerable<HostKey> inUse)
    {
        var keep = inUse.Select(k => k.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<Task<RenderHostConnection>> unused;
        lock (_lock)
        {
            var keys = _hosts.Keys.Where(k => !keep.Contains(k)).ToList();
            unused = keys.Select(k => _hosts[k].Task).ToList();
            foreach (var key in keys)
            {
                _hosts.Remove(key);
                _starts.Remove(key);
                _loadedStamps.Remove(key);
            }
        }

        foreach (var task in unused)
        {
            await DisposeHostAsync(task).ConfigureAwait(false);
        }

        if (unused.Count > 0)
        {
            Log?.Invoke($"Stopped {unused.Count} renderer{(unused.Count == 1 ? "" : "s")} no open file uses.");
        }
    }

    /// <summary>Shuts a host down (if it started) and deletes its shadow folder.</summary>
    private async Task DisposeHostAsync(Task<RenderHostConnection> task)
    {
        RenderHostConnection connection;
        try
        {
            connection = await task.ConfigureAwait(false);
        }
        catch
        {
            return; // it failed to start: StartAsync cleaned up
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        ShadowHostFolder? shadow;
        lock (_lock)
        {
            _shadows.Remove(connection, out shadow);
        }

        shadow?.Dispose();
    }

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

    /// <summary>
    /// When the build output last changed: the newest assembly, deps file or resource index next to the project's assembly.
    /// A referenced library rebuilt in place leaves the project's own assembly untouched (reference assemblies) but copies
    /// its new dll here, and the renderer must load that one (review M2).
    /// </summary>
    private static DateTime StampOf(string path)
    {
        try
        {
            var newest = File.GetLastWriteTimeUtc(path);
            if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (Path.GetExtension(file).ToLowerInvariant() is ".dll" or ".pri" or ".json" or ".xbf")
                    {
                        var written = File.GetLastWriteTimeUtc(file);
                        newest = written > newest ? written : newest;
                    }
                }
            }

            return newest;
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
            await DisposeHostAsync(task).ConfigureAwait(false);
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

        await DisposeHostAsync(Task.FromResult(connection)).ConfigureAwait(false);
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
            await DisposeHostAsync(task).ConfigureAwait(false);
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
    /// project's output (WinUI, MAUI), a fresh shadow folder of that output, which belongs to the process started from it.
    /// </summary>
    private async Task<(string Executable, string? AssemblyPath, ShadowHostFolder? Shadow)> PrepareHostAsync(HostKey key)
    {
        var executable = ExecutableFor(key.Profile);
        if (!key.Profile.HostRunsFromProjectOutput || key.AssemblyPath is null)
        {
            return (executable, key.AssemblyPath, null);
        }

        // MAUI's resource index: {Project}.pri, or resources.pri in a packaged build (review M30).
        (string, string)? resourceIndex = key.Profile.HostUsesProjectResourceIndex
            ? (Path.GetFileNameWithoutExtension(key.AssemblyPath), Path.GetFileNameWithoutExtension(key.Profile.HostExecutable))
            : null;
        var shadow = await Task.Run(() => ShadowHostFolder.Prepare(
            Path.GetDirectoryName(executable)!, Path.GetDirectoryName(key.AssemblyPath)!, _shadowRoot, resourceIndex)).ConfigureAwait(false);
        return (Path.Combine(shadow.Path, key.Profile.HostExecutable), Path.Combine(shadow.Path, Path.GetFileName(key.AssemblyPath)), shadow);
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
        var (executable, assemblyPath, shadow) = await PrepareHostAsync(key).ConfigureAwait(false);

        RenderHostConnection connection;
        try
        {
            connection = await StartWithFallbackAsync(key, executable, assemblyPath).ConfigureAwait(false);
        }
        catch
        {
            shadow?.Dispose(); // no process uses it
            throw;
        }

        if (shadow is not null)
        {
            lock (_lock)
            {
                _shadows[connection] = shadow;
            }
        }

        var info = connection.Info;
        if (info.FrameworkId != key.Profile.Id.ToString())
        {
            // A damaged install (one host's files in another's folder) must not render a project with the wrong version.
            await DisposeHostAsync(Task.FromResult(connection)).ConfigureAwait(false);
            throw new InvalidOperationException($"The renderer in {Path.GetDirectoryName(executable)} renders {info.FrameworkId}, not {key.Profile.DisplayName}. Reinstall xDesigner.");
        }

        // A project on a newer version than the renderer may use what the renderer doesn't have (review M19).
        if (key.AssemblyPath is not null && OlderThanProject(key, info.FrameworkVersion) is { } projectVersion && _versionWarned.TryAdd(key.Id, true))
        {
            VersionWarning?.Invoke($"{Path.GetFileNameWithoutExtension(key.AssemblyPath)} uses {key.Profile.DisplayName} {projectVersion}, and the designer's renderer has "
                + $"{info.FrameworkVersion}. Controls or properties new in {projectVersion} may not show; update xDesigner to design with the newer version.");
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

    /// <summary>
    /// Starts the host with the project's App, and when that App fails, exits (a framework-dependent WinUI app's bootstrapper,
    /// review M23) or hangs while starting (review M18), again without it (the default theme, §16).
    /// </summary>
    private async Task<RenderHostConnection> StartWithFallbackAsync(HostKey key, string executable, string? assemblyPath)
    {
        try
        {
            return await RenderHostConnection.StartAsync(
                new RenderHostStartInfo(executable, assemblyPath, ProjectDirectory: key.ProjectDirectory),
                key.AssemblyPath is not null ? UserAppStartTimeout : null).ConfigureAwait(false);
        }
        catch (RenderHostStartException ex) when ((ex.IsSetupFailure || ex.TimedOut || ex.ExitCode is not null) && key.AssemblyPath is not null)
        {
            Log?.Invoke($"The project's App {(ex.TimedOut ? "didn't finish starting" : "could not start")} inside the renderer; using the default theme instead.{Environment.NewLine}{FirstLines(ex.ErrorOutput, 3)}");
            try
            {
                return await RenderHostConnection.StartAsync(new RenderHostStartInfo(executable, assemblyPath, UseUserApplication: false, key.ProjectDirectory)).ConfigureAwait(false);
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
    }

    /// <summary>A newer project framework version (major.minor) than the renderer's, or null.</summary>
    private static string? OlderThanProject(HostKey key, string hostVersion)
    {
        var project = FrameworkDetector.FromBuildOutput(key.AssemblyPath!).Version;
        var host = Version.TryParse(hostVersion.Split('-', '+')[0], out var v) ? v : null;
        return project is not null && host is not null && key.Profile.Family != FrameworkFamily.WinUI
            && (project.Major, project.Minor).CompareTo((host.Major, host.Minor)) > 0
            ? $"{project.Major}.{project.Minor}"
            : null;
    }

    /// <summary>A project needs a newer framework version than the renderer has (shown to the user).</summary>
    public event Action<string>? VersionWarning;

    private static string FirstLines(string text, int count) =>
        string.Join(Environment.NewLine, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(count).Select(l => "  " + l.TrimEnd('\r')));
}
