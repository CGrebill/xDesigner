using System.Globalization;

namespace XDesigner.Core.Settings;

/// <summary>
/// The only place xDesigner writes for itself: <c>%LOCALAPPDATA%\xDesigner</c> (settings and the error log). The
/// uninstaller removes this folder; nothing else on the machine belongs to the app.
/// </summary>
public static class AppDataPaths
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xDesigner");

    public static string Settings => Path.Combine(Folder, "settings.json");

    public static string ErrorLog => Path.Combine(Folder, "errors.log");
}

/// <summary>
/// Appends unexpected errors to <see cref="AppDataPaths.ErrorLog"/>, so a crash can be reported with details. The file
/// starts over once it passes 512 KB. Never throws.
/// </summary>
public static class ErrorLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly Lock Gate = new();

    public static void Append(string context, Exception? exception, string? path = null)
    {
        path ??= AppDataPaths.ErrorLog;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    File.Delete(path);
                }

                var version = typeof(ErrorLog).Assembly.GetName().Version;
                File.AppendAllText(path, string.Create(CultureInfo.InvariantCulture,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] xDesigner {version} - {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere left to report it.
        }
    }
}
