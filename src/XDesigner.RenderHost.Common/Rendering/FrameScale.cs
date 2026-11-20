namespace XDesigner.RenderHost.Rendering;

/// <summary>How many pixels a frame may have, for every host (§14.2).</summary>
public static class FrameScale
{
    /// <summary>The most pixels one frame may have (32 MP, 128 MB of BGRA); a larger frame is rendered at a lower scale.</summary>
    public const double MaxFramePixels = 32_000_000;

    /// <summary>The longest side a frame may have in pixels (GPU textures are limited to 16384).</summary>
    public const double MaxFrameSide = 16_384;

    /// <summary>
    /// The requested scale, lowered when the frame would exceed <see cref="MaxFramePixels"/> or <see cref="MaxFrameSide"/>
    /// (a large window at a high zoom): the result carries the scale used, and the surface stretches the frame to fit.
    /// </summary>
    public static double Effective(double width, double height, double requested)
    {
        var scale = requested is > 0 and < double.PositiveInfinity ? requested : 1.0;
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var area = width * height;
        if (area * scale * scale > MaxFramePixels)
        {
            scale = Math.Sqrt(MaxFramePixels / area);
        }

        return Math.Min(scale, MaxFrameSide / Math.Max(width, height));
    }
}
