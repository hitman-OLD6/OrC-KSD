using Orc.Server.Execution;
using Xunit;

namespace Orc.Server.Tests;

/// <summary>
/// S2b 串行执行管道：串行性、交互类插队、容量拒绝、消费者上下文、异常传播。
/// </summary>
public class SerialWorkQueueTests
{
    [Fact]
    public async Task Executes_StrictlyOneAtATime()
    {
        await using var queue = new SerialWorkQueue();
        var current = 0;
        var maxObserved = 0;
        var gate = new object();

        var tasks = Enumerable.Range(0, 100).Select(_ => queue.EnqueueAsync(
            WorkPriority.Command,
            async ct =>
            {
                lock (gate)
                {
                    current++;
                    maxObserved = Math.Max(maxObserved, current);
                }

                await Task.Delay(1, ct);
                lock (gate)
                {
                    current--;
                }

                return true;
            })).ToArray();

        await Task.WhenAll(tasks);
        Assert.Equal(1, maxObserved);
    }

    [Fact]
    public async Task Interactive_JumpsAheadOfQueuedCommands()
    {
        await using var queue = new SerialWorkQueue();
        var order = new List<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var blocking = queue.EnqueueAsync(WorkPriority.Command, async _ =>
        {
            order.Add("blocking");
            started.TrySetResult();
            await release.Task;
            return 0;
        });

        await started.Task;

        var c1 = queue.EnqueueAsync(WorkPriority.Command, _ => { order.Add("c1"); return Task.FromResult(0); });
        var c2 = queue.EnqueueAsync(WorkPriority.Command, _ => { order.Add("c2"); return Task.FromResult(0); });
        var i1 = queue.EnqueueAsync(WorkPriority.Interactive, _ => { order.Add("i1"); return Task.FromResult(0); });

        release.SetResult();
        await Task.WhenAll(blocking, c1, c2, i1);

        Assert.Equal(["blocking", "i1", "c1", "c2"], order);
    }

    [Fact]
    public async Task Rejects_WhenCapacityExceeded()
    {
        await using var queue = new SerialWorkQueue(capacity: 2);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var blocking = queue.EnqueueAsync(WorkPriority.Command, async _ =>
        {
            started.TrySetResult();
            await release.Task;
            return 0;
        });

        await started.Task;

        _ = queue.EnqueueAsync(WorkPriority.Command, _ => Task.FromResult(0));
        _ = queue.EnqueueAsync(WorkPriority.Command, _ => Task.FromResult(0));

        Assert.Throws<WorkQueueFullException>(() =>
        {
            _ = queue.EnqueueAsync(WorkPriority.Command, _ => Task.FromResult(0));
        });

        release.SetResult();
        await blocking;
    }

    [Fact]
    public async Task ConsumerContext_IsTrueInside_AndFalseOutside()
    {
        await using var queue = new SerialWorkQueue();

        Assert.False(SerialWorkQueue.IsConsumerContext);

        var inside = await queue.EnqueueAsync(
            WorkPriority.Command,
            _ => Task.FromResult(SerialWorkQueue.IsConsumerContext));

        Assert.True(inside);
        Assert.False(SerialWorkQueue.IsConsumerContext);
    }

    [Fact]
    public async Task Exceptions_ArePropagatedToCaller()
    {
        await using var queue = new SerialWorkQueue();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            queue.EnqueueAsync<bool>(WorkPriority.Command, _ => throw new InvalidOperationException("boom")));
    }
}
