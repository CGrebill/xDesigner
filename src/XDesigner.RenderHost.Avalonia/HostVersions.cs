namespace XDesigner.RenderHost;

/// <summary>Version information reported to the designer during the handshake.</summary>
internal static class HostVersions
{
    /// <summary>The Avalonia version this host was compiled and runs against.</summary>
    public static Version AvaloniaVersion { get; } =
        typeof(Avalonia.AvaloniaObject).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>The framework id the designer knows this host by (<c>Avalonia11</c>, <c>Avalonia12</c>).</summary>
    public static string FrameworkId { get; } = $"Avalonia{AvaloniaVersion.Major}";
}
