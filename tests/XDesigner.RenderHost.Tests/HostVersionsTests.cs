using XDesigner.Protocol;

namespace XDesigner.RenderHost.Tests;

public class HostVersionsTests
{
    [Fact]
    public void HostRunsOnSupportedAvaloniaMajorVersion()
    {
        Assert.Equal(ProtocolInfo.SupportedAvaloniaMajorVersion, HostVersions.AvaloniaVersion.Major);
    }
}
