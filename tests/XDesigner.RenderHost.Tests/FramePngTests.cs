using System.Runtime.InteropServices;
using XDesigner.RenderHost.Rendering;

namespace XDesigner.RenderHost.Tests;

public class FramePngTests(RenderHostFixture host)
{
    [Fact]
    public async Task Encode_WritesAPngThatDecodesToTheSamePixels()
    {
        // 2×1: opaque red, half-transparent green (premultiplied: G = 128 at A = 128).
        byte[] bgra = [0, 0, 255, 255, 0, 128, 0, 128];

        var png = FramePng.Encode(bgra, 2, 1);

        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
        (int Width, int Height, byte[] Pixels) decoded = await host.Runtime.InvokeAsync(() =>
        {
            using var bitmap = new Avalonia.Media.Imaging.Bitmap(new MemoryStream(png));
            var pixels = new byte[8];
            var buffer = Marshal.AllocHGlobal(pixels.Length);
            try
            {
                bitmap.CopyPixels(new Avalonia.PixelRect(0, 0, 2, 1), buffer, pixels.Length, 8);
                Marshal.Copy(buffer, pixels, 0, pixels.Length);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return (bitmap.PixelSize.Width, bitmap.PixelSize.Height, pixels);
        });

        Assert.Equal(2, decoded.Width);
        Assert.Equal(1, decoded.Height);
        Assert.Equal([0, 0, 255, 255], decoded.Pixels[..4]);
        Assert.Equal(128, decoded.Pixels[7]);
        Assert.InRange(decoded.Pixels[5], 126, 129); // green, premultiplied again on load
    }
}
