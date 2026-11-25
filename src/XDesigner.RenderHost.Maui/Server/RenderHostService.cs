using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using XDesigner.Protocol;
using XDesigner.Protocol.Metadata;
using XDesigner.Protocol.Rendering;
using XDesigner.RenderHost.Hosting;
using XDesigner.RenderHost.Metadata;
using XDesigner.RenderHost.Rendering;

namespace XDesigner.RenderHost.Server;

/// <summary>The RPC target: implements <see cref="IRenderHost"/> on top of <see cref="HostApp"/> (all work on its UI thread).</summary>
internal sealed class RenderHostService : IRenderHost
{
    private readonly HostApp _host;
    private readonly MauiTypeResolver _resolver = new();
    private readonly MauiSanitizer _sanitizer;
    private readonly MetadataProvider _metadata;
    private readonly PropertyMetadataProvider _properties = new();
    private readonly EventMetadataProvider _events = new();
    private readonly ConcurrentDictionary<string, MauiRenderer> _renderers = new();
    private readonly TaskCompletionSource _shutdownRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RenderHostService(HostApp host)
    {
        _host = host;
        _sanitizer = new MauiSanitizer(_resolver, host.UserAssembly);
        _metadata = new MetadataProvider(_resolver);
    }

    /// <summary>Completes when the client calls <see cref="ShutdownAsync"/>.</summary>
    public Task ShutdownRequested => _shutdownRequested.Task;

    public Task<HostInfo> HandshakeAsync(int clientProtocolVersion, CancellationToken cancellationToken) =>
        Task.FromResult(new HostInfo(
            ProtocolInfo.Version,
            Program.FrameworkId,
            Program.MauiPackageVersion,
            Application.Current?.GetType().FullName ?? "",
            _host.BuilderSource,
            false, // MAUI has no design mode a host can switch on
            _host.UserAssembly?.GetName().Name,
            _host.Warnings.ToArray()));

    public Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        var renderer = _renderers.GetOrAdd(request.DocumentId, _ => new MauiRenderer(_host, _sanitizer));
        return _host.InvokeTaskAsync(() => renderer.RenderAsync(request));
    }

    public Task<ControlTypeInfo[]> GetControlTypesAsync(CancellationToken cancellationToken) =>
        _host.InvokeAsync(_metadata.GetControlTypes);

    public Task<PropertyInfoDto[]> GetPropertiesAsync(string typeFullName, CancellationToken cancellationToken) =>
        _host.InvokeAsync(() => _metadata.FindType(typeFullName) is { } type
            ? _properties.For(type).Select(p => p.Info).ToArray()
            : []);

    public Task<PropertyValueDto[]> GetValuesAsync(string documentId, int designId, CancellationToken cancellationToken) =>
        _host.InvokeAsync(() =>
            _renderers.TryGetValue(documentId, out var renderer) && ValueReader.Find(renderer.CurrentRoot, designId) is { } element
                ? ValueReader.Read(element, _properties.For(element.GetType()), renderer.WrittenProperties.GetValueOrDefault(designId))
                : []);

    public Task<EventInfoDto[]> GetEventsAsync(string typeFullName, CancellationToken cancellationToken) =>
        _host.InvokeAsync(() => _metadata.FindType(typeFullName) is { } type ? _events.For(type) : []);

    /// <summary>The project's font aliases (<c>OpenSansRegular</c>, from MauiProgram's ConfigureFonts) first, then installed fonts.</summary>
    public async Task<string[]> GetFontFamiliesAsync(CancellationToken cancellationToken)
    {
        var aliases = await _host.InvokeAsync(RegisteredFontAliases);
        var installed = await Task.Run(FontList.Families, cancellationToken);
        return aliases.Concat(installed.Except(aliases, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    public async Task CloseDocumentAsync(string documentId, CancellationToken cancellationToken)
    {
        if (_renderers.TryRemove(documentId, out var renderer))
        {
            await _host.InvokeAsync(() =>
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

    /// <summary>
    /// The aliases fonts were registered with. MAUI's font registrar doesn't list them, so its dictionaries are read by
    /// reflection; if that ever fails, the list is just shorter.
    /// </summary>
    private string[] RegisteredFontAliases()
    {
        try
        {
            var registrar = _host.MauiContext.Services.GetService(typeof(IFontRegistrar));
            if (registrar is null)
            {
                return [];
            }

            // The registrations only: the lookup cache also holds any name that was looked up, misspelled ones too (review L44).
            return registrar.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(f => f.Name is "_embeddedFonts" or "_nativeFonts")
                .Select(f => f.GetValue(registrar))
                .OfType<IDictionary>()
                .SelectMany(d => d.Keys.OfType<string>())
                .Where(k => !k.Contains('.', StringComparison.Ordinal)) // aliases, not file names
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }
}
