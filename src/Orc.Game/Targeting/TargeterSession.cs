using Orc.Core;

namespace Orc.Game.Targeting;

/// <summary>
/// 会话实现（引擎侧）：驱动组装方流程；流程产出的选择器进入队列供前端拉取；
/// 流程结束＝终局（<see cref="Result"/> 可读、<see cref="NextAsync"/> 返回 null）。
/// </summary>
internal sealed class TargeterSession : ITargeterSession
{
    private readonly object _sync = new();
    private readonly Queue<ISelectorInstance> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly TaskCompletionSource<TargeterResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _completed;

    internal TargeterSession(Func<ITargeterFlow, Task<TargeterResult>> flow, ITargetingTraceSink trace)
    {
        _ = RunFlowAsync(flow, trace);
    }

    /// <summary>终局任务（Manager await）。</summary>
    internal Task<TargeterResult> Completion => _completion.Task;

    /// <inheritdoc />
    public TargeterResult? Result { get; private set; }

    /// <inheritdoc />
    public async Task<ISelectorInstance?> NextAsync()
    {
        while (true)
        {
            lock (_sync)
            {
                if (_queue.Count > 0)
                {
                    return _queue.Dequeue();
                }

                if (_completed)
                {
                    return null;
                }
            }

            await _signal.WaitAsync().ConfigureAwait(false);
        }
    }

    /// <summary>入队一个选择器（供流程产出；前端据以拉取）。</summary>
    internal void Enqueue(ISelectorInstance instance)
    {
        lock (_sync)
        {
            _queue.Enqueue(instance);
        }

        _signal.Release();
    }

    private async Task RunFlowAsync(Func<ITargeterFlow, Task<TargeterResult>> flow, ITargetingTraceSink trace)
    {
        TargeterResult result;
        try
        {
            result = await flow(new TargeterFlow(this)).ConfigureAwait(false)
                     ?? TargeterResult.Failed(TargeterFailureReason.Other, "流程返回 null。");
        }
        catch (Exception ex)
        {
            TargetingTraceLog.Write(
                trace,
                LogLevel.Error,
                $"targeter 流程异常（兜底）：{ex.Message}。",
                new[] { $"reason:{TargeterFailureReason.Fault}" },
                new Dictionary<string, object?> { ["exceptionType"] = ex.GetType().FullName });

            result = TargeterResult.Failed(TargeterFailureReason.Fault, ex.Message);
        }

        lock (_sync)
        {
            Result = result;
            _completed = true;
        }

        _completion.TrySetResult(result);
        _signal.Release();
    }
}

/// <summary>流程宿主实现（组装方视角）：产出选择器 / 重入当前选择器。</summary>
internal sealed class TargeterFlow : ITargeterFlow
{
    /// <summary>单次选择器的重试上限（防死循环）。</summary>
    internal const int MaxRetry = 16;

    private readonly TargeterSession _session;
    private ISelectorInstance? _current;
    private int _retryCount;

    internal TargeterFlow(TargeterSession session)
    {
        _session = session;
    }

    /// <inheritdoc />
    public int RetryCount => _retryCount;

    /// <inheritdoc />
    public Task<SelectorResult<TResult>> Step<TResult>(Selector<TResult> selector, SelectorParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(parameter);

        _retryCount = 0;
        var instance = selector.CreateInstance(parameter);
        // S6：归属座位随选择器实例走（单点注入——不侵入各 CreateInstance 重载）。
        instance.Presentation.OwnerSeat = parameter.Owner?.Index;
        // R8：动作类别与归属座位同点写入（缺省＝unknown，不伪造类别）。
        instance.Presentation.Action = parameter.Action ?? SelectorActions.Unknown;
        _current = instance;
        _session.Enqueue(instance);
        return instance.Completion;
    }

    /// <inheritdoc />
    public Task<SelectorResult<TResult>> Retry<TResult>()
    {
        if (_current is not SelectorInstance<TResult> typed)
        {
            return Task.FromResult(SelectorResult<TResult>.Failed(SelectorFailureReason.Fault));
        }

        _retryCount++;
        if (_retryCount > MaxRetry)
        {
            return Task.FromResult(SelectorResult<TResult>.Failed(SelectorFailureReason.RetryLimitExceeded));
        }

        if (!typed.Reopen())
        {
            return Task.FromResult(SelectorResult<TResult>.Failed(SelectorFailureReason.Fault));
        }

        _session.Enqueue(typed);
        return typed.Completion;
    }
}
