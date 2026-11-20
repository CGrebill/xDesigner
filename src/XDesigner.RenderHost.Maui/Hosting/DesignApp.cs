namespace XDesigner.RenderHost.Hosting;

/// <summary>
/// The MAUI app used when there is no project (or its own app failed to start, <c>--no-user-app</c>): MAUI's default look,
/// and an empty window whose context pages are drawn with.
/// </summary>
internal sealed class DesignApp : Application
{
    protected override Window CreateWindow(IActivationState? activationState) => new(new ContentPage());
}
