namespace Orc.Server.Execution;

/// <summary>工作项优先级：交互类（选靶/会话）先于对局动作。</summary>
public enum WorkPriority
{
    /// <summary>交互类（<c>target.*</c>、<c>session.*</c>）——动作可能正挂在等待它。</summary>
    Interactive = 0,

    /// <summary>普通对局动作与房间操作。</summary>
    Command = 1,
}

/// <summary>队列在容量上限时的拒绝（调用方转 <c>busy</c> 业务结果）。</summary>
public sealed class WorkQueueFullException : InvalidOperationException
{
    public WorkQueueFullException(string message) : base(message)
    {
    }
}

/// <summary>
/// 串行执行管道（唯一写者）：所有进入引擎的工作项经此单消费者依次执行。
/// <para>
/// 设计要点：
/// ① 同优先级严格保序（优先级键＝(优先级, 入队序号)）；
/// ② 交互类可插到排队中的动作之前，但**不能**抢占正在执行的工作项；
/// ③ <see cref="IsConsumerContext"/> 为真时表示当前在消费者上下文内，
///    供只读执行面（<c>RunReadOnlyAsync</c>）做误用断言；
/// ④ 容量满时立即拒绝，不阻塞网络线程。
/// </para>
/// </summary>
public sealed class SerialWorkQueue : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly PriorityQueue<WorkItem, (int Priority, long Sequence)> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;
    private readonly int _capacity;
    private long _sequence;

    public SerialWorkQueue(int capacity = 512)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "容量必须为正。");
        }

        _capacity = capacity;
        _loop = Task.Run(ConsumeAsync);
    }

    /// <summary>当前是否在消费者上下文内（委托服务层的标记，供测试与只读面断言共用）。</summary>
    public static bool IsConsumerContext => Orc.Mcp.Execution.ConsumerContext.IsActive;

    /// <summary>排队中的工作项数量（不含正在执行的）。</summary>
    public int PendingCount
    {
        get
        {
            lock (_sync)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>投递工作项并等待结果。</summary>
    /// <exception cref="WorkQueueFullException">队列已满。</exception>
    public Task<T> EnqueueAsync<T>(
        WorkPriority priority,
        Func<CancellationToken, Task<T>> work,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_shutdown.IsCancellationRequested, this);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem<T>(work, completion, ct);

        lock (_sync)
        {
            if (_queue.Count >= _capacity)
            {
                throw new WorkQueueFullException($"串行队列已满（容量 {_capacity}）。");
            }

            _queue.Enqueue(item, ((int)priority, _sequence++));
        }

        _signal.Release();
        return completion.Task;
    }

    private async Task ConsumeAsync()
    {
        var token = _shutdown.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            WorkItem? item;
            lock (_sync)
            {
                _queue.TryDequeue(out item, out _);
            }

            if (item is null)
            {
                continue;
            }

            using (Orc.Mcp.Execution.ConsumerContext.Enter())
            {
                await item.ExecuteAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 预期：关闭时取消。
        }

        _signal.Dispose();
        _shutdown.Dispose();
    }

    private abstract class WorkItem
    {
        public abstract Task ExecuteAsync();
    }

    private sealed class WorkItem<T>(
        Func<CancellationToken, Task<T>> work,
        TaskCompletionSource<T> completion,
        CancellationToken ct) : WorkItem
    {
        public override async Task ExecuteAsync()
        {
            try
            {
                completion.TrySetResult(await work(ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException oce)
            {
                completion.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }
    }
}
