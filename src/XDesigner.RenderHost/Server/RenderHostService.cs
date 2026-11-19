using System.Collections.Concurrent;
using XDesigner.Protocol;
using XDesigner.Protocol.Metadata;
using XDesigner.Protocol.Rendering;
using XDesigner.RenderHost.Hosting;
using XDesigner.RenderHost.Metadata;
using XDesigner.RenderHost.Rendering;

namespace XDesigner.RenderHost.Server;

/// <summary>The RPC target: implements <see cref="IRenderHost"/> on top of <see cref="HostRuntime"/>.</summary>
internal sealed class RenderHostService(HostRuntime runtime) : IRenderHost
{
    private readonly XamlTypeResolver _resolver = new();
    private readonly MetadataProvider _metadata = new();
    private readonly PropertyMetadataProvider _properties = new();
    private readonly EventMetadataProvider _events = new();
    private readonly ConcurrentDictionary<string, XamlRenderer> _renderers = new();
    private readonly TaskCompletionSource _shutdownRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the client calls <see cref="ShutdownAsync"/>.</summary>
    public Task ShutdownRequested => _shutdownRequested.Task;

    public Task<HostInfo> HandshakeAsync(int clientProtocolVersion, CancellationToken cancellationToken) =>
        Task.FromResult(new HostInfo(
            ProtocolInfo.Version,
            HostVersions.AvaloniaVersion.ToString(),
            runtime.ApplicationTypeName,
            runtime.BuilderSource,
            runtime.IsDesignMode,
            runtime.UserAssembly?.GetName().Name,
            runtime.Warnings.ToArray()));

    public Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        // All rendering happens on the one Avalonia UI thread, so requests are naturally serialized.
        // The app sends at most one request per document at a time (latest wins on its side, §8.2).
        var renderer = _renderers.GetOrAdd(request.DocumentId, _ => new XamlRenderer(runtime.UserAssembly, _resolver));
        return runtime.InvokeAsync(() => renderer.Render(request));
    }

    public Task<ControlTypeInfo[]> GetControlTypesAsync(CancellationToken cancellationToken) =>
        runtime.InvokeAsync(_metadata.GetControlTypes);

    public Task<PropertyInfoDto[]> GetPropertiesAsync(string typeFullName, CancellationToken cancellationToken) =>
        runtime.InvokeAsync(() => _metadata.FindType(typeFullName) is { } type
            ? _properties.For(type).Select(p => p.Info).ToArray()
            : []);

    public Task<PropertyValueDto[]> GetValuesAsync(string documentId, int designId, CancellationToken cancellationToken) =>
        runtime.InvokeAsync(() =>
        {
            if (!_renderers.TryGetValue(documentId, out var renderer)
                || ValueReader.Find(renderer.CurrentRoot, designId) is not { } element)
            {
                return Array.Empty<PropertyValueDto>();
            }

            return ValueReader.Read(element, _properties.For(element.GetType()));
        });

    public Task<EventInfoDto[]> GetEventsAsync(string typeFullName, CancellationToken cancellationToken) =>
        runtime.InvokeAsync(() => _metadata.FindType(typeFullName) is { } type ? _events.For(type) : []);

    public Task<string[]> GetFontFamiliesAsync(CancellationToken cancellationToken) =>
        runtime.InvokeAsync(() =>
        {
            string[] bundled = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Avalonia.Fonts.Inter")
                ? ["fonts:Inter#Inter"]
                : [];
            var installed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var family in Avalonia.Media.FontManager.Current.SystemFonts)
                {
                    installed.Add(family.Name);
                }
            }
            catch
            {
                // No font list is fine: the editor still accepts typed names.
            }

            return bundled.Concat(installed).ToArray();
        });

    public async Task CloseDocumentAsync(string documentId, CancellationToken cancellationToken)
    {
        if (_renderers.TryRemove(documentId, out var renderer))
        {
            await runtime.InvokeAsync(() =>
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
