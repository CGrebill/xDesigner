using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using XDesigner.Protocol.Rendering;

namespace XDesigner.RenderHost.Rendering;

/// <summary>
/// Turns a render copy of a XAML document into pixels plus a layout tree.
/// Must be used on the Avalonia UI thread. See docs/ARCHITECTURE.md §14.2.
/// </summary>
internal sealed class XamlRenderer
{
    /// <summary>Preview size used when neither the request nor the document gives one (decision D12).</summary>
    public static readonly Size DefaultDesignSize = new(800, 450);

    private readonly Assembly? _localAssembly;
    private readonly RenderCopySanitizer _sanitizer;
    private Window? _previousWindow;

    public XamlRenderer(Assembly? localAssembly, XamlTypeResolver? resolver = null)
    {
        _localAssembly = localAssembly;
        _sanitizer = new RenderCopySanitizer(resolver ?? new XamlTypeResolver());
    }

    /// <summary>The root object of the last successful render (kept alive for value queries).</summary>
    public object? CurrentRoot { get; private set; }

    public RenderResult Render(RenderRequest request)
    {
        Dispatcher.UIThread.VerifyAccess();

        object root;
        try
        {
            // With the document's place in the project assembly as base, "/Assets/logo.png" finds the project's resources.
            var xaml = _sanitizer.Sanitize(request.Xaml);
            var document = request.BaseUri is { Length: > 0 } baseUri && Uri.TryCreate(baseUri, UriKind.Absolute, out var uri)
                ? new RuntimeXamlLoaderDocument(uri, xaml)
                : new RuntimeXamlLoaderDocument(xaml);
            root = AvaloniaRuntimeXamlLoader.Load(
                document,
                new RuntimeXamlLoaderConfiguration { LocalAssembly = _localAssembly, DesignMode = true });
        }
        catch (Exception ex)
        {
            return RenderResult.Failed(request.Version, ToRenderError(ex));
        }

        Window window;
        RootKind rootKind;
        try
        {
            (window, rootKind) = HostRoot(root, request);
        }
        catch (Exception ex)
        {
            return RenderResult.Failed(request.Version, ToRenderError(ex));
        }

        try
        {
            // Default leaves the theme to the file (a <Window RequestedThemeVariant="Dark">) and the App (review L29).
            if (request.Theme is ThemeVariantKind.Light or ThemeVariantKind.Dark)
            {
                window.RequestedThemeVariant = request.Theme == ThemeVariantKind.Light ? ThemeVariant.Light : ThemeVariant.Dark;
            }

            window.Show();
            LayoutPasses(window);

            var scale = EffectiveScale(window.ClientSize, request.Scale);
            var (pixelWidth, pixelHeight, pixels) = RenderPixels(window, scale);
            var layout = LayoutTreeBuilder.Build(window);

            ReplaceCurrent(window, root);
            return new RenderResult(
                request.Version, true, pixelWidth, pixelHeight, scale, pixels, layout, rootKind,
                root is Window w ? w.Title : null, null);
        }
        catch (Exception ex)
        {
            CloseQuietly(window);
            return RenderResult.Failed(request.Version, ToRenderError(ex));
        }
    }

    private static (Window Window, RootKind Kind) HostRoot(object root, RenderRequest request)
    {
        if (root is Window window)
        {
            var size = request.DesignSize is { } ds && IsUsable(ds.Width) && IsUsable(ds.Height) // 0 or NaN: the file's size (review L35)
                ? new Size(ds.Width, ds.Height)
                : new Size(
                    FirstUsable(window.Width, Avalonia.Controls.Design.GetWidth(window), DefaultDesignSize.Width),
                    FirstUsable(window.Height, Avalonia.Controls.Design.GetHeight(window), DefaultDesignSize.Height));
            window.SizeToContent = SizeToContent.Manual;
            window.Width = size.Width;
            window.Height = size.Height;
            return (window, RootKind.Window);
        }

        if (root is Control control)
        {
            var size = request.DesignSize is { } ds && IsUsable(ds.Width) && IsUsable(ds.Height) // 0 or NaN: the file's size (review L35)
                ? new Size(ds.Width, ds.Height)
                : new Size(
                    FirstUsable(control.Width, Avalonia.Controls.Design.GetWidth(control), DefaultDesignSize.Width),
                    FirstUsable(control.Height, Avalonia.Controls.Design.GetHeight(control), DefaultDesignSize.Height));
            var host = new Window
            {
                SizeToContent = SizeToContent.Manual,
                Width = size.Width,
                Height = size.Height,
                Content = control,
            };
            // Design.ApplyDesignModeProperties is hidden from Avalonia 12's reference assemblies ([PrivateApi]),
            // so carry over the one design-time property that matters here by hand.
            if (Avalonia.Controls.Design.GetDataContext(control) is { } designData)
            {
                control.DataContext = designData;
            }

            return (host, control is UserControl ? RootKind.UserControl : RootKind.Other);
        }

        throw new NotSupportedException(
            $"Only Windows and controls can be designed; the document root is a {root.GetType().Name}.");
    }

    private static double FirstUsable(params double[] candidates) => candidates.First(IsUsable);

    private static bool IsUsable(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && v > 0;

    /// <summary>Runs layout until it settles (bounded) and flushes deferred dispatcher work such as template application.</summary>
    private static void LayoutPasses(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            if (window.IsMeasureValid && window.IsArrangeValid)
            {
                break;
            }
        }
    }

    /// <inheritdoc cref="FrameScale.MaxFramePixels"/>
    internal const double MaxFramePixels = FrameScale.MaxFramePixels;

    /// <inheritdoc cref="FrameScale.Effective"/>
    internal static double EffectiveScale(Size size, double requested) => FrameScale.Effective(size.Width, size.Height, requested);

    private static (int Width, int Height, byte[] Pixels) RenderPixels(Window window, double scale)
    {
        var size = window.ClientSize;
        var width = Math.Max(1, Math.Ceiling(size.Width * scale));
        var height = Math.Max(1, Math.Ceiling(size.Height * scale));
        var pixelSize = new PixelSize((int)width, (int)height);

        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);

        if (bitmap.Format is { } format && format != PixelFormat.Bgra8888)
        {
            throw new NotSupportedException($"Unexpected render target pixel format {format}; expected Bgra8888.");
        }

        var stride = pixelSize.Width * 4;
        var pixels = new byte[stride * pixelSize.Height];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(pixelSize), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        return (pixelSize.Width, pixelSize.Height, pixels);
    }

    /// <summary>Closes the live render (document closed in the app).</summary>
    public void Close()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_previousWindow is not null)
        {
            CloseQuietly(_previousWindow);
        }

        _previousWindow = null;
        CurrentRoot = null;
    }

    private void ReplaceCurrent(Window window, object root)
    {
        if (_previousWindow is not null && !ReferenceEquals(_previousWindow, window))
        {
            CloseQuietly(_previousWindow);
        }

        _previousWindow = window;
        CurrentRoot = root;
    }

    private static void CloseQuietly(Window window)
    {
        try
        {
            window.Close();
        }
        catch
        {
            // A misbehaving user control must not break the next render.
        }
    }

    /// <summary>Extracts message, position and offending type from XAML compiler/loader exceptions.</summary>
    internal static RenderError ToRenderError(Exception ex)
    {
        if (ex is TargetInvocationException { InnerException: { } inner })
        {
            ex = inner;
        }

        var line = ReadIntProperty(ex, "LineNumber") ?? ReadIntProperty(ex, "Line");
        var column = ReadIntProperty(ex, "LinePosition") ?? ReadIntProperty(ex, "Column") ?? ReadIntProperty(ex, "Position");

        // An exception thrown from a control's own code: name the control's type. The loader may wrap it,
        // so look through the inner exceptions too (innermost first: that's where the user code threw).
        var chain = new List<Exception>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            chain.Add(e);
        }

        var offendingType = Enumerable.Reverse(chain)
            .SelectMany(e => new System.Diagnostics.StackTrace(e, false).GetFrames())
            .Select(f => f.GetMethod()?.DeclaringType)
            .FirstOrDefault(t => t is not null && typeof(Control).IsAssignableFrom(t) && !IsAvaloniaType(t))
            ?.FullName;

        return new RenderError(ex.Message, ex.GetType().FullName, line, column, offendingType);
    }

    /// <summary>True for Avalonia's own assemblies ("Avalonia", "Avalonia.*"), not user ones like "AvaloniaApp1".</summary>
    internal static bool IsAvaloniaType(Type t) =>
        t.Assembly.GetName().Name is { } name
        && (name == "Avalonia" || name.StartsWith("Avalonia.", StringComparison.Ordinal));

    private static int? ReadIntProperty(Exception ex, string name) =>
        ex.GetType().GetProperty(name)?.GetValue(ex) switch
        {
            int i when i > 0 => i,
            _ => null,
        };
}
