using XDesigner.App.Services;
using XDesigner.Core.CodeGen;
using XDesigner.Core.Editing;
using XDesigner.Core.Layout;
using XDesigner.Core.Metadata;
using XDesigner.Core.Project;
using XDesigner.Core.Rendering;
using XDesigner.Core.Toolbox;
using XDesigner.Core.Xaml;
using XDesigner.Protocol.Metadata;
using XDesigner.Protocol.Rendering;
using CommunityToolkit.Mvvm.ComponentModel;

namespace XDesigner.App.ViewModels;

/// <summary>
/// One open <c>.axaml</c> file: its <see cref="XamlDocument"/> (source of truth), selection, undo history,
/// the control catalog from its renderer, and the latest rendered frame and layout.
/// </summary>
public sealed partial class DocumentViewModel : ObservableObject
{
    public static readonly double[] ZoomLevels = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];

    private readonly RenderHostManager _hosts;
    private readonly RenderScheduler _scheduler;
    private long _version;
    private double _lastScale = 1;
    private bool _catalogRequested;

    public DocumentViewModel(string filePath, XamlDocument xaml, ProjectOutput? project, RenderHostManager hosts)
    {
        FilePath = filePath;
        Xaml = xaml;
        Project = project;
        ApplyProjectProfile(project);
        _hosts = hosts;
        Undo = new UndoManager(xaml);
        Actions = new DesignerActions(xaml, Undo);
        _scheduler = new RenderScheduler(RenderAsync);
        _scheduler.Rendered += OnRendered;
        _scheduler.Failed += OnFailed;
        Xaml.Changed += OnXamlChanged;
        DiskText = xaml.ToXaml();
        Selection.Changed += (_, _) =>
        {
            // Selecting something in another tab of a TabControl opens that tab on the surface (design-only, §9.4).
            if (Preview.Follow(Xaml, Catalog, Selection.Selected))
            {
                RenderRequested?.Invoke();
            }
        };
        Undo.StateChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(DisplayTitle));
        };
    }

    /// <summary>The document model.</summary>
    public XamlDocument Xaml { get; }

    public UndoManager Undo { get; }

    public DesignerActions Actions { get; }

    /// <summary>Selected elements; shared by the surface, outline and properties window.</summary>
    public SelectionService Selection { get; } = new();

    /// <summary>Design-only view state: which tab of each TabControl the surface shows.</summary>
    public DesignPreview Preview { get; } = new();

    /// <summary>Where each element was laid out in the frame on screen.</summary>
    [ObservableProperty]
    public partial LayoutIndex Layout { get; private set; } = LayoutIndex.Empty;

    /// <summary>Control types known to this document's renderer (toolbox, drop rules); empty until the first render.</summary>
    [ObservableProperty]
    public partial ControlCatalog Catalog { get; private set; } = ControlCatalog.Empty;

    /// <summary>The user project's assembly name as the renderer loaded it (for the toolbox's Project group).</summary>
    public string? UserAssemblyName { get; private set; }

    /// <summary>Raised when elements were added, removed or moved (the outline rebuilds).</summary>
    public event EventHandler? StructureChanged;

    /// <summary>Identifies this document to the render host.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public string FilePath { get; }

    public string Title => Path.GetFileName(FilePath);

    /// <summary>Tab header: the file name, with a dot when there are unsaved changes.</summary>
    public string DisplayTitle => IsDirty ? $"{Title} ●" : Title;

    public bool IsDirty => Undo.IsDirty;

    public ProjectOutput? Project { get; private set; }

    /// <summary>The file's text as last loaded or saved by the designer (to tell its own writes from external edits, §11.6).</summary>
    public string DiskText { get; set; }

    /// <summary>Switches to another build of the project (or none): re-renders with it and reloads the control catalog.</summary>
    public void UseProject(ProjectOutput? project)
    {
        var changed = !string.Equals(project?.AssemblyPath, Project?.AssemblyPath, StringComparison.OrdinalIgnoreCase);
        Project = project;

        changed |= ApplyProjectProfile(project);

        if (changed)
        {
            RendererRestarted();
        }
    }

    /// <summary>
    /// The project says which version of the document's framework it is (Avalonia 11 or 12): that picks the renderer.
    /// Returns whether the profile changed.
    /// </summary>
    private bool ApplyProjectProfile(ProjectOutput? project)
    {
        if (project?.Profile is { } profile && profile.Family == Xaml.Profile.Family && profile != Xaml.Profile)
        {
            Xaml.Profile = profile;
            return true;
        }

        return false;
    }

    /// <summary>The renderer for this document was restarted (rebuild): fetch the catalog again and re-render.</summary>
    public void RendererRestarted()
    {
        _catalogRequested = false;
        RenderRequested?.Invoke();
    }

    /// <summary>The project assembly the renderer loads (custom controls, App styles), if the project has been built.</summary>
    public string? UserAssemblyPath => Project?.AssemblyPath;

    /// <summary>The render host this document needs: its framework version's, loading its project's assembly.</summary>
    public HostKey HostKey => new(Xaml.Profile, UserAssemblyPath);

    [ObservableProperty]
    public partial double Zoom { get; set; } = 1;

    [ObservableProperty]
    public partial ThemeVariantKind Theme { get; set; } = ThemeVariantKind.Default;

    /// <summary>Last successful render (the frame on screen).</summary>
    [ObservableProperty]
    public partial RenderResult? Frame { get; private set; }

    [ObservableProperty]
    public partial bool IsRendering { get; private set; }

    /// <summary>Why the latest render failed; null when the frame on screen is current.</summary>
    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>True when the renderer itself is unavailable (offer Retry), as opposed to a XAML error.</summary>
    [ObservableProperty]
    public partial bool IsRendererError { get; private set; }

    /// <summary>Raised when the view should re-render (zoom, theme or the document changed).</summary>
    public event Action? RenderRequested;

    /// <summary>Preview size (§8.4, view only): null shows the document at its own size.</summary>
    [ObservableProperty]
    public partial SizeDto? PreviewSize { get; set; }

    /// <summary>How long one render may take before the renderer is considered stuck (§14.2).</summary>
    public static TimeSpan RenderTimeout { get; } = TimeSpan.FromSeconds(10);

    /// <summary>The project folder, when the document belongs to a project (images go to its Assets folder, §12).</summary>
    public string? ProjectDirectory => Project is { } p ? Path.GetDirectoryName(p.ProjectPath) : null;

    /// <summary>
    /// The document's place in the project assembly (<c>avares://App/Views/MainWindow.axaml</c>), so <c>/Assets/…</c> images
    /// resolve in the renderer; null without a built project.
    /// </summary>
    public string? BaseUri
    {
        get
        {
            if (Project is not { AssemblyPath: not null } project || ProjectDirectory is not { } dir)
            {
                return null;
            }

            var relative = Path.GetRelativePath(dir, FilePath).Replace('\\', '/');
            return relative.StartsWith("../", StringComparison.Ordinal)
                ? null
                : $"avares://{Uri.EscapeDataString(project.AssemblyName)}/{string.Join('/', relative.Split('/').Select(Uri.EscapeDataString))}";
        }
    }

    /// <summary>Hides or shows a control on the surface only (outline eye icon, §9.6).</summary>
    public void SetHidden(int id, bool hidden)
    {
        if (Preview.SetHidden(id, hidden))
        {
            HiddenChanged?.Invoke(this, EventArgs.Empty);
            RenderRequested?.Invoke();
        }
    }

    /// <summary>Raised when a control was hidden or shown on the surface.</summary>
    public event EventHandler? HiddenChanged;

    partial void OnZoomChanged(double value) => RenderRequested?.Invoke();

    partial void OnThemeChanged(ThemeVariantKind value) => RenderRequested?.Invoke();

    partial void OnPreviewSizeChanged(SizeDto? value) => RenderRequested?.Invoke();

    /// <summary>Queues a render at <paramref name="rasterizationScale"/> × <see cref="Zoom"/>.</summary>
    public void Render(double rasterizationScale)
    {
        var renderCopy = Xaml.BuildRenderCopy(Preview.Overrides(Xaml, Catalog));
        _lastScale = rasterizationScale;
        IsRendering = true;
        _scheduler.Submit(new RenderRequest(++_version, renderCopy, rasterizationScale * Zoom, Theme, PreviewSize, Id, BaseUri));
    }

    /// <summary>
    /// A document reloaded from disk shows the previous version's picture until its own first render arrives, and keeps
    /// it (dimmed) when that render fails (§8.5). Its layout stays empty: the old element ids mean nothing here.
    /// </summary>
    public void ShowPreviousFrame(RenderResult? frame)
    {
        if (Frame is null && frame is { Success: true })
        {
            Frame = frame;
        }
    }

    /// <summary>Retry after the renderer stopped.</summary>
    public void Retry()
    {
        _hosts.ResetRestartLimit(HostKey);
        Render(_lastScale);
    }

    /// <summary>Adds a toolbox control as planned for a drop, and selects it.</summary>
    public void AddControl(ToolboxItem item, DropPlan plan)
    {
        var id = Actions.AddControl(item, plan);
        Selection.Select(id);
    }

    /// <summary>
    /// Adds a toolbox control without dragging (double-click): into the selected container, or the nearest one
    /// above the selection that can take it, near its top-left corner. Returns why not if nothing can.
    /// </summary>
    public string? AddControlAtSelection(ToolboxItem item)
    {
        var start = (Selection.Primary is { } id ? Xaml.FindById(id) : null) ?? Xaml.Root;
        for (var node = start; node is not null; node = node.Parent)
        {
            if (Layout.Find(node.Id) is not { } bounds)
            {
                continue;
            }

            var plan = DropPlanner.PlanInto(Xaml, Catalog, Layout, node.Id, bounds.Bounds.X + 12, bounds.Bounds.Y + 12);
            if (plan is not null)
            {
                AddControl(item, plan);
                return null;
            }
        }

        return "Nothing selected can hold another control. Select a panel (Grid, StackPanel, Canvas…) first.";
    }

    /// <summary>Moves one element as planned for a drag (reposition, reorder or into another container).</summary>
    public void MoveTo(int id, DropPlan plan) => Actions.MoveTo(id, plan, Layout);

    /// <summary>Shifts elements inside their positional containers.</summary>
    public void MoveBy(IReadOnlyCollection<int> ids, double dx, double dy) => Actions.MoveBy(ids, dx, dy, Layout, Catalog);

    public void Resize(int id, RectDto bounds, ResizeEdges edges)
    {
        Actions.Resize(id, bounds, edges, Layout, Catalog);
        if (id == Xaml.Root.Id)
        {
            PreviewSize = null; // the document now has the size it was dragged to; a preview size would hide it
        }
    }

    /// <summary>One step along <see cref="ZoomLevels"/> from the current zoom (which may be between levels after Fit).</summary>
    public void StepZoom(int direction)
    {
        Zoom = direction > 0
            ? ZoomLevels.FirstOrDefault(z => z > Zoom + 0.001, ZoomLevels[^1])
            : ZoomLevels.LastOrDefault(z => z < Zoom - 0.001, ZoomLevels[0]);
    }

    /// <summary>Raised when the view should pick a zoom that fits the whole frame in the window (View ▸ Fit).</summary>
    public event EventHandler? FitRequested;

    public void RequestFit() => FitRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Arrow keys: move the selection by a few DIPs, or reorder it inside a stack.</summary>
    public void Nudge(double dx, double dy) => Actions.Nudge(Selection.Selected.ToList(), dx, dy, Layout, Catalog);

    /// <summary>Ctrl+A: select the primary selection and all its siblings (or the root's children).</summary>
    public void SelectAllSiblings()
    {
        var anchor = Selection.Primary is { } id ? Xaml.FindById(id) : null;
        var parent = anchor?.Parent ?? Xaml.Root;
        var siblings = parent.Children.Select(c => c.Id).ToList();
        if (siblings.Count > 0)
        {
            Selection.Set(siblings);
        }
    }

    /// <summary>The selection as clipboard text, or null if only the root is selected.</summary>
    public string? CopySelection() => Actions.Copy(Selection.Selected);

    /// <summary>Pastes clipboard text and selects the result. Returns why not when it can't.</summary>
    public string? Paste(string text)
    {
        var ids = Actions.Paste(text, Selection.Primary, Layout, Catalog, out var problem);
        if (ids is not null)
        {
            Selection.Set(ids);
        }

        return problem;
    }

    /// <summary>Deletes the selected controls (never the root).</summary>
    public void DeleteSelection()
    {
        var parent = Selection.Primary is { } id ? Xaml.FindById(id)?.Parent?.Id : null;
        if (Actions.Delete(Selection.Selected) > 0 && parent is { } p && Xaml.FindById(p) is not null)
        {
            Selection.Select(p);
        }
    }

    /// <summary>Sets (or with null, resets) a property on elements as one undo step (§9.2). False if nothing changed.</summary>
    public bool SetProperty(IReadOnlyCollection<int> ids, string key, string? value) => Actions.SetProperty(ids, key, value, Catalog);

    /// <summary>Renames an element; returns why not when the name can't be used.</summary>
    public string? Rename(int id, string name) => Actions.Rename(id, name);

    /// <summary>Properties of a control type from this document's renderer; null while it isn't running or on failure.</summary>
    public async Task<IReadOnlyList<PropertyInfoDto>?> GetPropertiesAsync(string typeFullName)
    {
        if (_hosts.TryGetRunning(HostKey) is not { } host)
        {
            return null;
        }

        try
        {
            return await _hosts.GetPropertiesAsync(host, typeFullName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Effective values of an element in the latest render, by property key; null when unavailable.</summary>
    public async Task<IReadOnlyDictionary<string, PropertyValueDto>?> GetValuesAsync(int id)
    {
        if (_hosts.TryGetRunning(HostKey) is not { } host)
        {
            return null;
        }

        try
        {
            var values = (await host.Host.GetValuesAsync(Id, id, CancellationToken.None)).GroupBy(v => v.Key).ToDictionary(g => g.Key, g => g.First());

            // Hidden with the outline's eye icon: the renderer has IsVisible=False, but that's the designer's doing, not the file's.
            if (Preview.IsHidden(id) && values.TryGetValue("IsVisible", out var visible) && Xaml.FindById(id)?.Element.Attribute("IsVisible") is null)
            {
                values["IsVisible"] = visible with { Value = "True", Source = ValueSource.Default };
            }

            return values;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Raised after every attempt to connect a handler (success or not): the surface shows the confirmation (D9).</summary>
    public event Action<EventHandlerOutcome>? HandlerConnected;

    /// <summary>One line for Output ▸ Events: handlers created, connected, disconnected, or why not.</summary>
    public event Action<string>? EventLogged;

    /// <summary>Events of a control type from this document's renderer; null while it isn't running or on failure.</summary>
    public async Task<IReadOnlyList<EventInfoDto>?> GetEventsAsync(string typeFullName)
    {
        if (_hosts.TryGetRunning(HostKey) is not { } host)
        {
            return null;
        }

        try
        {
            return await _hosts.GetEventsAsync(host, typeFullName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Connects an event to a code-behind method (default name <c>{control}_{Event}</c>), creating the method if needed (§10).
    /// </summary>
    public EventHandlerOutcome ConnectHandler(IReadOnlyList<int> ids, EventInfoDto evt, string? methodName)
    {
        var outcome = Actions.ConnectHandler(ids, evt, methodName, FilePath, Catalog);
        Report(outcome);
        return outcome;
    }

    /// <summary>Double-click: creates (or shows) the handler of the control's default event, e.g. Button.Click.</summary>
    public async Task CreateDefaultHandlerAsync(int id)
    {
        if (Xaml.FindById(id) is not { } node)
        {
            return;
        }

        var type = Catalog.Resolve(node);
        if (type?.DefaultEventName is not { } eventName)
        {
            Report(EventHandlerOutcome.Failed($"{node.TypeName} has no default event. Pick one on the Events tab of the Properties window.", "", node.TypeName));
            return;
        }

        if (await GetEventsAsync(type.FullName) is not { } events || events.FirstOrDefault(e => e.Name == eventName) is not { } evt)
        {
            Report(EventHandlerOutcome.Failed($"The renderer couldn't describe {node.TypeName}.{eventName}, so no handler was created.", eventName, node.TypeName));
            return;
        }

        // Already connected: reuse (and show) that method rather than making a second one.
        ConnectHandler([id], evt, (string?)node.Element.Attribute(eventName));
    }

    /// <summary>Removes the event attribute (the method stays in the .cs file, D10).</summary>
    public bool DisconnectHandler(IReadOnlyList<int> ids, string eventName)
    {
        var handler = ids.Select(Xaml.FindById).Select(n => (string?)n?.Element.Attribute(eventName)).FirstOrDefault(h => h is not null);
        if (!Actions.DisconnectHandler(ids, eventName))
        {
            return false;
        }

        EventLogged?.Invoke($"Disconnected {eventName} from {handler} in {Title}. The method is still in {Path.GetFileName(CodeBehindPath)}.");
        return true;
    }

    /// <summary>Methods of the code-behind class that can handle <paramref name="evt"/> (for the Events tab's list).</summary>
    public IReadOnlyList<(string Name, int Line)> CompatibleMethods(EventInfoDto evt)
    {
        if ((string?)Xaml.Root.Element.Attribute(System.Xml.Linq.XName.Get("Class", XamlNamespaces.Xaml)) is not { } className)
        {
            return [];
        }

        try
        {
            return CodeBehindWriter.CompatibleMethods(CodeBehindPath, className, HandlerSignature.From(evt));
        }
        catch (CodeBehindException)
        {
            return [];
        }
    }

    /// <summary>The <c>.axaml.cs</c> next to this file.</summary>
    public string CodeBehindPath => CodeBehindWriter.CodeBehindPathFor(FilePath);

    private void Report(EventHandlerOutcome outcome)
    {
        HandlerConnected?.Invoke(outcome);
        EventLogged?.Invoke(outcome.Success && outcome.Handler is { Created: true }
            ? $"{outcome.Message} Undo removes only the XAML link; the method stays."
            : outcome.Message);
    }

    /// <summary>Font names for the FontFamily editor.</summary>
    public async Task<IReadOnlyList<string>> GetFontFamiliesAsync()
    {
        try
        {
            return _hosts.TryGetRunning(HostKey) is { } host ? await _hosts.GetFontFamiliesAsync(host) : [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// The file's text when another program changed it since the designer last loaded or saved it; null otherwise
    /// (also when it can't be read, or no longer exists).
    /// </summary>
    public string? TextChangedOnDisk()
    {
        try
        {
            var text = File.ReadAllText(FilePath);
            return text == DiskText ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save()
    {
        Xaml.Save(FilePath);
        DiskText = Xaml.ToXaml();
        Undo.MarkSaved();
    }

    public async Task CloseAsync()
    {
        _scheduler.Cancel();
        if (_hosts.TryGetRunning(HostKey) is { } host)
        {
            try
            {
                await host.Host.CloseDocumentAsync(Id, CancellationToken.None);
            }
            catch
            {
                // Host already gone.
            }
        }
    }

    /// <summary>
    /// Renders on the document's host. If the connection goes away mid-request (the renderer was restarted for a rebuild,
    /// or crashed), the request is sent once more to a fresh host before anything is reported: a planned restart must
    /// never show "renderer unavailable".
    /// </summary>
    private async Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var key = HostKey;
            var host = await _hosts.GetAsync(key, cancellationToken).ConfigureAwait(false);
            try
            {
                // Watchdog (§14.2): a control stuck in a loop blocks the host's UI thread, and the call would never return.
                var render = host.Host.RenderAsync(request, cancellationToken);
                try
                {
                    return await render.WaitAsync(RenderTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _ = render.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); // observed: it fails once the host is gone
                    await _hosts.AbandonAsync(host, key).ConfigureAwait(false);
                    throw new TimeoutException(
                        $"Drawing {Title} took more than {RenderTimeout.TotalSeconds:0} seconds (a control may be stuck in a loop), so the renderer was stopped. Undo the last change, or fix the control and rebuild, then press Retry.");
                }
            }
            catch (Exception ex) when (ex is not TimeoutException && attempt == 0 && !cancellationToken.IsCancellationRequested && (!host.IsAlive || key != HostKey))
            {
                // The host this request went to is gone (or the document switched builds): try the current one.
            }
        }
    }

    private void OnRendered(RenderResult result)
    {
        IsRendering = _scheduler.IsBusy;
        if (result.Success)
        {
            Frame = result;
            Layout = new LayoutIndex(result.Root);
            ErrorMessage = null;
            IsRendererError = false;
            _ = LoadCatalogAsync();
        }
        else
        {
            var e = result.Error!;
            var where = e.Line is { } line ? $" (line {line}{(e.Column is { } col ? $", column {col}" : "")})" : "";
            var who = e.OffendingType is null ? "" : $" in {e.OffendingType}";
            ShowError($"{e.Message}{where}{who}", rendererError: false);
        }
    }

    private async Task LoadCatalogAsync()
    {
        if (_catalogRequested)
        {
            return;
        }

        _catalogRequested = true;
        try
        {
            var host = await _hosts.GetAsync(HostKey);
            UserAssemblyName = host.Info.UserAssemblyName;
            Catalog = await _hosts.GetCatalogAsync(host);
        }
        catch
        {
            _catalogRequested = false; // try again after the next successful render
        }
    }

    private void OnFailed(RenderRequest request, Exception exception)
    {
        IsRendering = _scheduler.IsBusy;
        ShowError(exception is TimeoutException ? exception.Message : $"The renderer is unavailable: {exception.Message}", rendererError: true);
    }

    private void OnXamlChanged(object? sender, XamlDocumentChange change)
    {
        if (change.StructureChanged)
        {
            Selection.RemoveMissing(id => Xaml.FindById(id) is not null);
            StructureChanged?.Invoke(this, EventArgs.Empty);
        }

        RenderRequested?.Invoke();
    }

    private void ShowError(string message, bool rendererError)
    {
        ErrorMessage = message;
        IsRendererError = rendererError;
    }
}
