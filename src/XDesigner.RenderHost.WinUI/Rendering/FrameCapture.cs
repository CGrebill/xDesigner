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
        foreach (var element in SelfAndDescendants(root))
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
            try
            {
                await Task.WhenAll(pending).WaitAsync(ImageWait);
            }
            catch (TimeoutException)
            {
                // Draw without the images still loading.
            }
        }

        return showing;
    }
}
