using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
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
            frame = FrameCapture.CreateFrame(root, request.Theme, width * scale / host.RasterizationScale, height * scale / host.RasterizationScale);
            host.Surface.Children.Add(frame);
        }
        catch (Exception ex)
        {
            return RenderResult.Failed(request.Version, ToRenderError(ex));
        }

        try
        {
            var (pixelWidth, pixelHeight, pixels) = await FrameCapture.CaptureAsync(frame);
            var layout = LayoutTreeBuilder.Build(root);
            if (copy.IsWindow && layout is not null && layout.DesignId != 0)
            {
                // The stand-in Grid is the Window's client area: report it as the Window (a content control, not a grid).
                layout = layout with { TypeName = typeof(Window).FullName!, Panel = null };
            }

            ReplaceCurrent(frame, root);

            var rootKind = copy.IsWindow ? RootKind.Window : root is UserControl ? RootKind.UserControl : RootKind.Other;
            return new RenderResult(
                request.Version, true, pixelWidth, pixelHeight, pixelWidth / width, pixels, layout, rootKind,
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

    private static double FirstUsable(params double?[] candidates) =>
        candidates.First(v => v is { } d && double.IsFinite(d) && d > 0)!.Value;

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
