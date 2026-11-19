using Avalonia;
using Avalonia.Themes.Fluent;

namespace XDesigner.RenderHost.Hosting;

/// <summary>
/// Fallback application used when there is no user project, or the user's <c>App</c> can't be used.
/// Gives controls the same Fluent look a new Avalonia project has.
/// </summary>
internal sealed class DesignApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }
}
