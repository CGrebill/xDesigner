using XDesigner.Core.Rendering;

namespace XDesigner.Core.Tests;

public sealed class ShadowHostFolderTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "xd-shadow-" + Guid.NewGuid().ToString("N")[..8]);

    public ShadowHostFolderTests()
    {
        Write("host/Host.exe", "host exe");
        Write("host/WinRT.Runtime.dll", "host runtime");
        Write("host/en-us/Host.resources.dll", "host resources");
        Write("user/App.dll", "user app");
        Write("user/WinRT.Runtime.dll", "user runtime");
        Write("user/Assets/logo.png", "png");
        Write("user/MainWindow.xbf", "xbf");
        Write("user/App.xbf", "app xbf");
    }

    private string Root => Path.Combine(_temp, "shadow");

    [Fact]
    public void Prepare_LayersTheHostOverTheUsersOutput()
    {
        using var shadow = ShadowHostFolder.Prepare(Path.Combine(_temp, "host"), Path.Combine(_temp, "user"), Root);

        Assert.Equal("host exe", Read(shadow, "Host.exe"));
        Assert.Equal("host runtime", Read(shadow, "WinRT.Runtime.dll")); // the host's own wins
        Assert.Equal("host resources", Read(shadow, "en-us/Host.resources.dll"));
        Assert.Equal("user app", Read(shadow, "App.dll"));
        Assert.Equal("png", Read(shadow, "Assets/logo.png"));
        Assert.Equal("xbf", Read(shadow, "MainWindow.xbf"));
        Assert.False(File.Exists(Path.Combine(shadow.Path, "App.xbf"))); // WinUI would load it into the host's Application
    }

    [Fact]
    public void UsersFiles_AreCopiedSoRebuildingNeverFindsThemLocked()
    {
        using var shadow = ShadowHostFolder.Prepare(Path.Combine(_temp, "host"), Path.Combine(_temp, "user"), Root);
        using var held = File.Open(Path.Combine(shadow.Path, "App.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);

        File.WriteAllText(Path.Combine(_temp, "user", "App.dll"), "rebuilt");

        Assert.Equal("user app", Read(shadow, "App.dll"));
    }

    [Fact]
    public void Dispose_RemovesTheFolder()
    {
        var shadow = ShadowHostFolder.Prepare(Path.Combine(_temp, "host"), Path.Combine(_temp, "user"), Root);

        shadow.Dispose();

        Assert.False(Directory.Exists(shadow.Path));
    }

    [Fact]
    public void Prepare_RemovesFoldersNoDesignerHolds_AndKeepsTheOthers()
    {
        using var inUse = ShadowHostFolder.Prepare(Path.Combine(_temp, "host"), Path.Combine(_temp, "user"), Root);
        var leftOver = Path.Combine(Root, "leftover-from-a-crash");
        Directory.CreateDirectory(leftOver);
        File.WriteAllText(Path.Combine(leftOver, "file.txt"), "x");
        Directory.SetCreationTimeUtc(leftOver, DateTime.UtcNow.AddMinutes(-5));
        var beingCreated = Directory.CreateDirectory(Path.Combine(Root, "another-designer-starting")).FullName; // no lock file yet

        using var next = ShadowHostFolder.Prepare(Path.Combine(_temp, "host"), Path.Combine(_temp, "user"), Root);

        Assert.False(Directory.Exists(leftOver));
        Assert.True(Directory.Exists(beingCreated)); // review L26
        Assert.True(File.Exists(Path.Combine(inUse.Path, "Host.exe")));
        Assert.NotEqual(inUse.Path, next.Path);
    }

    /// <summary>A packaged build's layout copy isn't copied again (review L34); its resources.pri is the MAUI host's index (review M30).</summary>
    [Fact]
    public void PackagedOutput_SkipsTheLayoutCopy_AndUsesResourcesPri()
    {
        Write("user/AppX/App.dll", "layout copy");
        Write("user/resources.pri", "project index");
        Write("host/Host.pri", "host index");

        using var shadow = ShadowHostFolder.Prepare(Path.Combine(_temp, "host"), Path.Combine(_temp, "user"), Root, ("App", "Host"));

        Assert.False(Directory.Exists(Path.Combine(shadow.Path, "AppX")));
        Assert.Equal("project index", Read(shadow, "Host.pri"));
        Assert.Equal("host index", File.ReadAllText(Path.Combine(_temp, "host", "Host.pri"))); // the installed file is untouched
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_temp, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string Read(ShadowHostFolder shadow, string relative) => File.ReadAllText(Path.Combine(shadow.Path, relative));
}
