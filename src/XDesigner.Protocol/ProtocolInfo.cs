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
    public const int Version = 5;

    /// <summary>Prefix for the named pipe the render host listens on.</summary>
    public const string PipeNamePrefix = "xDesigner-";

    /// <summary>The only Avalonia major version the designer supports (decision D5).</summary>
    public const int SupportedAvaloniaMajorVersion = 12;

    /// <summary>
    /// XML namespace (declared as <c>xmlns:dsn</c>) of the attached property <c>DesignId.Value</c> that the app
    /// adds to every element in a render copy (§5.4). Defined by the render host's <c>DesignTime.DesignId</c>.
    /// </summary>
    public const string DesignIdXmlNamespace =
        "clr-namespace:XDesigner.RenderHost.DesignTime;assembly=XDesigner.RenderHost";

    /// <summary>Attribute local name of the design id (<c>dsn:DesignId.Value</c>).</summary>
    public const string DesignIdAttributeName = "DesignId.Value";

    /// <summary>Builds a unique pipe name for one render host instance.</summary>
    public static string CreatePipeName(int appProcessId) =>
        $"{PipeNamePrefix}{appProcessId}-{Guid.NewGuid():N}";
}
