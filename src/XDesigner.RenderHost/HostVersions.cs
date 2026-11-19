namespace XDesigner.RenderHost;

/// <summary>Version information reported to the designer during the handshake.</summary>
internal static class HostVersions
{
    /// <summary>The Avalonia version this host was compiled and runs against.</summary>
    public static Version AvaloniaVersion { get; } =
        typeof(Avalonia.AvaloniaObject).Assembly.GetName().Version ?? new Version(0, 0);
}
