namespace XDesigner.RenderHost.Tests;

public class AssemblyLockTests(RenderHostFixture host)
{
    /// <summary>
    /// The project's assembly is loaded from memory, so a build can overwrite it while the designer shows it.
    /// Opening it for exclusive write access fails if anything holds the file.
    /// </summary>
    [Fact]
    public void LoadedUserAssembly_IsNotLocked()
    {
        Assert.NotNull(host.Runtime.UserAssembly);
        Assert.Equal("", host.Runtime.UserAssembly!.Location); // loaded from bytes

        using var exclusive = new FileStream(TestPaths.SampleAssembly, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }
}
