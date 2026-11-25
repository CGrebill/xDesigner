using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using XDesigner.Protocol.Rendering;
using Border = Microsoft.UI.Xaml.Controls.Border; // also compiled into the MAUI hosts, which import Microsoft.Maui.Controls globally
using Image = Microsoft.UI.Xaml.Controls.Image;
using Stretch = Microsoft.UI.Xaml.Media.Stretch;

namespace XDesigner.RenderHost.Rendering;

/// <summary>
/// Draws a WinUI element tree to pixels (§14.5): in a frame with the theme's page background, scaled by a Viewbox so zoomed
/// renders are sharp, after layout, deferred work and image decoding have settled. Shared by the WinUI and MAUI hosts.
/// </summary>
internal static class FrameCapture
{
    private const string PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>How long a render waits for images to decode before drawing without them.</summary>
    private static readonly TimeSpan ImageWait = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// The frame to place on the surface: <paramref name="content"/> (sized in DIPs) in a Viewbox of the frame's pixel size,
    /// so its own coordinates stay unscaled for the layout tree.
    /// </summary>
    public static Border CreateFrame(FrameworkElement content, ThemeVariantKind theme, double width, double height)
    {
        var frame = (Border)XamlReader.Load(
            $$"""<Border xmlns="{{PresentationNamespace}}" Background="{ThemeResource ApplicationPageBackgroundThemeBrush}" />""");
        frame.RequestedTheme = theme switch
        {
            ThemeVariantKind.Light => ElementTheme.Light,
            ThemeVariantKind.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        frame.Width = width;
        frame.Height = height;
        frame.Child = new Viewbox { Stretch = Stretch.Fill, Child = content };
        return frame;
    }

    /// <summary>Lays out <paramref name="frame"/> (already on the surface), lets it settle, and captures it as premultiplied BGRA.</summary>
    public static async Task<(int Width, int Height, byte[] Pixels)> CaptureAsync(FrameworkElement frame)
    {
        frame.UpdateLayout();
        await IdleAsync(); // Loaded handlers and deferred template work
        var images = await WaitForImagesAsync(frame);
        foreach (var element in images)
        {
            // Measured before its image had loaded (an image without a set size measures to the picture).
            element.InvalidateMeasure();
        }

        frame.UpdateLayout();
        if (images.Count == 0)
        {
            return await DrawAsync(frame);
        }

        // WinUI decodes images again at the size they are shown, asynchronously, and on a slow (software) renderer that
        // can take longer than a frame or two: draw until two pictures in a row agree, or until the wait is up.
        var deadline = DateTime.UtcNow + ImageWait;
        await RenderedFramesAsync(2);
        frame.UpdateLayout();
        var drawn = await DrawAsync(frame);
        while (DateTime.UtcNow < deadline)
        {
            await RenderedFramesAsync(2);
            frame.UpdateLayout();
            var again = await DrawAsync(frame);
            var settled = again.Width == drawn.Width && again.Height == drawn.Height && again.Pixels.AsSpan().SequenceEqual(drawn.Pixels);
            drawn = again;
            if (settled)
            {
                return drawn;
            }
        }

        Console.Error.WriteLine($"The form's images were still changing after {ImageWait.TotalSeconds:0.#} s; drawing it as it is.");
        return drawn;
    }

    private static async Task<(int Width, int Height, byte[] Pixels)> DrawAsync(FrameworkElement frame)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(frame);
        if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0)
        {
            throw new InvalidOperationException("The form drew nothing (it may be too large to draw).");
        }

        return (bitmap.PixelWidth, bitmap.PixelHeight, (await bitmap.GetPixelsAsync()).ToArray());
    }

    /// <summary>The element and everything below it in the visual tree.</summary>
    public static IEnumerable<DependencyObject> SelfAndDescendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            for (var i = VisualTreeHelper.GetChildrenCount(current) - 1; i >= 0; i--)
            {
                stack.Push(VisualTreeHelper.GetChild(current, i));
            }
        }
    }

    /// <summary>Lets queued UI work (Loaded events, deferred layout) run before drawing.</summary>
    public static Task IdleAsync()
    {
        var done = new TaskCompletionSource();
        if (DispatcherQueue.GetForCurrentThread()?.TryEnqueue(DispatcherQueuePriority.Low, () => done.TrySetResult()) != true)
        {
            done.TrySetResult();
        }

        return done.Task;
    }

    /// <summary>Waits until the compositor has drawn <paramref name="count"/> frames (at most a quarter second).</summary>
    public static async Task RenderedFramesAsync(int count)
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
            await done.Task.WaitAsync(TimeSpan.FromMilliseconds(250));
        }
        catch (TimeoutException)
        {
            // Draw anyway.
            Console.Error.WriteLine($"Only {seen} of {count} frames were drawn in 250 ms.");
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
        var pending = new List<(Uri Uri, Task Done)>();
        var unsubscribe = new List<Action>();
        var showing = new List<UIElement>();
        foreach (var element in SelfAndDescendants(root))
        {
            var source = element switch
            {
                Image image => image.Source,
                Border { Background: ImageBrush brush } => brush.ImageSource,
                Panel { Background: ImageBrush brush } => brush.ImageSource,
                Control { Background: ImageBrush brush } => brush.ImageSource,
                Microsoft.UI.Xaml.Shapes.Shape { Fill: ImageBrush brush } => brush.ImageSource,
                _ => null,
            };
            if (source is not null)
            {
                showing.Add((UIElement)element);
            }

            // Handlers come off again: shared images (App resources) would otherwise gather one per render (review L36).
            if (source is BitmapImage { UriSource: { } uri, PixelWidth: 0 } bitmap && !Unavailable.Contains(uri))
            {
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                RoutedEventHandler opened = (_, _) => loaded.TrySetResult();
                ExceptionRoutedEventHandler failed = (_, e) =>
                {
                    Console.Error.WriteLine($"The image {uri} couldn't be loaded: {e.ErrorMessage}");
                    Unavailable.Add(uri);
                    loaded.TrySetResult();
                };
                bitmap.ImageOpened += opened;
                bitmap.ImageFailed += failed;
                unsubscribe.Add(() =>
                {
                    bitmap.ImageOpened -= opened;
                    bitmap.ImageFailed -= failed;
                });
                pending.Add((uri, loaded.Task));
            }
            else if (source is SvgImageSource { UriSource: { } svgUri } svg && !Unavailable.Contains(svgUri))
            {
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Windows.Foundation.TypedEventHandler<SvgImageSource, SvgImageSourceOpenedEventArgs> opened = (_, _) => loaded.TrySetResult();
                Windows.Foundation.TypedEventHandler<SvgImageSource, SvgImageSourceFailedEventArgs> failed = (_, e) =>
                {
                    Console.Error.WriteLine($"The image {svgUri} couldn't be loaded: {e.Status}");
                    Unavailable.Add(svgUri);
                    loaded.TrySetResult();
                };
                svg.Opened += opened;
                svg.OpenFailed += failed;
                unsubscribe.Add(() =>
                {
                    svg.Opened -= opened;
                    svg.OpenFailed -= failed;
                });
                pending.Add((svgUri, loaded.Task));
            }
        }

        try
        {
            if (pending.Count > 0)
            {
                await Task.WhenAll(pending.Select(p => p.Done)).WaitAsync(ImageWait);
            }
        }
        catch (TimeoutException)
        {
            // Draw without the images still loading. An offline URL isn't waited for again (it would add the whole wait
            // to every render); a file of the app's is: a missing one fails at once, so this one was only slow to decode,
            // and giving up on it for good would leave it blank until the next build.
            foreach (var (uri, done) in pending.Where(p => !p.Done.IsCompleted))
            {
                Console.Error.WriteLine($"The image {uri} took longer than {ImageWait.TotalSeconds:0.#} s to load; drawing without it.");
                if (uri.Scheme is "http" or "https")
                {
                    Unavailable.Add(uri);
                }
            }
        }
        finally
        {
            unsubscribe.ForEach(u => u());
        }

        return showing;
    }

    /// <summary>Images that failed, and web images that timed out: renders don't wait for them again (for this host process, so until a build).</summary>
    private static readonly HashSet<Uri> Unavailable = [];
}
