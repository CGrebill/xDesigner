using XDesigner.App.Services;
using XDesigner.Core.Editing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;

namespace XDesigner.App;

public enum NotifyKind
{
    Success,
    Warning,
    Error,
}

/// <summary>A button on the notification strip.</summary>
public sealed record NotifyAction(string Label, Action Run, bool Dismisses = true);

/// <summary>A message for the notification strip, raised by view models.</summary>
public sealed record NotifyRequest(NotifyKind Kind, string Message, IReadOnlyList<NotifyAction>? Actions = null, bool Sticky = false);

/// <summary>
/// The notification strip under the title bar (D9): it slides open, pushing the panes down, and slides closed by itself
/// after a few seconds (not while the pointer is on it). Notifications that need an answer stay until answered.
/// </summary>
public sealed partial class MainWindow
{
    private const double StripHeight = 44; // 36 strip + 8 gap below it
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(220);

    private DispatcherQueueTimer? _notifyTimer;
    private TimeSpan _notifyDuration;
    private bool _notifySticky;
    private Storyboard? _notifyAnimation;

    /// <summary>Shows a message on the strip; <paramref name="sticky"/> keeps it until a button or the close box is used.</summary>
    public void ShowNotification(NotifyKind kind, string message, IReadOnlyList<NotifyAction>? actions = null, bool sticky = false)
    {
        var prefix = kind switch { NotifyKind.Success => "NotifySuccess", NotifyKind.Warning => "NotifyWarning", _ => "NotifyError" };
        NotifyStrip.Background = Brush(prefix + "Background");
        NotifyStrip.BorderBrush = Brush(prefix + "Border");
        NotifyIcon.Foreground = Brush(prefix + "Icon");
        NotifyIcon.Glyph = kind switch { NotifyKind.Success => "", NotifyKind.Warning => "", _ => "" };
        NotifyText.Text = message;
        ToolTipService.SetToolTip(NotifyText, message);

        NotifyActions.Children.Clear();
        foreach (var action in actions ?? [])
        {
            var button = new HyperlinkButton { Content = action.Label, Padding = new Thickness(8, 4, 8, 4) };
            button.Click += (_, _) =>
            {
                action.Run();
                if (action.Dismisses)
                {
                    HideNotification();
                }
            };
            NotifyActions.Children.Add(button);
        }

        _notifySticky = sticky;
        // Plain confirmations close after 5 s; anything with a button to press, or a problem to read, stays longer.
        _notifyDuration = kind == NotifyKind.Success && NotifyActions.Children.Count == 0 ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(10);
        Slide(open: true);
        RestartNotifyTimer(_notifyDuration);
    }

    private void HideNotification()
    {
        _notifyTimer?.Stop();
        Slide(open: false);
    }

    /// <summary>The handler confirmation (§10.1 step 7) as a strip message with Open in editor / Copy method name.</summary>
    private void ShowHandlerOutcome(EventHandlerOutcome outcome)
    {
        if (outcome.Handler is not { } handler)
        {
            ShowNotification(NotifyKind.Warning, outcome.Message);
            return;
        }

        ShowNotification(NotifyKind.Success, outcome.Message,
        [
            new NotifyAction("Open in editor", () =>
            {
                if (EditorLauncher.Open(handler.FilePath, handler.Line, ViewModel.Settings) is { } problem)
                {
                    ShowNotification(NotifyKind.Warning, problem);
                }
            }, Dismisses: false),
            new NotifyAction("Copy method name", () =>
            {
                var package = new DataPackage();
                package.SetText(handler.MethodName);
                Clipboard.SetContent(package);
            }, Dismisses: false),
        ]);
    }

    /// <summary>Animates the strip's height (so the panes below move with it) and slides its content in from above.</summary>
    private void Slide(bool open)
    {
        _notifyAnimation?.Stop();
        var from = NotifyHost.ActualHeight;
        var to = open ? StripHeight : 0;
        if (NotifyStrip.RenderTransform is not TranslateTransform translate)
        {
            NotifyStrip.RenderTransform = translate = new TranslateTransform { Y = -StripHeight };
        }

        if (open && from <= 0)
        {
            translate.Y = -StripHeight; // start fully above, then slide down
        }

        var ease = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn };
        var height = new DoubleAnimation { From = from, To = to, Duration = SlideDuration, EasingFunction = ease, EnableDependentAnimation = true };
        Storyboard.SetTarget(height, NotifyHost);
        Storyboard.SetTargetProperty(height, "Height");
        var offset = new DoubleAnimation { From = translate.Y, To = open ? 0 : -StripHeight, Duration = SlideDuration, EasingFunction = ease };
        Storyboard.SetTarget(offset, translate);
        Storyboard.SetTargetProperty(offset, "Y");

        var storyboard = new Storyboard { Children = { height, offset } };
        storyboard.Completed += (_, _) =>
        {
            NotifyHost.Height = to; // keep the end value once the animation lets go
            translate.Y = open ? 0 : -StripHeight;
        };
        _notifyAnimation = storyboard;
        storyboard.Begin();
    }

    private void RestartNotifyTimer(TimeSpan after)
    {
        _notifyTimer ??= CreateNotifyTimer();
        _notifyTimer.Stop();
        if (!_notifySticky)
        {
            _notifyTimer.Interval = after;
            _notifyTimer.Start();
        }
    }

    private DispatcherQueueTimer CreateNotifyTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) => HideNotification();
        return timer;
    }

    // Reading a message with the pointer on it holds it open; leaving gives a couple more seconds.
    private void NotifyHost_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => _notifyTimer?.Stop();

    private void NotifyHost_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (NotifyHost.Height > 0 || NotifyHost.ActualHeight > 0)
        {
            RestartNotifyTimer(TimeSpan.FromSeconds(2.5));
        }
    }

    private void NotifyClose_Click(object sender, RoutedEventArgs e) => HideNotification();

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
