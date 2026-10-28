namespace Orc.Core;

/// <summary>
/// 执行帧（内部）：一次执行（顶层或嵌套）的运行期会话。
/// <para>
/// 承担：①当前执行者感知（AsyncLocal 栈；执行会话入/出栈——子流挂载、Interrupt 传播、空转判定共用）；
/// ②检查点状态（Stopped/Interrupted）与 ctx 状态查询面共用的唯一状态源；
/// ③source 构成素材（成员地址标签＋当前边名）。
/// </para>
/// <para>
/// **本类型随 `Trigger&lt;TView&gt;` 退役后仍不可删**（PROGRESS §27.4）：创建与恢复点已全部迁到新模型的执行面
/// ——<see cref="Orc.Core.Model.OrchestrationRuntime.RunMemberAsync(Orc.Core.Model.OrchestrationEntity, string, Context, LogicEngine?, CancellationToken)"/>、
/// <see cref="SignalBridge"/>、<see cref="Context"/>（<c>Stop</c>／<c>Interrupt</c>／<c>Engine</c> 都读本帧）。
/// 它从来不是"触发器的壳"，而是执行会话本身；旧触发器的删除只是把它原来的宿主文件一并带走了。
/// </para>
/// </summary>
internal sealed class ExecutionFrame
{
    private static readonly AsyncLocal<ExecutionFrame?> CurrentFrame = new();

    /// <summary>当前执行的栈顶帧（AsyncLocal；会话进入时入栈、退出时还原）。</summary>
    internal static ExecutionFrame? Current
    {
        get => CurrentFrame.Value;
        set => CurrentFrame.Value = value;
    }

    private Context? _context;

    internal ExecutionFrame(ExecutionFrame? parent, EventStream stream, string triggerLabel, LogicEngine? engine)
    {
        Parent = parent;
        Stream = stream;
        TriggerLabel = triggerLabel;
        Engine = engine;
    }

    /// <summary>父帧（触发者执行会话；无父时为 null——顶层）。</summary>
    internal ExecutionFrame? Parent { get; }

    /// <summary>本次执行的流。</summary>
    internal EventStream Stream { get; }

    /// <summary>本次执行所属引擎（S-C2；供 <see cref="Context.Engine"/> 读取；嵌套执行沿调用传入）。</summary>
    internal LogicEngine? Engine { get; }

    /// <summary>执行者标签（成员地址或卡牌成员名）。</summary>
    internal string TriggerLabel { get; }

    /// <summary>当前正在执行的边名（source 构成用；未进入边时为 null）。</summary>
    internal string? CurrentEventName { get; set; }

    /// <summary>
    /// 当前触发者卡牌（R9；帧级解析结果）：入口初值（载荷 <c>TriggerCard</c>，仅接受 Card/null）或父帧继承。
    /// </summary>
    internal Orc.Cards.Card? TriggerCard { get; set; }

    internal Func<string, CancellationToken, Task>? SeamExecutor { get; set; }

    /// <summary>本级停止（Stop）标记。</summary>
    internal bool Stopped { get; set; }

    /// <summary>链中断（Interrupt）标记。</summary>
    internal bool Interrupted { get; set; }

    internal void AttachContext(Context context) => _context = context;

    /// <summary>解除上下文关联（b4：沿用调用方上下文时，执行结束后不留悬挂帧）。</summary>
    internal void ReleaseContext() => _context = null;

    /// <summary>Stop（仅本级）：标记本层剩余边停止；不影响父层与子执行；写入事件流。</summary>
    internal void RequestStop()
    {
        if (_context is null)
        {
            return; // 未关联执行会话（执行路径之外）防御：无操作
        }

        Stopped = true;
        Stream.Write(
            LogEntryKind.Log,
            LogLevel.Info,
            BuildSource(TriggerLabel, CurrentEventName),
            "执行被停止（Stop）：本级剩余步骤不再执行。",
            new[] { "stop" },
            data: null);
    }

    /// <summary>静默停止（仅本级；不写事件流条目——disposing 守卫过滤专用）。</summary>
    internal void RequestStopSilent()
    {
        if (_context is null)
        {
            return;
        }

        Stopped = true;
    }

    /// <summary>
    /// Interrupt（链级）：标记当前活跃执行栈全部帧（Interrupt 所在层、全部祖先层，以及正在运行的更深子层）；
    /// 各层在各自下一检查边界检查后停止；已中断状态下链上新发起的执行空转。写入事件流。
    /// </summary>
    internal void RequestInterrupt()
    {
        if (_context is null)
        {
            return;
        }

        Stream.Write(
            LogEntryKind.Log,
            LogLevel.Warning,
            BuildSource(TriggerLabel, CurrentEventName),
            "执行被中断（Interrupt）：动作链取消，链上剩余步骤不再执行。",
            new[] { "interrupt" },
            data: null);

        var top = Current;
        if (top is not null)
        {
            for (var frame = top; frame is not null; frame = frame.Parent)
            {
                frame.Interrupted = true;
                frame.Stream.Interrupted = true;
            }
        }
        else
        {
            for (var frame = this; frame is not null; frame = frame.Parent)
            {
                frame.Interrupted = true;
                frame.Stream.Interrupted = true;
            }
        }
    }

    /// <summary>沿帧链（含起点帧）检查是否存在中断标记（用于已中断状态下的空转判定）。</summary>
    internal static bool IsChainInterrupted(ExecutionFrame? frame)
    {
        for (var f = frame; f is not null; f = f.Parent)
        {
            if (f.Interrupted)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>source 构成：执行者标签＋边名（无边上下文时省略）。</summary>
    internal static string BuildSource(string triggerLabel, string? eventName)
        => string.IsNullOrEmpty(eventName) ? triggerLabel : $"{triggerLabel}/{eventName}";
}
