using System.Text.Json.Nodes;
using Orc.Game;
using Orc.Mcp.Contracts;

namespace Orc.Mcp.Match;

/// <summary>
/// 一次对局动作的结局（执行器内部形态）。
/// </summary>
/// <param name="Status">结局：<c>completed</c>/<c>targeting</c>/<c>not-ready</c>/<c>busy</c>/<c>failed</c>。</param>
/// <param name="Payload">引擎原生结果的 JSON 形态（completed）。</param>
/// <param name="Pending">待决选择请求（targeting/busy）。</param>
/// <param name="FailureCode">失败码（受控集合）。</param>
/// <param name="FailureMessage">失败说明。</param>
/// <param name="TimedOut">本轮应答是否因超时被按取消处理。</param>
public sealed record ActionOutcome(
    string Status,
    JsonNode? Payload = null,
    PendingSelection? Pending = null,
    string? FailureCode = null,
    string? FailureMessage = null,
    bool TimedOut = false)
{
    public const string StatusCompleted = "completed";
    public const string StatusTargeting = "targeting";
    public const string StatusCancelled = "cancelled";
    public const string StatusNotReady = "not-ready";
    public const string StatusBusy = "busy";
    public const string StatusFailed = "failed";

    public bool IsTargeting => Status == StatusTargeting;

    /// <summary>引擎三态中的**取消**（合法结局，按成功结果处理；与失败区分）。</summary>
    public static ActionOutcome Cancelled(JsonNode? payload = null) => new(StatusCancelled, payload);

    public static ActionOutcome Completed(JsonNode? payload = null) => new(StatusCompleted, payload);

    public static ActionOutcome Targeting(PendingSelection pending) => new(StatusTargeting, Pending: pending);

    public static ActionOutcome NotReady()
        => new(StatusNotReady, FailureCode: McpCodes.NotReady, FailureMessage: "沙盒对局尚未创建（请先调用 sandbox.create）。");

    public static ActionOutcome Busy(PendingSelection? pending)
        => new(StatusBusy, Pending: pending, FailureCode: McpCodes.PendingTargeting,
            FailureMessage: "存在未完成的选择请求（请先调用 target.submit 或 target.cancel 应答）。");

    public static ActionOutcome NoPending()
        => new(StatusFailed, FailureCode: McpCodes.InvalidArguments, FailureMessage: "当前没有待决的选择请求。");

    public static ActionOutcome Failed(string code, string message, JsonNode? payload = null)
        => new(StatusFailed, payload, FailureCode: code, FailureMessage: message);
}

/// <summary>
/// 对局执行器：承载**唯一活跃对局**，并把一切对局调用串行化到单一写者
/// （引擎全域为无锁单线程语义；"单线程"实际靠**不并发**而非**同线程**）。
/// <para>
/// 动作生命周期门禁（避免"持锁等待应答"导致的死锁）：
/// ①动作调用取锁 → 启动动作 → 等到"完成**或**进入待决" → 释放锁；
/// ②进入待决后动作 Task 仍挂在后台、执行器标记为忙：此时只允许 <c>target.*</c> 与只读工具（由服务层门禁）；
/// ③<see cref="SubmitAsync"/> 取锁 → 驱动会话 → 等到"动作完成**或**再次待决" → 释放锁（多步选择天然支持）；
/// ④待决窗口内动作调用栈已展开（挂在 <c>TaskCompletionSource</c> 上）⇒ 引擎实际空闲，只读调用不会与之并发。
/// </para>
/// </summary>
public sealed class MatchExecutor
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Orc.Game.Match? _match;
    private Task<ActionOutcome>? _active;
    private int _handedOff;

    public MatchExecutor(TargeterBridgeAdapter bridge)
    {
        Bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    /// <summary>目标选择桥接（装配进 <c>Match</c> 构造）。</summary>
    public TargeterBridgeAdapter Bridge { get; }

    /// <summary>
    /// 动作"交出去之后自行完结"的观测点（F4③）：调用方已拿到 <c>targeting</c> 返回，
    /// 后续由选靶超时/取消把动作推进到完结——完成点不在任何客户端调用栈上，只能在这里挂推送。
    /// <para>
    /// 约束：订阅者只允许做 fire-and-forget 入队；此刻**不在**串行消费者上下文内（不能同步投影），
    /// 且订阅者异常不得污染动作结果。
    /// </para>
    /// </summary>
    public event Action<ActionOutcome>? Settled;

    /// <summary>当前活跃对局（无＝null）。</summary>
    public Orc.Game.Match? Match => _match;

    /// <summary>是否存在活跃对局。</summary>
    public bool HasMatch => _match is not null;

    /// <summary>是否存在未完成动作（待决中）。</summary>
    public bool IsBusy => _active is { IsCompleted: false };

    /// <summary>
    /// 若"忙"是因为选靶会话异常（桥已故障），清空活跃动作并以 <c>engine-rejected</c> 收束。
    /// <para>
    /// 供服务层门禁在"忙"分支之前调用：否则故障动作会把队列永久卡成 <c>busy</c>（F18 的直接症状）。
    /// </para>
    /// </summary>
    /// <returns>确实清空并给出失败结局＝true。</returns>
    public bool TryFailFaultedAction(out ActionOutcome? outcome)
    {
        outcome = null;

        if (_active is not { IsCompleted: false } || Bridge.Fault is not { } fault)
        {
            return false;
        }

        _active = null;
        outcome = Faulted(fault);
        return true;
    }

    private static ActionOutcome Faulted(Exception fault)
        => ActionOutcome.Failed(
            McpCodes.EngineRejected,
            $"选靶会话异常（{fault.GetType().Name}: {fault.Message}），动作已按失败收束。");

    /// <summary>装配活跃对局（唯一活跃对局约束：已有则拒绝）。</summary>
    public void Attach(Orc.Game.Match match)
    {
        ArgumentNullException.ThrowIfNull(match);

        if (_match is not null)
        {
            throw new InvalidOperationException("已存在活跃对局（同一进程只允许一个活跃对局——targeter 桥接无法区分多局归属）。");
        }

        _match = match;
        _active = null;
        Bridge.Reset();
    }

    /// <summary>摘除活跃对局（销毁）。返回被摘除的对局（无＝null）。</summary>
    public Orc.Game.Match? Detach()
    {
        var match = _match;
        _match = null;
        _active = null;
        Bridge.Reset();
        Bridge.ExpectClaimant(null);
        return match;
    }

    /// <summary>
    /// 声明**预期应答者**（AI 发起动作前调用）：该动作链内的待决请求在发布时即预占给该应答者。
    /// 动作链结束后（非 targeting 结局）自动清除。
    /// </summary>
    public void ExpectClaimant(string? claimant) => Bridge.ExpectClaimant(claimant);

    /// <summary>运行一次对局动作（取锁、启动、等到"完成或待决"）。</summary>
    public async Task<ActionOutcome> RunAsync(Func<Orc.Game.Match, Task<ActionOutcome>> action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_match is null)
        {
            return ActionOutcome.NotReady();
        }

        if (IsBusy)
        {
            // S13：上一轮动作的选靶会话已异常收束 → 先清空再回失败，避免队列被永久卡在 busy。
            if (TryFailFaultedAction(out var faulted))
            {
                return faulted!;
            }

            return ActionOutcome.Busy(Bridge.CurrentPending);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var match = _match;
            if (match is null)
            {
                return ActionOutcome.NotReady();
            }

            if (IsBusy)
            {
                if (TryFailFaultedAction(out var faulted))
                {
                    return faulted!;
                }

                return ActionOutcome.Busy(Bridge.CurrentPending);
            }

            Bridge.Reset();
            Volatile.Write(ref _handedOff, 0); // 上一轮 targeting 的残留标记不得误触发
            var task = ObserveSettle(Guard(action, match, ct));
            _active = task;

            var outcome = await WaitAsync(task).ConfigureAwait(false);
            if (!outcome.IsTargeting)
            {
                Bridge.ExpectClaimant(null);
            }

            return outcome;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 包装动作 Task：完结时若"动作曾被交出去（targeting 已返回给调用方）"，则触发一次 <see cref="Settled"/>。
    /// </summary>
    private async Task<ActionOutcome> ObserveSettle(Task<ActionOutcome> task)
    {
        try
        {
            var outcome = await task.ConfigureAwait(false);
            if (Interlocked.Exchange(ref _handedOff, 0) == 1)
            {
                RaiseSettled(outcome);
            }

            return outcome;
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _handedOff, 0);
            RaiseSettled(ActionOutcome.Failed(McpCodes.EngineRejected, ex.Message));
            throw;
        }
    }

    private void RaiseSettled(ActionOutcome outcome)
    {
        try
        {
            Settled?.Invoke(outcome);
        }
        catch (Exception)
        {
            // 订阅者异常不得污染动作结果。
        }
    }

    /// <summary>应答当前待决请求并推进动作（取锁、驱动会话、等到"完成或再次待决"）。</summary>
    public async Task<ActionOutcome> SubmitAsync(
        string sessionId,
        IReadOnlyList<int> picks,
        bool cancel,
        CancellationToken ct,
        string? claimant = null)
    {
        if (_active is null || _active.IsCompleted)
        {
            return ActionOutcome.NoPending();
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var active = _active;
            if (active is null || active.IsCompleted)
            {
                return ActionOutcome.NoPending();
            }

            if (!Bridge.Submit(sessionId, picks, cancel, claimant))
            {
                return ActionOutcome.Failed(
                    McpCodes.InvalidArguments,
                    "会话标识不符、已无待决请求，或该请求已被另一方认领（先到先服务）。");
            }

            // 本调用方自己会等完结并推送 → 清位，避免 Settled 与调用方那次推送重复。
            Volatile.Write(ref _handedOff, 0);

            var outcome = await WaitAsync(active).ConfigureAwait(false);
            if (!outcome.IsTargeting)
            {
                Bridge.ExpectClaimant(null);
                return outcome with { TimedOut = Bridge.LastSubmissionTimedOut };
            }

            return outcome;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 只读执行面（多人支持 S4）：**不取门禁、不判 <c>Busy</c>**，仅供串行消费者上下文内的投影/动作可用性计算使用。
    /// <para>
    /// 为什么不能复用 <see cref="RunAsync"/>：选靶挂起期间 <c>IsBusy</c> 为真，<see cref="RunAsync"/> 会直接返回
    /// <c>Busy</c>——而选靶期恰是最需要高亮的时刻。
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">不在消费者上下文内，或无活跃对局。</exception>
    public ActionOutcome ReadOnly(Func<Orc.Game.Match, ActionOutcome> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        if (!Orc.Mcp.Execution.ConsumerContext.IsActive)
        {
            throw new InvalidOperationException("只读执行面只能在串行消费者上下文内调用（当前不在消费者上下文）。");
        }

        var match = _match ?? throw new InvalidOperationException("无活跃对局（只读执行面不可用）。");
        return read(match);
    }

    /// <summary>等待动作"完成或进入待决"（不忙转：每轮等待一个新的状态变更信号）。</summary>
    private async Task<ActionOutcome> WaitAsync(Task<ActionOutcome> task)
    {
        while (true)
        {
            if (task.IsCompleted)
            {
                return await task.ConfigureAwait(false);
            }

            // S13：选靶会话异常 → 动作按失败收束并清空活跃动作（否则 IsBusy 恒真）。
            if (Bridge.Fault is { } fault)
            {
                _active = null;
                return Faulted(fault);
            }

            if (Bridge.HasPending)
            {
                // 动作交给调用方：后续若自行完结，由 Settled 补推（清位发生在调用方开始等待时）。
                Volatile.Write(ref _handedOff, 1);
                return ActionOutcome.Targeting(Bridge.CurrentPending!);
            }

            var change = Bridge.ChangeTask;
            var first = await Task.WhenAny(task, change).ConfigureAwait(false);
            if (ReferenceEquals(first, task))
            {
                return await task.ConfigureAwait(false);
            }
        }
    }

    private static async Task<ActionOutcome> Guard(
        Func<Orc.Game.Match, Task<ActionOutcome>> action,
        Orc.Game.Match match,
        CancellationToken ct)
    {
        try
        {
            return await action(match).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ActionOutcome.Failed(McpCodes.Cancelled, "动作被取消（调用方取消令牌）。");
        }
        catch (InvalidOperationException ex)
        {
            return ActionOutcome.Failed(McpCodes.NotReady, $"引擎拒绝（门禁/状态不符）：{ex.Message}");
        }
        catch (KeyNotFoundException ex)
        {
            return ActionOutcome.Failed(McpCodes.EngineRejected, $"引擎拒绝（引用不存在）：{ex.Message}");
        }
        catch (Exception ex)
        {
            return ActionOutcome.Failed(McpCodes.EngineRejected, $"引擎异常：{ex.GetType().Name}: {ex.Message}");
        }
    }
}
