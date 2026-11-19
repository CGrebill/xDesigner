using System.IO.Pipes;
using XDesigner.Core.Rendering;

namespace XDesigner.Core.Tests;

public class RenderHostConnectionCleanupTests
{
    [Fact]
    public async Task AnAbandonedConnect_Ends_InsteadOfPollingForever()
    {
        // A pipe nobody will ever create, as when the render host exits during startup.
        using var pipe = new NamedPipeClientStream(".", $"xDesigner-test-{Guid.NewGuid():N}", PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource();
        var connect = pipe.ConnectAsync(cts.Token);
        Assert.False(connect.IsCompleted);

        await RenderHostConnection.CancelConnectAsync(cts, connect).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(connect.IsCompleted);
    }
}
