using XDesigner.Protocol;

namespace XDesigner.Core.Tests;

public class ProtocolInfoTests
{
    [Fact]
    public void CreatePipeName_IsUniqueAndPrefixed()
    {
        var first = ProtocolInfo.CreatePipeName(1234);
        var second = ProtocolInfo.CreatePipeName(1234);

        Assert.StartsWith($"{ProtocolInfo.PipeNamePrefix}1234-", first);
        Assert.NotEqual(first, second);
    }
}
