using System.Buffers.Binary;
using System.IO.Compression;
using XDesigner.Protocol.Rendering;

namespace XDesigner.RenderHost.Rendering;

/// <summary>
/// Writes a rendered frame to a PNG (render-once mode and tests). No UI framework involved, so every render host uses it.
/// </summary>
public static class FramePng
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <returns>Null on success, otherwise the error message.</returns>
    public static string? TrySave(RenderResult result, string path)
    {
        try
        {
            Save(result, path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ex.Message;
        }
    }

    public static void Save(RenderResult result, string path)
    {
        if (result.Bgra is not { } bgra)
        {
            throw new ArgumentException("The render result has no pixels.", nameof(result));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, Encode(bgra, result.PixelWidth, result.PixelHeight));
    }

    /// <summary>A PNG (8-bit RGBA) of premultiplied BGRA pixels, stride = width × 4.</summary>
    public static byte[] Encode(byte[] bgra, int width, int height)
    {
        if (bgra.Length < (long)width * height * 4)
        {
            throw new ArgumentException("The pixel buffer is smaller than the image.", nameof(bgra));
        }

        // Each row: filter type 0, then straight (not premultiplied) RGBA.
        var raw = new byte[(long)height * (width * 4 + 1)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (width * 4 + 1);
            for (var x = 0; x < width; x++)
            {
                var s = (y * width + x) * 4;
                var d = row + 1 + x * 4;
                var a = bgra[s + 3];
                raw[d] = Unpremultiply(bgra[s + 2], a);
                raw[d + 1] = Unpremultiply(bgra[s + 1], a);
                raw[d + 2] = Unpremultiply(bgra[s], a);
                raw[d + 3] = a;
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static byte Unpremultiply(byte value, byte alpha) =>
        alpha is 0 or 255 ? (alpha == 0 ? (byte)0 : value) : (byte)Math.Min(255, value * 255 / alpha);

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = Crc(Crc(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint Crc(uint crc, byte[] data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
