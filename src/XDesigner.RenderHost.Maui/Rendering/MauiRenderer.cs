using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Maui.Controls.Xaml;
using Microsoft.Maui.Platform;
using XDesigner.Protocol.Rendering;
using XDesigner.RenderHost.Hosting;
using Xaml = Microsoft.UI.Xaml;

namespace XDesigner.RenderHost.Rendering;

/// <summary>
/// Turns a render copy of a MAUI XAML document into pixels plus a layout tree (§14.6): the root is created from its type
/// and loaded with MAUI's runtime XAML loader, turned into WinUI elements by MAUI's handlers (<c>ToPlatform</c>) and drawn
/// like a WinUI document. Each document has one; its latest render stays alive for value queries. UI thread only.
/// </summary>
internal sealed partial class MauiRenderer(HostApp host, MauiSanitizer sanitizer)
{
    /// <summary>Preview size used when neither the request nor the document gives one (decision D12).</summary>
    public const double DefaultDesignWidth = 800;

    public const double DefaultDesignHeight = 450;

    private Xaml.FrameworkElement? _frame;

    /// <summary>The root of the last successful render (kept alive for value queries).</summary>
    public VisualElement? CurrentRoot { get; private set; }

    /// <summary>The properties the last render's XAML writes on each element, by design id (to tell Local values from styles').</summary>
    public IReadOnlyDictionary<int, HashSet<string>> WrittenProperties { get; private set; } = new Dictionary<int, HashSet<string>>();

    public async Task<RenderResult> RenderAsync(RenderRequest request)
    {
        SanitizedMauiXaml copy;
        VisualElement root;
        Window? window = null;
        try
        {
            copy = sanitizer.Sanitize(request.Xaml);
            var isWindow = copy.RootType is { } t && typeof(Window).IsAssignableFrom(t);
            if (copy.RootType is not { } rootType || !(isWindow || typeof(VisualElement).IsAssignableFrom(rootType)))
            {
                throw new NotSupportedException(copy.RootType is null
                    ? "The document's root element isn't a type the renderer knows."
                    : $"Only windows, pages and views can be designed; the document root is a {copy.RootType.Name}.");
            }

            if (isWindow)
            {
                // A Window isn't drawn itself: its Page is, inside the Window the layout reports (as the WinUI renderer does).
                window = (Window)Activator.CreateInstance(rootType)!;
                window.LoadFromXaml(copy.Xaml);
                root = window.Page ?? throw new NotSupportedException("The window has no page to show.");
            }
            else
            {
                root = (VisualElement)Activator.CreateInstance(rootType)!;
                root.LoadFromXaml(copy.Xaml);
            }
        }
        catch (Exception ex)
        {
            return RenderResult.Failed(request.Version, ToRenderError(ex));
        }

        // A design size of 0 or NaN falls back like a missing one (review L35).
        var width = FirstUsable(request.DesignSize?.Width, root.WidthRequest, window?.Width, copy.DesignWidth, DefaultDesignWidth);
        var height = FirstUsable(request.DesignSize?.Height, root.HeightRequest, window?.Height, copy.DesignHeight, DefaultDesignHeight);
        var scale = FrameScale.Effective(width, height, request.Scale);

        Xaml.FrameworkElement frame;
        try
        {
            if (Application.Current is { } app)
            {
                app.UserAppTheme = request.Theme switch
                {
                    ThemeVariantKind.Light => AppTheme.Light,
                    ThemeVariantKind.Dark => AppTheme.Dark,
                    _ => AppTheme.Unspecified,
                };
            }

            var native = (Xaml.FrameworkElement)root.ToPlatform(host.MauiContext);
            native.Width = width;
            native.Height = height;
            frame = FrameCapture.CreateFrame(native, request.Theme, width * scale / host.RasterizationScale, height * scale / host.RasterizationScale);
            host.Surface.Children.Add(frame);
        }
        catch (Exception ex)
        {
            Disconnect(root);
            return RenderResult.Failed(request.Version, ToRenderError(ex));
        }

        try
        {
            frame.UpdateLayout();
            await WaitForImageSourcesAsync(root);
            var (pixelWidth, pixelHeight, pixels) = await FrameCapture.CaptureAsync(frame);
            var layout = MauiLayoutTree.Build(root);
            if (window is not null && DesignTime.DesignId.GetValue(window) is var windowId and not 0 && layout is not null)
            {
                layout = new ElementLayout(windowId, typeof(Window).FullName!, new RectDto(0, 0, width, height), true,
                    ThicknessDto.Zero, ThicknessDto.Zero, null, [layout]);
            }

            ReplaceCurrent(frame, root);
            WrittenProperties = Written(request.Xaml);
            var rootKind = window is not null ? RootKind.Window : root is Page or ContentView ? RootKind.UserControl : RootKind.Other;
            return new RenderResult(request.Version, true, pixelWidth, pixelHeight, pixelWidth / width, pixels, layout, rootKind, window?.Title, null);
        }
        catch (Exception ex)
        {
            Remove(frame);
            Disconnect(root);
            return RenderResult.Failed(request.Version, ToRenderError(ex));
        }
    }

    /// <summary>Forgets the live render (document closed in the app).</summary>
    public void Close()
    {
        if (_frame is not null)
        {
            Remove(_frame);
        }

        if (CurrentRoot is not null)
        {
            Disconnect(CurrentRoot);
        }

        _frame = null;
        CurrentRoot = null;
    }

    /// <summary>
    /// MAUI loads image sources itself (asynchronously, through its image service) before WinUI's Image gets one: wait, at
    /// most <see cref="ImageSourceWait"/>, until every image with a source has it on screen, so the capture can wait for
    /// the pictures to decode.
    /// </summary>
    private static async Task WaitForImageSourcesAsync(VisualElement root)
    {
        // Sources that didn't arrive before aren't waited for again: an offline URL or a missing file would add the whole wait
        // to every render (review L48).
        var images = Descendants(root).OfType<IImageSourcePart>()
            .Where(i => i.Source is not null && !Unavailable.Contains(KeyOf(i.Source)) && i is VisualElement { Handler.PlatformView: Xaml.FrameworkElement })
            .Select(i => (Key: KeyOf(i.Source!), View: ((VisualElement)i).Handler!.PlatformView!))
            .ToList();
        var started = Environment.TickCount64;
        while (images.Any(i => HasNoSource(i.View)) && Environment.TickCount64 - started < ImageSourceWait.TotalMilliseconds)
        {
            await Task.Delay(20);
        }

        foreach (var (key, _) in images.Where(i => HasNoSource(i.View) && i.Key.Length > 0))
        {
            Unavailable.Add(key);
        }

        static bool HasNoSource(object platformView) => platformView switch
        {
            Microsoft.UI.Xaml.Controls.Image image => image.Source is null,
            Microsoft.UI.Xaml.Controls.Button { Content: Microsoft.UI.Xaml.Controls.Image image } => image.Source is null,
            _ => false,
        };
    }

    /// <summary>
    /// Attribute and property-element names per design id in the render copy: what the file sets. MAUI's IsSet is also
    /// true for values a style set, which aren't written there (review L42).
    /// </summary>
    private static Dictionary<int, HashSet<string>> Written(string xaml)
    {
        var result = new Dictionary<int, HashSet<string>>();
        try
        {
            var idName = System.Xml.Linq.XName.Get(XDesigner.Protocol.ProtocolInfo.DesignIdAttributeName, XDesigner.Protocol.ProtocolInfo.DesignIdXmlNamespace);
            foreach (var element in MauiSanitizer.ParseWithImplicitNamespaces(xaml).Descendants())
            {
                if (int.TryParse((string?)element.Attribute(idName), System.Globalization.CultureInfo.InvariantCulture, out var id))
                {
                    var names = element.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => a.Name.LocalName)
                        .Concat(element.Elements().Select(e => e.Name.LocalName).Where(n => n.StartsWith(element.Name.LocalName + ".", StringComparison.Ordinal))
                            .Select(n => n[(element.Name.LocalName.Length + 1)..]));
                    result[id] = names.ToHashSet(StringComparer.Ordinal);
                }
            }
        }
        catch (System.Xml.XmlException)
        {
            // Rendered anyway: every set value counts as written.
        }

        return result;
    }

    private static readonly TimeSpan ImageSourceWait = TimeSpan.FromSeconds(1.5);

    /// <summary>Image sources that never arrived (for this host process, so until a build).</summary>
    private static readonly HashSet<string> Unavailable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What identifies a source across renders: its URL or file; nothing for a stream.</summary>
    private static string KeyOf(IImageSource source) => source switch
    {
        UriImageSource uri => uri.Uri?.ToString() ?? "",
        FileImageSource file => file.File ?? "",
        _ => "",
    };

    private static IEnumerable<IVisualTreeElement> Descendants(IVisualTreeElement element) =>
        element.GetVisualChildren().SelectMany(Descendants).Prepend(element);

    private static double FirstUsable(params double?[] candidates) =>
        candidates.First(v => v is { } d && double.IsFinite(d) && d > 0)!.Value;

    private void ReplaceCurrent(Xaml.FrameworkElement frame, VisualElement root)
    {
        if (_frame is not null && !ReferenceEquals(_frame, frame))
        {
            Remove(_frame);
        }

        if (CurrentRoot is not null && !ReferenceEquals(CurrentRoot, root))
        {
            Disconnect(CurrentRoot);
        }

        _frame = frame;
        CurrentRoot = root;
    }

    private void Remove(Xaml.FrameworkElement frame)
    {
        try
        {
            host.Surface.Children.Remove(frame);
        }
        catch
        {
            // A misbehaving user control must not break the next render.
        }
    }

    /// <summary>Lets the handlers of a page that is no longer shown go (they hold WinUI elements and event subscriptions).</summary>
    private static void Disconnect(VisualElement root)
    {
        try
        {
            ((IView)root).DisconnectHandlers(); // every handler in the tree: child views hold timers, media, platform events (review L43)
        }
        catch
        {
            // Nothing more to release.
        }
    }

    /// <summary>
    /// Message, position and offending type of a load or render failure. MAUI's loader reports <c>Position 7:114. …</c>
    /// (also as <c>XmlInfo</c>).
    /// </summary>
    internal static RenderError ToRenderError(Exception ex)
    {
        if (ex is TargetInvocationException { InnerException: { } inner })
        {
            ex = inner;
        }

        var message = ex.Message;
        int? line = null, column = null;
        if (ex is XamlParseException { XmlInfo: { } info } && info.HasLineInfo())
        {
            (line, column) = (info.LineNumber, info.LinePosition);
        }

        if (PositionPattern().Match(message) is { Success: true } position)
        {
            line ??= int.Parse(position.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            column ??= int.Parse(position.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            message = message[position.Length..].TrimStart();
        }

        var chain = new List<Exception>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            chain.Add(e);
        }

        var offendingType = Enumerable.Reverse(chain)
            .SelectMany(e => new System.Diagnostics.StackTrace(e, false).GetFrames())
            .Select(f => f.GetMethod()?.DeclaringType)
            .FirstOrDefault(t => t is not null && typeof(Element).IsAssignableFrom(t) && !IsMauiType(t))
            ?.FullName;

        return new RenderError(message, ex.GetType().FullName, line, column, offendingType);
    }

    /// <summary>True for MAUI's own assemblies (<c>Microsoft.Maui*</c>), not the user's types.</summary>
    internal static bool IsMauiType(Type t) => t.Assembly.GetName().Name?.StartsWith("Microsoft.Maui", StringComparison.Ordinal) == true;

    [GeneratedRegex(@"^Position (\d+):(\d+)\.")]
    private static partial Regex PositionPattern();
}
