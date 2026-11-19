using System.Diagnostics;
using System.Text;

namespace XDesigner.Core.Project;

/// <summary>Runs a command-line tool (dotnet) and streams its output line by line.</summary>
public static class ProcessRunner
{
    public sealed record Result(int ExitCode, string Output);

    /// <summary>
    /// Runs <paramref name="fileName"/> in <paramref name="workingDirectory"/> (so that directory's <c>global.json</c> picks the SDK,
    /// §11.2), with English, non-interactive output. Each stdout/stderr line goes to <paramref name="onLine"/> (any thread).
    /// Cancelling kills the whole process tree.
    /// </summary>
    public static async Task<Result> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory,
        Action<string>? onLine = null, CancellationToken cancellationToken = default)
    {
        using var process = new Process { StartInfo = CreateStartInfo(fileName, arguments, workingDirectory), EnableRaisingEvents = true };
        var output = new StringBuilder();
        void Receive(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (output)
            {
                output.AppendLine(line);
            }

            onLine?.Invoke(line);
        }

        process.OutputDataReceived += (_, e) => Receive(e.Data);
        process.ErrorDataReceived += (_, e) => Receive(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            throw;
        }

        process.WaitForExit(); // flush the async output readers
        lock (output)
        {
            return new Result(process.ExitCode, output.ToString());
        }
    }

    /// <summary>
    /// How every <c>dotnet</c> command starts: hidden, output redirected as UTF-8, English messages (parseable
    /// <c>error CS1002:</c> lines), no logo, no telemetry, plain MSBuild logger.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["MSBUILDTERMINALLOGGER"] = "off";
        return start;
    }
}
