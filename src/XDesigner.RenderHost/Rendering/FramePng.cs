using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using XDesigner.Protocol.Rendering;

namespace XDesigner.RenderHost.Rendering;

/// <summary>Writes a rendered frame to a PNG (render-once mode and tests). Must run on the UI thread.</summary>
internal static class FramePng
{
    /// <returns>Null on success, otherwise the error message.</returns>
    public static string? TrySave(RenderResult result, string path)
    {
        try
        {
            Save(result, path);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public static void Save(RenderResult result, string path)
    {
        if (result.Bgra is null)
        {
            throw new ArgumentException("The render result has no pixels.", nameof(result));
        }

        var handle = GCHandle.Alloc(result.Bgra, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(
                PixelFormat.Bgra8888,
                AlphaFormat.Premul,
                handle.AddrOfPinnedObject(),
                new PixelSize(result.PixelWidth, result.PixelHeight),
                new Vector(96 * result.Scale, 96 * result.Scale),
                result.PixelWidth * 4);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            bitmap.Save(path, PngBitmapEncoderOptions.Default);
        }
        finally
        {
            handle.Free();
        }
    }
}
