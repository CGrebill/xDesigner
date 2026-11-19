using XDesigner.Core.Rendering;
using XDesigner.Protocol.Rendering;

namespace XDesigner.Core.Tests;

public class RenderSchedulerTests
{
    private static RenderResult Ok(RenderRequest r) => new(r.Version, true, 1, 1, 1, [0, 0, 0, 0], null, RootKind.Other, null, null);

    [Fact]
    public async Task OnlyLatestPendingRequestIsSent_WhileOneIsInFlight()
    {
        var sent = new List<long>();
        var gate = new TaskCompletionSource();
        var delivered = new List<long>();
        var allDone = new TaskCompletionSource();
        var scheduler = new RenderScheduler(async (r, _) =>
        {
            lock (sent)
            {
                sent.Add(r.Version);
            }

            if (r.Version == 1)
            {
                await gate.Task;
            }

            return Ok(r);
        }, TimeSpan.Zero);
        scheduler.Rendered += r =>
        {
            delivered.Add(r.Version);
            if (r.Version == 4)
            {
                allDone.TrySetResult();
            }
        };

        scheduler.Submit(new RenderRequest(1, "a"));
        await WaitUntil(() => sent.Count == 1);
        scheduler.Submit(new RenderRequest(2, "b"));
        scheduler.Submit(new RenderRequest(3, "c"));
        scheduler.Submit(new RenderRequest(4, "d"));
        gate.SetResult();
        await allDone.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal([1, 4], sent);
        Assert.Equal([1, 4], delivered);
        await WaitUntil(() => !scheduler.IsBusy);
    }

    [Fact]
    public async Task Debounce_CoalescesRapidSubmissions()
    {
        var sent = new List<long>();
        var done = new TaskCompletionSource();
        var scheduler = new RenderScheduler((r, _) =>
        {
            sent.Add(r.Version);
            return Task.FromResult(Ok(r));
        }, TimeSpan.FromMilliseconds(100));
        scheduler.Rendered += _ => done.TrySetResult();

        for (var v = 1; v <= 5; v++)
        {
            scheduler.Submit(new RenderRequest(v, "x"));
        }

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal([5], sent);
    }

    [Fact]
    public async Task TransportFailure_RaisesFailed_AndSchedulerKeepsWorking()
    {
        var failed = new TaskCompletionSource<RenderRequest>();
        var rendered = new TaskCompletionSource<RenderResult>();
        var scheduler = new RenderScheduler((r, _) =>
            r.Version == 1 ? Task.FromException<RenderResult>(new IOException("pipe broken")) : Task.FromResult(Ok(r)), TimeSpan.Zero);
        scheduler.Failed += (r, _) => failed.TrySetResult(r);
        scheduler.Rendered += r => rendered.TrySetResult(r);

        scheduler.Submit(new RenderRequest(1, "x"));
        Assert.Equal(1, (await failed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Version);
        await WaitUntil(() => !scheduler.IsBusy);

        scheduler.Submit(new RenderRequest(2, "y"));
        Assert.Equal(2, (await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Version);
    }

    [Fact]
    public async Task OlderResultThanOneAlreadyDelivered_IsDropped()
    {
        var delivered = new List<long>();
        var scheduler = new RenderScheduler((r, _) => Task.FromResult(Ok(r)), TimeSpan.Zero);
        scheduler.Rendered += r => delivered.Add(r.Version);

        scheduler.Submit(new RenderRequest(5, "x"));
        await WaitUntil(() => delivered.Count == 1);
        scheduler.Submit(new RenderRequest(3, "y"));
        await WaitUntil(() => !scheduler.IsBusy);

        Assert.Equal([5], delivered);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }
}
