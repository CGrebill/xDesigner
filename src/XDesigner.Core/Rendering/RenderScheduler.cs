using XDesigner.Protocol.Rendering;

namespace XDesigner.Core.Rendering;

/// <summary>
/// Sends render requests for one document, latest wins: at most one request is in flight, and anything
/// submitted meanwhile replaces the waiting request instead of queueing behind it (§8.2).
/// Results older than one already delivered are dropped.
/// </summary>
/// <remarks>
/// Events are raised on the <see cref="SynchronizationContext"/> captured at construction (the UI thread in the app),
/// or on the thread pool when there is none (tests).
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The token source has no timer or callbacks to release; Cancel() is the shutdown.")]
public sealed class RenderScheduler
{
    private readonly Func<RenderRequest, CancellationToken, Task<RenderResult>> _render;
    private readonly TimeSpan _debounce;
    private readonly SynchronizationContext? _context;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _disposed = new();

    private RenderRequest? _pending;
    private bool _pumping;
    private long _lastDeliveredVersion = long.MinValue;

    public RenderScheduler(Func<RenderRequest, CancellationToken, Task<RenderResult>> render, TimeSpan? debounce = null)
    {
        _render = render;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(40);
        _context = SynchronizationContext.Current;
    }

    /// <summary>A render finished (successfully or with a XAML/render error inside the result).</summary>
    public event Action<RenderResult>? Rendered;

    /// <summary>The request could not be delivered (host gone, transport error).</summary>
    public event Action<RenderRequest, Exception>? Failed;

    /// <summary>True while a request is waiting or in flight.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_lock)
            {
                return _pumping;
            }
        }
    }

    public void Submit(RenderRequest request)
    {
        lock (_lock)
        {
            _pending = request;
            if (_pumping)
            {
                return;
            }

            _pumping = true;
        }

        _ = PumpAsync();
    }

    /// <summary>Stops delivering results; anything in flight is cancelled.</summary>
    public void Cancel() => _disposed.Cancel();

    private async Task PumpAsync()
    {
        try
        {
            if (_debounce > TimeSpan.Zero)
            {
                await Task.Delay(_debounce, _disposed.Token).ConfigureAwait(false);
            }

            while (true)
            {
                RenderRequest next;
                lock (_lock)
                {
                    if (_pending is null || _disposed.IsCancellationRequested)
                    {
                        _pumping = false;
                        return;
                    }

                    next = _pending;
                    _pending = null;
                }

                try
                {
                    var result = await _render(next, _disposed.Token).ConfigureAwait(false);
                    if (result.Version > _lastDeliveredVersion && !_disposed.IsCancellationRequested)
                    {
                        _lastDeliveredVersion = result.Version;
                        Raise(() => Rendered?.Invoke(result));
                    }
                }
                catch (Exception ex) when (!_disposed.IsCancellationRequested)
                {
                    Raise(() => Failed?.Invoke(next, ex));
                }
            }
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
            lock (_lock)
            {
                _pumping = false;
            }
        }
    }

    private void Raise(Action action)
    {
        if (_context is null)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }
}
