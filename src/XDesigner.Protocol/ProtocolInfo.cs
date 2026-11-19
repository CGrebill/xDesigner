namespace XDesigner.Protocol;

/// <summary>
/// Constants shared by both ends of the designer ⇄ render host connection.
/// See docs/ARCHITECTURE.md §4.
/// </summary>
public static class ProtocolInfo
{
    /// <summary>
    /// Bumped whenever <c>IRenderHost</c> or any DTO changes shape.
    /// A mismatch during the handshake means the render host is out of date.
    /// </summary>
    public const int Version = 6;

    /// <summary>Prefix for the named pipe the render host listens on.</summary>
    public const string PipeNamePrefix = "xDesigner-";

    /// <summary>
    /// XML namespace (declared as <c>xmlns:dsn</c>) of the design id <c>dsn:DesignId.Value</c> that the app adds to every
    /// element in a render copy (§5.4). It is a neutral marker: each render host maps it to its own attached property,
    /// whose real namespace depends on the framework and the host's assembly.
    /// </summary>
    public const string DesignIdXmlNamespace = "urn:xdesigner:design-id";

    /// <summary>Attribute local name of the design id (<c>dsn:DesignId.Value</c>).</summary>
    public const string DesignIdAttributeName = "DesignId.Value";

    /// <summary>Builds a unique pipe name for one render host instance.</summary>
    public static string CreatePipeName(int appProcessId) =>
        $"{PipeNamePrefix}{appProcessId}-{Guid.NewGuid():N}";
}
