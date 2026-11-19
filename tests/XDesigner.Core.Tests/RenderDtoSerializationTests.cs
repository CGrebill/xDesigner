using XDesigner.Protocol.Rendering;
using MessagePack;

namespace XDesigner.Core.Tests;

/// <summary>The render DTOs must survive the MessagePack transport used over the named pipe (§4).</summary>
public class RenderDtoSerializationTests
{
    [Fact]
    public void RenderResult_RoundTrips()
    {
        var child = new ElementLayout(2, "Avalonia.Controls.Button", new RectDto(10, 20, 80, 32), true,
            new ThicknessDto(10, 20, 0, 0), ThicknessDto.Zero, null, []);
        var root = new ElementLayout(1, "Avalonia.Controls.Grid", new RectDto(0, 0, 400, 300), true,
            ThicknessDto.Zero, ThicknessDto.Zero,
            new PanelInfo(PanelKind.Grid, RowOffsets: [0, 100, 300], ColumnOffsets: [0, 400]),
            [child]);
        var result = new RenderResult(7, true, 2, 1, 1.5, [1, 2, 3, 4, 5, 6, 7, 8], root, RootKind.UserControl, null, null);

        var bytes = MessagePackSerializer.Serialize(result, cancellationToken: TestContext.Current.CancellationToken);
        var copy = MessagePackSerializer.Deserialize<RenderResult>(bytes, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(result.Version, copy.Version);
        Assert.Equal(result.Bgra, copy.Bgra);
        Assert.Equal(RootKind.UserControl, copy.RootKind);
        Assert.Equal(new[] { 0d, 100, 300 }, copy.Root!.Panel!.RowOffsets);
        Assert.Equal(child with { Children = [] }, copy.Root.Children.Single() with { Children = [] });
    }

    [Fact]
    public void RenderRequest_RoundTripsWithDefaults()
    {
        var request = new RenderRequest(3, "<Grid />");

        var copy = MessagePackSerializer.Deserialize<RenderRequest>(MessagePackSerializer.Serialize(request, cancellationToken: TestContext.Current.CancellationToken), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(request, copy);
    }
}
