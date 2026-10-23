using Orc.Game.Targeting;

namespace Orc.Game.Cards;

/// <summary>打出链结局状态（三态；成功 / 取消 / 失败）。</summary>
public enum PlayResultStatus
{
    /// <summary>成功（链完整完成）。</summary>
    Success,

    /// <summary>取消（交互取消——拖回取消；无失败原因字段，取消的唯一类别由本状态表达）。</summary>
    Cancelled,

    /// <summary>失败（携带原因类别，见 <see cref="PlayFailureReason"/>）。</summary>
    Failed,
}

/// <summary>
/// 打出链失败原因（类别化；成功 / 取消＝null——取消由 <see cref="PlayResultStatus.Cancelled"/> 状态表达）。
/// 命名以本枚举为契约；不支持自由文本判断（调用方需要程序化判断与提示分流）。
/// </summary>
public enum PlayFailureReason
{
    /// <summary>预打出拒绝：指挥点不足（单位 / 指令的开始阶段；不发起 targeter 请求、不发任何更新）。</summary>
    PrePlayPointShortage,

    /// <summary>预打出拒绝：无可用空槽位（单位开始阶段；候选为空——不进入交互、不发任何更新）。</summary>
    PrePlayNoAvailableSlots,

    /// <summary>交互失败：targeting 系统失败（桥接未装配 / 无可用候选 / 交互异常等；细节见结果对象的 <see cref="PlayResult.Targeting"/>）。</summary>
    TargetingFailed,

    /// <summary>打出段复验失败：指挥点不足（预打出成功后点数漂移；仅本次取消语义＋留痕；零副作用——不部署、不扣费、不发更新、不离手）。</summary>
    PlayVerificationRejected,

    /// <summary>打出链结构故障（契约兜底；不应发生的结构性错误，防御类别）。</summary>
    PlayChainFault,

    /// <summary>目标槽位非空（部署 / 加入拒绝；零副作用）。</summary>
    TargetSlotOccupied,

    /// <summary>
    /// 目标位置已过期（提交的 <see cref="Orc.Game.Board.FormationGap"/> 属历史快照、线已变化；零副作用）。
    /// <para>与 <see cref="TargetSlotOccupied"/> 区分：这里位置并没有被占，是引用本身失效——
    /// 调用方须按「现取现用」重取插入点，而不是当作"被占用"重试。</para>
    /// </summary>
    PositionStale,

    /// <summary>单位已单位化（重复部署 / 重复加入拒绝；零副作用）。</summary>
    UnitAlreadyUnitized,

    /// <summary>反制拒绝：指挥点不足（激活时；不改变状态、不扣点、不注册、不发任何更新）。</summary>
    CounterPointShortage,

    /// <summary>反制拒绝：非己方回合（该卡所属玩家不是当前行动方；口径同上）。</summary>
    CounterNotOwnerTurn,

    /// <summary>反制拒绝：一般性（验证未通过且未提供可辨识类别——改写缺类别的降级缺省；明确、不伪造具体类别）。</summary>
    CounterRejected,

    /// <summary>对局已结束（终局门禁：打出链各入口拒绝——零副作用、状态不推进）。</summary>
    GameEnded,

    /// <summary>非"进行"相位（相位门禁：准备／换牌相位下打出链各入口拒绝——零副作用、状态不推进）。</summary>
    PhaseBlocked,
    InteractionBlocked,
    NonOwnerTurn,
    CardNotInHand,
    FormationGapUnavailable,

    /// <summary>
    /// 反制不可用：卡已消耗（一次性命中后）或已销毁——**不可重试**。
    /// <para>
    /// 不复用 <see cref="CounterRejected"/>：后者的语义是「验证未通过、未提供可辨识类别」的**降级缺省**，
    /// 让一个已知且可命名的条件复用它会使降级语义失效；且 agent 需要区分「不可重试的缺席」
    /// 与「可换条件重试的拒绝」（点数不足／非己方回合）。
    /// </para>
    /// <para>
    /// 只覆盖 consumed/destroyed，**不覆盖** <c>counter.disarmed</c>（自动解除后卡完好，可再次激活）。
    /// </para>
    /// </summary>
    CounterUnavailable,
}

/// <summary>
/// 打出链统一结果对象（不抛；消费方读 <see cref="Status"/> ＋ <see cref="FailureReason"/> 分流）：
/// 成功＝链完整完成；取消＝交互取消（玩家拖回；无原因字段）；失败＝携带原因类别。
/// <see cref="Targeting"/> 可选透传交互细节（取消 / 交互失败 / 预打出确认场景），其余为 null。
/// 结果对象不是游戏状态修改通道；消费方读取判断使用。
/// </summary>
public sealed class PlayResult
{
    private PlayResult(PlayResultStatus status, PlayFailureReason? failureReason, TargeterResult? targeting)
    {
        Status = status;
        FailureReason = failureReason;
        Targeting = targeting;
    }

    /// <summary>结局状态（三态）。</summary>
    public PlayResultStatus Status { get; }

    /// <summary>失败原因（仅失败时非 null；成功 / 取消＝null）。</summary>
    public PlayFailureReason? FailureReason { get; }

    /// <summary>交互细节透传（可空；取消 / 交互失败 / 预打出确认场景携带）。</summary>
    public TargeterResult? Targeting { get; }

    /// <summary>是否成功（便捷读面）。</summary>
    public bool IsSuccess => Status == PlayResultStatus.Success;

    /// <summary>创建成功结果（框架内部）。</summary>
    internal static PlayResult Success() => new(PlayResultStatus.Success, failureReason: null, targeting: null);

    /// <summary>创建取消结果（框架内部）。</summary>
    internal static PlayResult Cancelled(TargeterResult? targeting = null)
        => new(PlayResultStatus.Cancelled, failureReason: null, targeting);

    /// <summary>创建失败结果（框架内部）。</summary>
    internal static PlayResult Failure(PlayFailureReason reason, TargeterResult? targeting = null)
        => new(PlayResultStatus.Failed, reason, targeting);
}

/// <summary>
/// 预打出捕获箱（2B；指令预打出 handler 提交捕获值的通道）：
/// 由打出链（PlayManager）创建并经触发数据（<see cref="Orc.Game.Triggers.CardTriggerView.CaptureBox"/>）提供给预打出 handler；
/// handler 经 <see cref="Capture"/> 提交捕获值（null 忽略），可多次提交（按提交序累积）。
/// 产出规则（<see cref="Result"/>）＝0 个 → null；1 个 → 该值；多个 → <c>object?[]</c> 列表（提交序）——
/// 该产出即自动移交为打出触发器 object? 参数（Argument）的捕获结果。
/// 取消通道（<see cref="CancelPrePlay"/>）：handler 显式请求取消预打出时置位——链层见之即中止预打出段（打出不发生、零副作用）。
/// </summary>
public sealed class CardCaptureBox
{
    private readonly List<object?> _values = new();

    /// <summary>已提交的捕获值（提交序只读快照）。</summary>
    public IReadOnlyList<object?> Values => _values;

    /// <summary>是否已请求取消预打出（handler 显式置位；链层检查后中止预打出段）。</summary>
    public bool IsPrePlayCancelled { get; private set; }

    /// <summary>提交一个捕获值（null 忽略；可多次提交）。</summary>
    public void Capture(object? value)
    {
        if (value is null)
        {
            return;
        }

        _values.Add(value);
    }

    /// <summary>请求取消预打出（handler 显式动作：如 handler 发起的交互被取消时——预打出段中止、打出不发生、零副作用）。</summary>
    public void CancelPrePlay() => IsPrePlayCancelled = true;

    /// <summary>捕获产出（移交打出触发器 object? 参数的形态）：0 个 → null；1 个 → 该值；多个 → 列表（object?[]，提交序）。</summary>
    public object? Result => _values.Count switch
    {
        0 => null,
        1 => _values[0],
        _ => _values.ToArray(),
    };
}
