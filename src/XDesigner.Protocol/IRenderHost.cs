using XDesigner.Protocol.Metadata;
using XDesigner.Protocol.Rendering;
using MessagePack;
using StreamJsonRpc;

namespace XDesigner.Protocol;

/// <summary>
/// RPC surface of the render host process. The host is the server; the designer app is the client.
/// See docs/ARCHITECTURE.md §4. Bump ProtocolInfo.Version whenever this interface or a DTO changes.
/// </summary>
public interface IRenderHost
{
    /// <summary>First call on every connection; the app checks <see cref="HostInfo.ProtocolVersion"/>.</summary>
    Task<HostInfo> HandshakeAsync(int clientProtocolVersion, CancellationToken cancellationToken);

    /// <summary>Renders one document. The host keeps the latest render of each <see cref="RenderRequest.DocumentId"/> alive.</summary>
    Task<RenderResult> RenderAsync(RenderRequest request, CancellationToken cancellationToken);

    /// <summary>Every public control type the host can see (Avalonia, packages, the user project); for the toolbox and drop rules.</summary>
    Task<ControlTypeInfo[]> GetControlTypesAsync(CancellationToken cancellationToken);

    /// <summary>Properties of a control type (by full name) that XAML can set, including common attached ones.</summary>
    Task<PropertyInfoDto[]> GetPropertiesAsync(string typeFullName, CancellationToken cancellationToken);

    /// <summary>
    /// Effective values of every property of one element in the document's latest render (by design id).
    /// Empty when the element isn't in the render (e.g. inside a closed tab).
    /// </summary>
    Task<PropertyValueDto[]> GetValuesAsync(string documentId, int designId, CancellationToken cancellationToken);

    /// <summary>Events of a control type (by full name) that XAML can connect to a handler, with their handler signatures.</summary>
    Task<EventInfoDto[]> GetEventsAsync(string typeFullName, CancellationToken cancellationToken);

    /// <summary>Font family names to offer in the FontFamily editor (bundled fonts first, then installed ones).</summary>
    Task<string[]> GetFontFamiliesAsync(CancellationToken cancellationToken);

    /// <summary>Forgets a closed document's live render.</summary>
    Task CloseDocumentAsync(string documentId, CancellationToken cancellationToken);

    /// <summary>Asks the host to exit cleanly.</summary>
    Task ShutdownAsync();
}

/// <summary>What the host reports about itself after starting.</summary>
/// <param name="FrameworkId">Which framework and major version it renders (<c>Avalonia12</c>), as the designer's profiles name it.</param>
/// <param name="FrameworkVersion">The exact framework version it runs (<c>12.1.6</c>).</param>
/// <param name="ApplicationType">Full name of the Application in use (the user's, or the designer's fallback).</param>
/// <param name="BuilderSource">How the app was configured: <c>BuildAvaloniaApp</c>, <c>App type</c> or <c>DesignApp</c>.</param>
/// <param name="UserAssemblyName">Name of the loaded user assembly, if any.</param>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record HostInfo(
    int ProtocolVersion,
    string FrameworkId,
    string FrameworkVersion,
    string ApplicationType,
    string BuilderSource,
    bool IsDesignMode,
    string? UserAssemblyName,
    string[] Warnings);

/// <summary>Wire format shared by both ends: length-prefixed MessagePack messages.</summary>
public static class RenderHostRpc
{
    public static JsonRpc CreateConnection(Stream stream) =>
        new(new LengthHeaderMessageHandler(stream, stream, new MessagePackFormatter()));
}
