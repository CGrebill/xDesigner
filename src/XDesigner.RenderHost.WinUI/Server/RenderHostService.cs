using System.Collections.Concurrent;
using XDesigner.Protocol;
using XDesigner.Protocol.Metadata;
using XDesigner.Protocol.Rendering;
using XDesigner.RenderHost.Hosting;
using XDesigner.RenderHost.Metadata;
using XDesigner.RenderHost.Rendering;

namespace XDesigner.RenderHost.Server;

/// <summary>The RPC target: implements <see cref="IRenderHost"/> on top of <see cref="HostApp"/> (all work on its UI thread).</summary>
internal sealed class RenderHostService(HostApp host) : IRenderHost
{
    public const string FrameworkId = "WinUI3";

    private readonly RenderCopySanitizer _sanitizer = new(new XamlTypeResolver(), new ProjectResources(host.ProjectDirectory, AppContext.BaseDirectory));
    private readonly MetadataProvider _metadata = new();
    private readonly PropertyMetadataProvider _properties = new();
    private readonly EventMetadataProvider _events = new();
    private readonly ConcurrentDictionary<string, XamlRenderer> _renderers = new();
    private readonly ConcurrentDictionary<string, bool> _closed = new();
    private readonly TaskCompletionSource _shutdownRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the client calls <see cref="ShutdownAsync"/>.</summary>
    public Task ShutdownRequested => _shutdownRequested.Task;

    public Task<HostInfo> HandshakeAsync(int clientProtocolVersion, CancellationToken cancellationToken) =>
        Task.FromResult(new HostInfo(
            ProtocolInfo.Version,
            FrameworkId,
            Program.WindowsAppSdkVersion,
            typeof(HostApp).FullName!,
            host.BuilderSource,
            false, // WinUI has no design mode a host can switch on
            host.UserAssembly?.GetName().Name,
            host.Warnings.ToArray()));

    public Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        // A render that arrives after its document was closed would leave a live tree on the surface (review L40).
        if (_closed.ContainsKey(request.DocumentId))
        {
            return Task.FromResult(RenderResult.Failed(request.Version, new RenderError("The document was closed.")));
        }

        var renderer = _renderers.GetOrAdd(request.DocumentId, _ => new XamlRenderer(host, _sanitizer));
        return host.InvokeTaskAsync(() => renderer.RenderAsync(request));
    }

    public Task<ControlTypeInfo[]> GetControlTypesAsync(CancellationToken cancellationToken) =>
        host.InvokeAsync(_metadata.GetControlTypes);

    public Task<PropertyInfoDto[]> GetPropertiesAsync(string typeFullName, CancellationToken cancellationToken) =>
        host.InvokeAsync(() => _metadata.FindType(typeFullName) is { } type
            ? _properties.For(type).Select(p => p.Info).ToArray()
            : []);

    public Task<PropertyValueDto[]> GetValuesAsync(string documentId, int designId, CancellationToken cancellationToken) =>
        host.InvokeAsync(() =>
            _renderers.TryGetValue(documentId, out var renderer) && ValueReader.Find(renderer.CurrentRoot, designId) is { } element
                ? WithWindowTitle(renderer, element, ValueReader.Read(element, _properties.For(element.GetType())))
                : []);

    /// <summary>A Window document's root is drawn as a Grid: its Title comes from the XAML (review L39).</summary>
    private static PropertyValueDto[] WithWindowTitle(XamlRenderer renderer, Microsoft.UI.Xaml.DependencyObject element, PropertyValueDto[] values) =>
        renderer.WindowTitle is { } title && ReferenceEquals(element, renderer.CurrentRoot)
            ? [.. values, new PropertyValueDto("Title", title, ValueSource.Local)]
            : values;

    public Task<EventInfoDto[]> GetEventsAsync(string typeFullName, CancellationToken cancellationToken) =>
        host.InvokeAsync(() => _metadata.FindType(typeFullName) is { } type ? _events.For(type) : []);

    public Task<string[]> GetFontFamiliesAsync(CancellationToken cancellationToken) =>
        Task.Run(FontList.Families, cancellationToken);

    public async Task CloseDocumentAsync(string documentId, CancellationToken cancellationToken)
    {
        _closed[documentId] = true;
        if (_renderers.TryRemove(documentId, out var renderer))
        {
            await host.InvokeAsync(() =>
            {
                renderer.Close();
                return true;
            });
        }
    }

    public Task ShutdownAsync()
    {
        _shutdownRequested.TrySetResult();
        return Task.CompletedTask;
    }
}
