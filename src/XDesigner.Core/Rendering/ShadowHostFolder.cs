using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace XDesigner.Core.Rendering;

/// <summary>
/// The folder a WinUI render host runs from (§14.5): a copy of the user's build output with the host's files on top, so
/// the host's <c>ms-appx:///</c> URIs, compiled XAML (<c>.xbf</c>) and resources resolve to the user's, as in the user's app.
/// Host files are hard-linked (no copying of ~150 MB; they never change while installed); the user's files are copied, so
/// rebuilding the project never finds its output locked by the designer.
/// </summary>
/// <remarks>
/// A folder stays in use while its <see cref="LockFileName"/> is open (held by this object): <see cref="Prepare"/> removes
/// every other folder under the root, which is how folders left behind by a crash are cleaned up.
/// </remarks>
public sealed partial class ShadowHostFolder : IDisposable
{
    /// <summary>Held open while the folder is in use.</summary>
    public const string LockFileName = ".xdesigner-in-use";

    /// <summary>
    /// User files left out: the compiled App.xaml. WinUI loads <c>ms-appx:///App.xaml</c> into whichever Application runs,
    /// and the host's Application isn't the user's App class (the host reads App.xaml's resources from the project instead).
    /// </summary>
    public static readonly IReadOnlySet<string> SkippedUserFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "App.xbf" };

    private readonly FileStream _lock;

    private ShadowHostFolder(string path, FileStream lockFile)
    {
        Path = path;
        _lock = lockFile;
    }

    /// <summary>The prepared folder; run the host exe from here.</summary>
    public string Path { get; }

    /// <summary>Default root: <c>%LOCALAPPDATA%\xDesigner\shadow</c>.</summary>
    public static string DefaultRoot =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xDesigner", "shadow");

    /// <summary>
    /// Creates a fresh shadow folder under <paramref name="root"/> for one host start: <paramref name="hostDirectory"/>'s
    /// files, plus every file of <paramref name="userOutputDirectory"/> the host doesn't have itself.
    /// </summary>
    public static ShadowHostFolder Prepare(string hostDirectory, string userOutputDirectory, string root)
    {
        Directory.CreateDirectory(root);
        RemoveUnused(root);

        var path = System.IO.Path.Combine(root, $"{ShortHash(userOutputDirectory)}-{Guid.NewGuid():N}"[..26]);
        Directory.CreateDirectory(path);
        var lockFile = new FileStream(System.IO.Path.Combine(path, LockFileName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        try
        {
            foreach (var file in Directory.EnumerateFiles(hostDirectory, "*", SearchOption.AllDirectories))
            {
                LinkOrCopy(file, Target(path, hostDirectory, file));
            }

            foreach (var file in Directory.EnumerateFiles(userOutputDirectory, "*", SearchOption.AllDirectories))
            {
                if (SkippedUserFiles.Contains(System.IO.Path.GetRelativePath(userOutputDirectory, file)))
                {
                    continue;
                }

                var target = Target(path, userOutputDirectory, file);
                if (!File.Exists(target))
                {
                    File.Copy(file, target);
                }
            }

            return new ShadowHostFolder(path, lockFile);
        }
        catch
        {
            lockFile.Dispose();
            TryDelete(path);
            throw;
        }
    }

    /// <summary>The folder is no longer needed: release it and try to delete it (a host still exiting leaves it for later).</summary>
    public void Dispose()
    {
        _lock.Dispose();
        TryDelete(Path);
    }

    /// <summary>Deletes every folder under <paramref name="root"/> that no running designer holds (its lock file isn't open).</summary>
    public static void RemoveUnused(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            var lockFile = System.IO.Path.Combine(folder, LockFileName);
            try
            {
                if (File.Exists(lockFile))
                {
                    File.Delete(lockFile); // fails while a designer holds it
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            TryDelete(folder);
        }
    }

    private static string Target(string shadow, string sourceRoot, string file)
    {
        var target = System.IO.Path.Combine(shadow, System.IO.Path.GetRelativePath(sourceRoot, file));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        return target;
    }

    private static void LinkOrCopy(string source, string target)
    {
        // A hard link needs the same volume; anything else (or a non-Windows test run) copies.
        if (!OperatingSystem.IsWindows() || !CreateHardLink(target, source, 0))
        {
            File.Copy(source, target, overwrite: true);
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use (a host still exiting): the next Prepare removes it.
        }
    }

    /// <summary>Groups a project's folders by name (useful when looking at the root), not for uniqueness.</summary>
    private static string ShortHash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToUpperInvariant())))[..8];

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);
}
