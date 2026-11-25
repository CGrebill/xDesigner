using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml.Linq;
using Microsoft.Win32;

namespace XDesigner.Core.Project;

/// <summary>
/// Runs the user's built app (§11.3) and stops it again:
/// <list type="bullet">
/// <item>most projects with <c>dotnet run --no-build</c> in the project folder (WinUI ones with their platform), output streamed
/// to Output ▸ Run;</item>
/// <item>a packaged (MSIX) WinUI or MAUI app by registering its build output as a package and activating it, as Visual
/// Studio does, which needs Developer Mode; without it, a self-contained one's exe is started directly (it then runs
/// without package identity), and one that uses the installed Windows App SDK is refused with how to run it.</item>
/// </list>
/// <see cref="Stop"/> ends the whole process tree.
/// </summary>
public sealed class AppRunner : IDisposable
{
    private const string FoundationNamespace = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

    private readonly Process _process;

    private AppRunner(Process process)
    {
        _process = process;
    }

    private readonly Lock _exitLock = new();
    private Action<int>? _exited;
    private int? _exitCode;

    /// <summary>
    /// Raised once when the app has exited, with its exit code (any thread). A handler added after the app already
    /// exited (an app that crashed while it was being started) is called at once (review L13).
    /// </summary>
    public event Action<int>? Exited
    {
        add
        {
            int? code;
            lock (_exitLock)
            {
                code = _exitCode;
                if (code is null)
                {
                    _exited += value;
                }
            }

            if (code is { } c)
            {
                value?.Invoke(c);
            }
        }

        remove
        {
            lock (_exitLock)
            {
                _exited -= value;
            }
        }
    }

    private void RaiseExited(int code)
    {
        Action<int>? handlers;
        lock (_exitLock)
        {
            if (_exitCode is not null)
            {
                return;
            }

            _exitCode = code;
            handlers = _exited;
            _exited = null;
        }

        handlers?.Invoke(code);
    }

    public bool IsRunning
    {
        get
        {
            try
            {
                return !_process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>Starts <paramref name="project"/>'s built app the way that suits it (see the class summary).</summary>
    public static async Task<AppRunner> StartAsync(ProjectInfo project, Action<string> onLine, CancellationToken cancellationToken = default)
    {
        if (project.IsPackaged && OperatingSystem.IsWindows())
        {
            return await StartPackagedAsync(project, onLine, cancellationToken).ConfigureAwait(false);
        }

        List<string> args = ["run", "--no-build", "--project", project.ProjectPath];
        if (project.Platform is { } platform)
        {
            args.Add($"--property:Platform={platform}");
        }

        if (project.BuildFramework is { } framework)
        {
            args.Add("-f");
            args.Add(framework);
        }

        return Launch(ProcessRunner.CreateStartInfo("dotnet", args, project.Directory), onLine);
    }

    /// <summary>Whether Windows lets unsigned packages be registered from a folder (Settings ▸ System ▸ For developers).</summary>
    public static bool IsDeveloperModeOn()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock");
            return key?.GetValue("AllowDevelopmentWithoutDevLicense") is int value && value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>The package name and application id in an <c>AppxManifest.xml</c>.</summary>
    internal static (string Name, string ApplicationId) ReadIdentity(string manifestPath)
    {
        XNamespace ns = FoundationNamespace;
        var manifest = XDocument.Load(manifestPath);
        var name = (string?)manifest.Root?.Element(ns + "Identity")?.Attribute("Name");
        var appId = (string?)manifest.Root?.Element(ns + "Applications")?.Element(ns + "Application")?.Attribute("Id");
        return name is { Length: > 0 } && appId is { Length: > 0 }
            ? (name, appId)
            : throw new ProjectException($"{manifestPath} has no package name or application id.");
    }

    [SupportedOSPlatform("windows")]
    private static async Task<AppRunner> StartPackagedAsync(ProjectInfo project, Action<string> onLine, CancellationToken cancellationToken)
    {
        var layout = Path.GetDirectoryName(project.TargetPath)!;
        var manifest = Path.Combine(layout, "AppxManifest.xml");
        var exe = Path.ChangeExtension(project.TargetPath, ".exe");
        if (File.Exists(manifest) && !IsDeveloperModeOn() && !File.Exists(Path.Combine(layout, "Microsoft.WindowsAppRuntime.dll")))
        {
            // Not self-contained: its exe finds the Windows App SDK only through package identity, so it would just exit (review L14).
            throw new ProjectException(
                $"{project.Name} is a packaged app that uses the Windows App SDK installed on Windows, which it can only reach when it runs "
                + "as a registered package. Turn on Developer Mode in Settings ▸ System ▸ For developers so the designer can register it, "
                + "or set <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained> in the project.");
        }

        if (!File.Exists(manifest) || !IsDeveloperModeOn())
        {
            onLine(File.Exists(manifest)
                ? "Developer Mode is off, so Windows won't register the app as a package: starting its exe directly instead. It runs "
                  + "without package identity, so features that need one (notifications, app data, the Store) won't work. To run it as "
                  + "a package, turn on Developer Mode in Settings ▸ System ▸ For developers."
                : $"There is no AppxManifest.xml next to {Path.GetFileName(exe)}, so the app is started directly.");
            var direct = ProcessRunner.CreateStartInfo(exe, [], layout);
            return Launch(direct, onLine);
        }

        var (name, applicationId) = ReadIdentity(manifest);
        onLine($"Registering {name} from {layout} (as Visual Studio deploys it)…");
        var register = await ProcessRunner.RunAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                $"$ErrorActionPreference = 'Stop'; Add-AppxPackage -Register '{Quote(manifest)}' -ForceApplicationShutdown; "
                + $"(Get-AppxPackage -Name '{Quote(name)}' | Select-Object -First 1).PackageFamilyName"],
            layout,
            null,
            cancellationToken).ConfigureAwait(false);
        var familyName = register.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (register.ExitCode != 0 || string.IsNullOrEmpty(familyName) || familyName.Contains(' ', StringComparison.Ordinal))
        {
            throw new ProjectException($"Windows couldn't register {name}: {register.Output.Trim()}");
        }

        var appUserModelId = $"{familyName}!{applicationId}";
        var activation = (IApplicationActivationManager)new ApplicationActivationManager();
        activation.ActivateApplication(appUserModelId, null, 0, out var processId);
        Process process;
        try
        {
            process = Process.GetProcessById((int)processId);
        }
        catch (ArgumentException)
        {
            // Gone before it could be watched: it crashed at once (review L13).
            throw new ProjectException($"{project.Name} exited as soon as it started. Run it from Visual Studio or check the Windows event log (Application) for why.");
        }

        onLine($"Started {appUserModelId} (process {processId}). A packaged app's console output isn't shown here.");
        return Attach(process, onLine, captureOutput: false);
    }

    private static string Quote(string text) => text.Replace("'", "''", StringComparison.Ordinal);

    private static AppRunner Launch(ProcessStartInfo start, Action<string> onLine) =>
        Attach(new Process { StartInfo = start }, onLine, captureOutput: true);

    private static AppRunner Attach(Process process, Action<string> onLine, bool captureOutput)
    {
        var runner = new AppRunner(process);
        process.EnableRaisingEvents = true;
        if (captureOutput)
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    onLine(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    onLine(e.Data);
                }
            };
        }

        process.Exited += (_, _) =>
        {
            int code;
            try
            {
                process.WaitForExit(); // let the output readers finish
                code = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                code = 0; // an activated process may not report its exit code to us
            }

            runner.RaiseExited(code);
        };

        if (captureOutput)
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        else if (process.HasExited)
        {
            runner.RaiseExited(0);
        }

        return runner;
    }

    /// <summary>Ends the app and the <c>dotnet</c> process that started it.</summary>
    public void Stop()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    public void Dispose()
    {
        Stop();
        _process.Dispose();
    }

    /// <summary>Starts a packaged app by its app user model id and reports its process (shobjidl.h).</summary>
    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        void ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager;
}
