using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using XDesigner.Protocol.Rendering;
using XDesigner.RenderHost.Server;

namespace XDesigner.RenderHost.Rendering;

/// <summary>Render-once mode (<c>--render … --out …</c>): writes the PNG and the layout tree, and reports, for every host alike.</summary>
public static class RenderOnceOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Writes <paramref name="result"/> to <paramref name="pngPath"/> (and <c>.layout.json</c> next to it); returns the exit code.</summary>
    public static int Write(RenderResult result, string pngPath)
    {
        if (!result.Success)
        {
            var e = result.Error!;
            Console.Error.WriteLine($"Render failed: {e.Message} ({e.ExceptionType}) line {e.Line?.ToString(CultureInfo.InvariantCulture) ?? "?"}, column {e.Column?.ToString(CultureInfo.InvariantCulture) ?? "?"}{(e.OffendingType is null ? "" : $", in {e.OffendingType}")}");
            return RenderHostExitCodes.RenderFailed;
        }

        if (FramePng.TrySave(result, pngPath) is { } saveError)
        {
            Console.Error.WriteLine($"Could not save PNG: {saveError}");
            return RenderHostExitCodes.RenderFailed;
        }

        var layoutPath = Path.ChangeExtension(pngPath, ".layout.json");
        File.WriteAllText(layoutPath, JsonSerializer.Serialize(result.Root, JsonOptions));
        Console.WriteLine($"Rendered {result.PixelWidth}x{result.PixelHeight} px ({result.RootKind}) to {pngPath}; layout in {layoutPath}");
        return RenderHostExitCodes.Ok;
    }
}
