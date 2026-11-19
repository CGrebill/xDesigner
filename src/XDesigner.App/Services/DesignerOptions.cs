using XDesigner.Core.Settings;

namespace XDesigner.App.Services;

/// <summary>The settings the design surface reads while dragging (§7.3), copied from <see cref="DesignerSettings"/>.</summary>
public static class DesignerOptions
{
    public static bool SnapToControls { get; private set; } = true;

    public static bool SnapToGrid { get; private set; }

    public static double GridSize { get; private set; } = 8;

    public static void Apply(DesignerSettings settings)
    {
        SnapToControls = settings.SnapToControls;
        SnapToGrid = settings.SnapToGrid;
        GridSize = settings.GridSize is > 0 and <= 200 ? settings.GridSize : 8;
    }
}
