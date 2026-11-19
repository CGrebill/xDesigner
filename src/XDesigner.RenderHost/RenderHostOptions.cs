using System.Globalization;
using XDesigner.Protocol.Rendering;

namespace XDesigner.RenderHost;

/// <summary>
/// Command-line options for the render host. See docs/ARCHITECTURE.md §14.1.
/// <code>
/// Server mode:  --pipe {name} [--assembly {userTargetPath}] [--parent-pid {pid}] [--no-user-app]
/// Render once:  --render {file.axaml} --out {file.png} [--assembly {path}] [--scale 1.5] [--theme Light|Dark] [--size 800x600] [--no-user-app]
/// Version:      --version
/// </code>
/// </summary>
internal sealed record RenderHostOptions
{
    public string? PipeName { get; init; }

    public string? UserAssemblyPath { get; init; }

    public int? ParentProcessId { get; init; }

    public bool ShowVersion { get; init; }

    /// <summary>Skip the user's App/BuildAvaloniaApp and use the default Fluent theme.</summary>
    public bool NoUserApp { get; init; }

    /// <summary>Render-once mode: XAML file to render (already a render copy, see §5.4).</summary>
    public string? RenderFile { get; init; }

    /// <summary>Render-once mode: PNG to write; the layout tree is written next to it as <c>.layout.json</c>.</summary>
    public string? OutputPath { get; init; }

    public double Scale { get; init; } = 1.0;

    public ThemeVariantKind Theme { get; init; } = ThemeVariantKind.Default;

    public SizeDto? Size { get; init; }

    public static RenderHostOptions Parse(IReadOnlyList<string> args)
    {
        var options = new RenderHostOptions();
        for (var i = 0; i < args.Count; i++)
        {
            options = args[i] switch
            {
                "--pipe" => options with { PipeName = RequireValue(args, ref i) },
                "--assembly" => options with { UserAssemblyPath = RequireValue(args, ref i) },
                "--parent-pid" => options with { ParentProcessId = ParsePositiveInt(args[i], RequireValue(args, ref i)) },
                "--version" => options with { ShowVersion = true },
                "--no-user-app" => options with { NoUserApp = true },
                "--render" => options with { RenderFile = RequireValue(args, ref i) },
                "--out" => options with { OutputPath = RequireValue(args, ref i) },
                "--scale" => options with { Scale = ParseScale(RequireValue(args, ref i)) },
                "--theme" => options with { Theme = ParseTheme(RequireValue(args, ref i)) },
                "--size" => options with { Size = ParseSize(RequireValue(args, ref i)) },
                _ => throw new ArgumentException($"Unknown argument '{args[i]}'."),
            };
        }

        if (options.RenderFile is not null && options.OutputPath is null)
        {
            throw new ArgumentException("--render requires --out.");
        }

        return options;
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int i)
    {
        if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{args[i]} expects a value.");
        }

        return args[++i];
    }

    private static int ParsePositiveInt(string option, string raw) =>
        int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new ArgumentException($"{option} expects a positive integer, got '{raw}'.");

    private static double ParseScale(string raw) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is > 0 and <= 8
            ? value
            : throw new ArgumentException($"--scale expects a number between 0 and 8, got '{raw}'.");

    private static ThemeVariantKind ParseTheme(string raw) =>
        Enum.TryParse<ThemeVariantKind>(raw, ignoreCase: true, out var theme) && Enum.IsDefined(theme)
            ? theme
            : throw new ArgumentException($"--theme expects Default, Light or Dark, got '{raw}'.");

    private static SizeDto ParseSize(string raw)
    {
        var parts = raw.Split('x', 'X');
        return parts.Length == 2
               && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && w > 0
               && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && h > 0
            ? new SizeDto(w, h)
            : throw new ArgumentException($"--size expects WIDTHxHEIGHT, got '{raw}'.");
    }
}
