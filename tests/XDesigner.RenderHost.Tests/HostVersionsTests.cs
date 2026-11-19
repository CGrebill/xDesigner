using XDesigner.RenderHost.DesignTime;

namespace XDesigner.RenderHost.Tests;

public class HostVersionsTests
{
    [Fact]
    public void HostRunsOnTheAvaloniaVersionItWasBuiltFor()
    {
        Assert.Equal(TestPaths.AvaloniaMajor, HostVersions.AvaloniaVersion.Major);
        Assert.Equal($"Avalonia{TestPaths.AvaloniaMajor}", HostVersions.FrameworkId);
    }

    [Fact]
    public void DesignIds_LiveInThisHostsOwnAssembly()
    {
        Assert.Equal(
            $"clr-namespace:XDesigner.RenderHost.DesignTime;assembly=XDesigner.RenderHost.Avalonia{TestPaths.AvaloniaMajor}",
            DesignId.XmlNamespace);
    }
}
