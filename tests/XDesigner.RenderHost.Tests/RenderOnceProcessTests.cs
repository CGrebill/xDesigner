using System.Diagnostics;

namespace XDesigner.RenderHost.Tests;

/// <summary>Runs the real host executable, as the designer app will (fresh process per scenario).</summary>
public class RenderOnceProcessTests(ITestOutputHelper output)
{
    [Fact]
    public async Task NoProject_RendersWithDefaultFluentTheme()
    {
        using var dir = new TempDir();
        var xamlPath = Path.Combine(dir.Path, "control.axaml");
        await File.WriteAllTextAsync(xamlPath,
            $"""<UserControl {RenderXaml.Namespaces} d:DesignWidth="200" d:DesignHeight="100" dsn:DesignId.Value="1"><Button Content="Hi" dsn:DesignId.Value="2" /></UserControl>""",
            TestContext.Current.CancellationToken);
        var pngPath = Path.Combine(dir.Path, "control.png");

        var (exitCode, stdout) = await RunHostAsync("--render", xamlPath, "--out", pngPath, "--scale", "2");

        Assert.Equal(0, exitCode);
        Assert.Contains("DesignApp", stdout);
        Assert.True(File.Exists(pngPath));
        Assert.True(File.Exists(Path.ChangeExtension(pngPath, ".layout.json")));
        Assert.Contains("400x200 px", stdout);
    }

    [Fact]
    public async Task NoUserApp_SkipsTheProjectsApp()
    {
        using var dir = new TempDir();
        var xamlPath = Path.Combine(dir.Path, "main.axaml");
        await File.WriteAllTextAsync(xamlPath, RenderXaml.SampleMainWindow, TestContext.Current.CancellationToken);

        var (exitCode, stdout) = await RunHostAsync(
            "--render", xamlPath, "--out", Path.Combine(dir.Path, "main.png"), "--assembly", TestPaths.SampleAssembly, "--no-user-app");

        Assert.Equal(0, exitCode);
        Assert.Contains("DesignApp", stdout); // custom controls still resolve without the user's App
    }

    [Fact]
    public async Task RenderFailure_ExitsWithRenderFailedCode()
    {
        using var dir = new TempDir();
        var xamlPath = Path.Combine(dir.Path, "bad.axaml");
        await File.WriteAllTextAsync(xamlPath, "<UserControl xmlns=\"https://github.com/avaloniaui\"><Nope /></UserControl>", TestContext.Current.CancellationToken);

        var (exitCode, _) = await RunHostAsync("--render", xamlPath, "--out", Path.Combine(dir.Path, "bad.png"));

        Assert.Equal(4, exitCode);
    }

    [Fact]
    public async Task BadArguments_ExitWithCode1()
    {
        var (exitCode, _) = await RunHostAsync("--scale", "-1");

        Assert.Equal(1, exitCode);
    }

    private async Task<(int ExitCode, string StdOut)> RunHostAsync(params string[] args)
    {
        var psi = new ProcessStartInfo(TestPaths.RenderHostExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);

        output.WriteLine(await stdout);
        output.WriteLine(await stderr);
        return (process.ExitCode, await stdout);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("xdesigner-tests-").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }
}
