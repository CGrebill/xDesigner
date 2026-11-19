using System.Reflection;

namespace XDesigner.RenderHost.Hosting;

/// <summary>
/// Turns on <see cref="Avalonia.Controls.Design.IsDesignMode"/> so user controls can skip
/// design-time work (network calls, timers, ...).
/// </summary>
/// <remarks>
/// Avalonia 12 exposes only a getter; the setter is internal (Avalonia's own previewer sets it).
/// <see cref="Avalonia.Markup.Xaml.RuntimeXamlLoaderConfiguration.DesignMode"/> does not set it either,
/// so it is set by reflection. See docs/ARCHITECTURE.md §14.1.
/// </remarks>
internal static class DesignModeSwitch
{
    public static bool TryEnable()
    {
        var setter = typeof(Avalonia.Controls.Design)
            .GetProperty(nameof(Avalonia.Controls.Design.IsDesignMode), BindingFlags.Public | BindingFlags.Static)
            ?.GetSetMethod(nonPublic: true);
        if (setter is null)
        {
            return false;
        }

        setter.Invoke(null, [true]);
        return Avalonia.Controls.Design.IsDesignMode;
    }
}
