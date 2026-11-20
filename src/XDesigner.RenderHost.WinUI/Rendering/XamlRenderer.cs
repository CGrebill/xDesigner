using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using XDesigner.Protocol.Rendering;
using XDesigner.RenderHost.Hosting;

namespace XDesigner.RenderHost.Rendering;

/// <summary>
/// Turns a render copy of a WinUI XAML document into pixels plus a layout tree (§14.5). Each document has one; its latest
/// render stays on the host's surface for value queries. UI thread only (through <see cref="HostApp.InvokeTaskAsync{T}"/>).
/// </summary>
internal sealed partial class XamlRenderer(HostApp host, RenderCopySanitizer sanitizer)
{
    /// <summary>Preview size used when neither the request nor the document gives one (decision D12).</summary>
    public const double DefaultDesignWidth = 800;

    public const double DefaultDesignHeight = 450;

    /// <summary>How long a render waits for images to decode before drawing without them.</summary>
    private static readonly TimeSpan ImageWait = TimeSpan.FromSeconds(1.5);

    private FrameworkElement? _frame;

    /// <summary>The root element of the last successful render (kept alive for value queries).</summary>
    public FrameworkElement? CurrentRoot { get; private set; }

    public async Task<RenderResult> RenderAsync(RenderRequest request)
    {
        SanitizedXaml copy;
        FrameworkElement root;
        try
        {
            copy = sanitizer.Sanitize(request.Xaml);
            root = XamlReader.Load(copy.Xaml) as FrameworkElement
                ?? throw new NotSupportedException("Only windows, pages and controls can be designed; the document root is not an element.");
        }
        catch (Exception ex)
        {
            return RenderResult.Failed(request.Version, ToRenderError(ex));
        }

        var width = request.DesignSize?.Width ?? FirstUsable(root.Width, copy.DesignWidth, DefaultDesignWidth);
        var height = request.DesignSize?.Height ?? FirstUsable(root.Height, copy.DesignHeight, DefaultDesignHeight);
        var scale = FrameScale.Effective(width, height, request.Scale);

        FrameworkElement frame;
        try
        {
            root.Width = width;
            root.Height = height;
            frame = CreateFrame(root, request.Theme, width * scale / host.RasterizationScale, height * scale / host.RasterizationScale);
            host.Surface.Children.Add(frame);
        }
        catch (Exception ex)
        {
            return RenderResult.Failed(request.Version, ToRenderError(ex));
        }

        try
        {
            frame.UpdateLayout();
            await IdleAsync();          // Loaded handlers and deferred template work
            var images = await WaitForImagesAsync(root);
            foreach (var element in images)
            {
                // Measured before its image had loaded (an image without a set size measures to the picture).
                element.InvalidateMeasure();
            }

            frame.UpdateLayout();
            if (images.Count > 0)
            {
                // WinUI decodes images again at the size they are shown, asynchronously: let that reach the screen.
                await RenderedFramesAsync(2);
                frame.UpdateLayout();
            }

            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(frame);
            if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0)
            {
                throw new InvalidOperationException("The form drew nothing (it may be too large to draw).");
            }

            var pixels = (await bitmap.GetPixelsAsync()).ToArray();
            var layout = LayoutTreeBuilder.Build(root);
            if (copy.IsWindow && layout is not null && layout.DesignId != 0)
            {
                // The stand-in Grid is the Window's client area: report it as the Window (a content control, not a grid).
                layout = layout with { TypeName = typeof(Window).FullName!, Panel = null };
            }
            ReplaceCurrent(frame, root);

            var rootKind = copy.IsWindow ? RootKind.Window : root is UserControl ? RootKind.UserControl : RootKind.Other;
            return new RenderResult(
                request.Version, true, bitmap.PixelWidth, bitmap.PixelHeight, bitmap.PixelWidth / width, pixels, layout, rootKind,
                copy.IsWindow ? copy.WindowTitle : null, null);
        }
        catch (Exception ex)
        {
            Remove(frame);
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

        _frame = null;
        CurrentRoot = null;
    }

    /// <summary>
    /// What gets drawn: the theme's page background (as behind a window's content) with the document scaled to the frame's
    /// pixel size by a Viewbox, so zoomed renders are sharp. The root's own coordinates stay unscaled for the layout tree.
    /// </summary>
    private static Border CreateFrame(FrameworkElement root, ThemeVariantKind theme, double width, double height)
    {
        var frame = (Border)XamlReader.Load(
            $$"""<Border xmlns="{{XamlTypeResolver.PresentationNamespace}}" Background="{ThemeResource ApplicationPageBackgroundThemeBrush}" />""");
        frame.RequestedTheme = theme switch
        {
            ThemeVariantKind.Light => ElementTheme.Light,
            ThemeVariantKind.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        frame.Width = width;
        frame.Height = height;
        frame.Child = new Viewbox { Stretch = Stretch.Fill, Child = root };
        return frame;
    }

    private static double FirstUsable(params double?[] candidates) =>
        candidates.First(v => v is { } d && double.IsFinite(d) && d > 0)!.Value;

    /// <summary>Lets queued UI work (Loaded events, deferred layout) run before drawing.</summary>
    private static Task IdleAsync()
    {
        var done = new TaskCompletionSource();
        if (DispatcherQueue.GetForCurrentThread()?.TryEnqueue(DispatcherQueuePriority.Low, () => done.TrySetResult()) != true)
        {
            done.TrySetResult();
        }

        return done.Task;
    }

    /// <summary>Waits until the compositor has drawn <paramref name="count"/> frames (at most a quarter second).</summary>
    private static async Task RenderedFramesAsync(int count)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = 0;
        void OnRendering(object? sender, object e)
        {
            if (++seen >= count)
            {
                done.TrySetResult();
            }
        }

        CompositionTarget.Rendering += OnRendering;
        try
        {
            await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromMilliseconds(250)));
        }
        finally
        {
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    /// <summary>
    /// Images decode asynchronously: wait (briefly) until each one has loaded or failed, so the frame shows them.
    /// Returns the elements showing images, to measure again.
    /// </summary>
    private static async Task<List<UIElement>> WaitForImagesAsync(DependencyObject root)
    {
        var pending = new List<Task>();
        var showing = new List<UIElement>();
        foreach (var element in LayoutTreeBuilder.SelfAndDescendants(root))
        {
            var source = element switch
            {
                Image image => image.Source,
                Border { Background: ImageBrush brush } => brush.ImageSource,
                Panel { Background: ImageBrush brush } => brush.ImageSource,
                Control { Background: ImageBrush brush } => brush.ImageSource,
                _ => null,
            };
            if (source is not null)
            {
                showing.Add((UIElement)element);
            }

            if (source is BitmapImage { UriSource: not null, PixelWidth: 0 } bitmap)
            {
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                bitmap.ImageOpened += (_, _) => loaded.TrySetResult();
                bitmap.ImageFailed += (_, _) => loaded.TrySetResult();
                pending.Add(loaded.Task);
            }
        }

        if (pending.Count > 0)
        {
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(ImageWait));
        }

        return showing;
    }

    private void ReplaceCurrent(FrameworkElement frame, FrameworkElement root)
    {
        if (_frame is not null && !ReferenceEquals(_frame, frame))
        {
            Remove(_frame);
        }

        _frame = frame;
        CurrentRoot = root;
    }

    private void Remove(FrameworkElement frame)
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

    /// <summary>
    /// Message, position and offending type of a load or render failure. WinUI's parser reports
    /// <c>… [Line: 3 Position: 12]</c> in the message, after a "text associated with this error code" boilerplate line.
    /// </summary>
    internal static RenderError ToRenderError(Exception ex)
    {
        if (ex is TargetInvocationException { InnerException: { } inner })
        {
            ex = inner;
        }

        var message = ex.Message;
        int? line = null, column = null;
        if (PositionPattern().Match(message) is { Success: true } position)
        {
            line = int.Parse(position.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            column = int.Parse(position.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            message = message.Remove(position.Index, position.Length);
        }

        message = string.Join(" ", message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("The text associated with this error code", StringComparison.Ordinal)));
        if (message.Length == 0)
        {
            message = ex.GetType().Name;
        }

        // An exception thrown from a control's own code: name the control's type (innermost exception first).
        var chain = new List<Exception>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            chain.Add(e);
        }

        var offendingType = Enumerable.Reverse(chain)
            .SelectMany(e => new System.Diagnostics.StackTrace(e, false).GetFrames())
            .Select(f => f.GetMethod()?.DeclaringType)
            .FirstOrDefault(t => t is not null && typeof(UIElement).IsAssignableFrom(t) && !IsWinUIType(t))
            ?.FullName;

        return new RenderError(message, ex.GetType().FullName, line, column, offendingType);
    }

    /// <summary>True for the WinUI projection and the controls library, not the user's types.</summary>
    internal static bool IsWinUIType(Type t) => t.Assembly == typeof(UIElement).Assembly
        || t.Namespace?.StartsWith("Microsoft.UI.Xaml", StringComparison.Ordinal) == true;

    [GeneratedRegex(@"\[Line: (\d+) Position: (\d+)\]")]
    private static partial Regex PositionPattern();
}
