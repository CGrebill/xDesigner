namespace XDesigner.RenderHost.Tests;

public class RenderHostOptionsTests
{
    [Fact]
    public void Parse_ReadsAllOptions()
    {
        var options = RenderHostOptions.Parse(["--pipe", "p1", "--assembly", @"C:\app\bin\App.dll", "--parent-pid", "42"]);

        Assert.Equal("p1", options.PipeName);
        Assert.Equal(@"C:\app\bin\App.dll", options.UserAssemblyPath);
        Assert.Equal(42, options.ParentProcessId);
        Assert.False(options.ShowVersion);
    }

    [Fact]
    public void Parse_Version()
    {
        Assert.True(RenderHostOptions.Parse(["--version"]).ShowVersion);
    }

    [Theory]
    [InlineData("--pipe")]
    [InlineData("--parent-pid", "abc")]
    [InlineData("--parent-pid", "-3")]
    [InlineData("--bogus")]
    [InlineData("--pipe", "--version")]
    public void Parse_RejectsInvalidArguments(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => RenderHostOptions.Parse(args));
    }
}
