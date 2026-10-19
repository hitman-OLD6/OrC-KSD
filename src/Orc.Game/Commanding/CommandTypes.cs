using Orc.Cards;
using Orc.Core;
using Orc.Game.Targeting;

namespace Orc.Game.Commanding;

// ─────────────────────────────────────────────────────────────────────────────
// 2C 指挥系统公共类型面：
// 结果对象（不抛；成功/取消/失败三态＋类别化原因——沿用 2B 统一结果模式）、
// 动作可用性聚合判定公开面（bool＋不可用原因＋候选列表；纯查询——无副作用、不发更新、不启动交互）、
// 「造成攻击伤害」结算记录（伏击改写标志）与流程内部承载类型。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>指挥流程结局状态（三态；沿用 2B 统一结果模式）。</summary>
public enum CommandResultStatus
{
    /// <summary>成功（指挥流程完整完成——含以攻击者死亡告终的攻击结算）。</summary>
    Success,

    /// <summary>取消（玩家/前端主动退出——拖回；零副作用：不发更新、不扣费、不清位、双方状态不变）。</summary>
    Cancelled,

    /// <summary>失败（携带原因类别，见 <see cref="CommandFailureReason"/>）。</summary>
    Failed,
}

/// <summary>
/// 指挥失败原因（类别化；成功 / 取消＝null）。
/// 发起拒绝四类（非己方回合 / 归属无效 / 已死亡 / 两动作均不可用）＋交互失败＋执行前复验拒绝＋结构兜底。
/// </summary>
public enum CommandFailureReason
{
    /// <summary>发起拒绝：非己方回合（行动方＝单位所有者＝当前回合方；含回合未就绪）。</summary>
    NonOwnerTurn,

    /// <summary>发起拒绝：归属无效（Owner＝null——无归属单位不可被指挥）。</summary>
    OwnerInvalid,

    /// <summary>发起拒绝：单位已死亡（在场未死亡为发起校验全集之一）。</summary>
    UnitDead,

    /// <summary>发起拒绝：两动作均不可用（bool＝false / 费用不足 / 无合法目标——候选均空；不进入交互、零副作用）。</summary>
    NoActionAvailable,

    /// <summary>交互失败：targeting 系统失败（桥接未装配 / 无可用候选 / 交互异常等；细节见结果对象的 Targeting）。</summary>
    TargetingFailed,

    /// <summary>执行前复验拒绝（分派后、执行前兜底；零副作用＋留痕；理论上不应发生——前置验证已保证）。</summary>
    ExecutionRejected,

    /// <summary>指挥流程结构故障（契约兜底；不应发生的结构性错误，防御类别）。</summary>
    CommandFlowFault,

    /// <summary>发起拒绝：对局已结束（终局后所有游戏动作入口拒绝——零副作用、状态不推进）。</summary>
    GameEnded,

    /// <summary>发起拒绝：非"进行"相位（准备／换牌相位下指挥入口拒绝——零副作用、状态不推进）。</summary>
    PhaseBlocked,
    InteractionBlocked,
}

/// <summary>
/// 动作阻断原因（动作级可用性判定；bool＋不可用原因面的原因类别）：
/// 前三项为流程级资格原因（单位不可被指挥——两动作同因被阻断）；后三项为动作级原因（bool / 费用 / 候选）。
/// </summary>
public enum CommandBlockReason
{
    /// <summary>非己方回合（含回合未就绪；行动方＝单位所有者＝当前回合方）。</summary>
    NonOwnerTurn,

    /// <summary>归属无效（Owner＝null——无归属单位不可被指挥）。</summary>
    OwnerInvalid,

    /// <summary>单位已死亡（在场未死亡为发起校验全集之一；尸体不可被指挥、不参与目标筛选）。</summary>
    UnitDead,

    /// <summary>行动状态不可用（可移动 / 可攻击 bool＝false）。</summary>
    FlagFalse,

    /// <summary>行动费不足（单位所有者的指挥点数 ＜ 单位实时行动费；动作级拒绝、整体置黑）。</summary>
    PointShortage,

    /// <summary>无合法候选（移动：非支援线单位或前线无空槽；攻击：范围矩阵/烟幕/守护筛选后无合法目标）。</summary>
    NoCandidates,

    /// <summary>被压制（A2 加性）：被压制单位不能移动或攻击（行动合法性消费面读「被压制」标记——
    /// 施加即时生效；「额外压制一回」只影响持续、不改变本阻断）。</summary>
    Suppressed,
    InteractionBlocked,
}

/// <summary>
/// 指挥流程统一结果对象（不抛；消费方读 <see cref="Status"/> ＋ <see cref="FailureReason"/> 分流）：
/// 成功＝流程完整完成；取消＝玩家主动退出（零副作用）；失败＝携带原因类别。
/// <see cref="Targeting"/> 可选透传交互细节（取消 / 交互失败场景），其余为 null。
/// 结果对象不是游戏状态修改通道；消费方读取判断使用。
/// </summary>
public sealed class CommandResult
{
    private CommandResult(CommandResultStatus status, CommandFailureReason? failureReason, TargeterResult? targeting)
    {
        Status = status;
        FailureReason = failureReason;
        Targeting = targeting;
    }

    /// <summary>结局状态（三态）。</summary>
    public CommandResultStatus Status { get; }

    /// <summary>失败原因（仅失败时非 null；成功 / 取消＝null）。</summary>
    public CommandFailureReason? FailureReason { get; }

    /// <summary>交互细节透传（可空；取消 / 交互失败场景携带）。</summary>
    public TargeterResult? Targeting { get; }

    /// <summary>是否成功（便捷读面）。</summary>
    public bool IsSuccess => Status == CommandResultStatus.Success;

    /// <summary>创建成功结果（框架内部）。</summary>
    internal static CommandResult Success() => new(CommandResultStatus.Success, failureReason: null, targeting: null);

    /// <summary>创建取消结果（框架内部）。</summary>
    internal static CommandResult Cancelled(TargeterResult? targeting = null)
        => new(CommandResultStatus.Cancelled, failureReason: null, targeting);

    /// <summary>创建失败结果（框架内部）。</summary>
    internal static CommandResult Failure(CommandFailureReason reason, TargeterResult? targeting = null)
        => new(CommandResultStatus.Failed, reason, targeting);
}

/// <summary>
/// 动作可用性（动作级判定输出）：可用与否＋不可用原因＋候选列表（引用形态与既有候选面一致）。
/// 不可用时候选为空列表（该动作整类不进入候选集——非逐目标筛选）；可用时候选非空（无合法目标＝不可用）。
/// </summary>
public sealed class CommandActionAvailability
{
    private static readonly IReadOnlyList<Ref<Entity>> EmptyCandidates = Array.Empty<Ref<Entity>>();

    private CommandActionAvailability(bool canUse, CommandBlockReason? blockReason, IReadOnlyList<Ref<Entity>> candidates)
    {
        CanUse = canUse;
        BlockReason = blockReason;
        Candidates = candidates;
    }

    /// <summary>动作是否可用（bool ∧ 规则 ∧ 费用聚合判定通过且有合法候选）。</summary>
    public bool CanUse { get; }

    /// <summary>不可用原因（可用＝null）。</summary>
    public CommandBlockReason? BlockReason { get; }

    /// <summary>候选列表（可用时非空；移动＝前线空槽引用；攻击＝合法敌方单位/敌方 HQ 引用；不可用＝空列表）。</summary>
    public IReadOnlyList<Ref<Entity>> Candidates { get; }

    /// <summary>创建「可用」结果（框架内部）。</summary>
    internal static CommandActionAvailability Available(IReadOnlyList<Ref<Entity>> candidates)
        => new(canUse: true, blockReason: null, candidates);

    /// <summary>创建「不可用」结果（框架内部）。</summary>
    internal static CommandActionAvailability Blocked(CommandBlockReason reason)
        => new(canUse: false, blockReason: reason, EmptyCandidates);
}

/// <summary>
/// 指挥可用性报告（动作可用性聚合判定的公开面；10.2 裁决：动作级可用性＋候选两侧同时暴露）。
/// 与指挥流程内部计算同源（同一计算路径、单一真源——预览与执行不漂移）。
/// 纯查询：无副作用、不发更新、不启动交互、不改变任何状态（可被 UI 预览高频调用）。
/// </summary>
public sealed class CommandAvailability
{
    internal CommandAvailability(
        CommandBlockReason? ineligibleReason,
        CommandActionAvailability move,
        CommandActionAvailability attack)
    {
        IneligibleReason = ineligibleReason;
        Move = move;
        Attack = attack;
    }

    /// <summary>流程级资格原因（null＝合格；非 null 时两动作同因被阻断：非己方回合 / 归属无效 / 已死亡）。</summary>
    public CommandBlockReason? IneligibleReason { get; }

    /// <summary>移动动作可用性（候选＝前线任意空槽引用列表）。</summary>
    public CommandActionAvailability Move { get; }

    /// <summary>攻击动作可用性（候选＝合法敌方单位/敌方 HQ 引用列表）。</summary>
    public CommandActionAvailability Attack { get; }

    /// <summary>是否存在任一可用动作（发起判定依据：均不可用＝发起拒绝、不进入交互）。</summary>
    public bool AnyActionAvailable => Move.CanUse || Attack.CanUse;
}

/// <summary>
/// 2C 触发器数据键约定（移动/攻击/造成攻击伤害触发数据；键＝视图属性名，字面值冻结）：
/// 实体类值统一为「引用形态」（<c>Ref&lt;Entity&gt;</c>——与 targeter 候选/产出引用面同源：候选产出什么、分派与触发数据即传什么）；
/// 槽位引用与 <c>unit.position.changed</c> 载荷同口径（均槽位引用）。「Unit / OldPosition / NewPosition」
/// 与 <c>GameUpdates</c> 对应载荷键同字面值（语义一致、通道不同）。
/// X1 加性：新增「触发者卡牌」键（<see cref="TriggerCard"/>）——4 个内置流程触发器共用（值＝效果宿主卡引用形态；
/// 非空＝效果引发、空＝玩家主动操作；填充规则与判等口径见该常量注释）。
/// </summary>
public static class CommandDataKeys
{
    /// <summary>指挥单位（值＝<c>Ref&lt;Entity&gt;</c>；指挥触发器数据）。</summary>
    public const string Card = "Card";

    /// <summary>行动方（值＝<c>Player</c> 对象引用；指挥触发器数据）。</summary>
    public const string Player = "Player";

    /// <summary>指挥流程箱（值＝<see cref="CommandFlowBox"/>；指挥触发器出参承载）。</summary>
    public const string FlowBox = "FlowBox";

    /// <summary>移动单位（值＝<c>Ref&lt;Entity&gt;</c>；移动触发器数据——Unit/OldPosition/NewPosition 与 unit.position.changed 载荷同口径）。</summary>
    public const string Unit = "Unit";

    /// <summary>原槽位引用（值＝<c>Ref&lt;Entity&gt;</c>；移动触发器数据）。</summary>
    public const string OldPosition = "OldPosition";

    /// <summary>目标槽位引用（值＝<c>Ref&lt;Entity&gt;</c>；移动触发器数据——新位置来源＝分派时选中的目标空槽）。</summary>
    public const string NewPosition = "NewPosition";

    /// <summary>攻击者（值＝<c>Ref&lt;Entity&gt;</c>；攻击/造成攻击伤害触发器数据）。</summary>
    public const string Attacker = "Attacker";

    /// <summary>攻击目标引用（值＝<c>Ref&lt;Entity&gt;</c>；攻击/造成攻击伤害触发器数据——单位目标＝单位引用、HQ 目标＝HQ 实体引用〔hq.Ref——W3-3 实体化〕）。</summary>
    public const string Target = "Target";

    /// <summary>攻击伤害结算记录（值＝<see cref="AttackDamageResolution"/>；造成攻击伤害触发器数据——伏击改写标志承载）。</summary>
    public const string Resolution = "Resolution";

    /// <summary>
    /// 触发者卡牌（X1 加性；R9 起值＝<see cref="Orc.Cards.Card"/> 对象引用——引发本次操作的效果宿主卡；4 个内置流程触发器共用）。
    /// 非空＝效果引发（且知来源卡）；空＝玩家主动操作（非效果引发）——两集合互斥穷尽。
    /// 填充规则：主动指挥（含其嵌套调用）＝缺省空（不携带）；效果引发＝调用方（效果侧）显式携带、逐次指定，
    /// 且同一效果链内传递（「攻击→伤害」链内一致；出现新的独立直接因由时以新因由为准）。缺省调用（未携带）＝空（向后兼容）。
    /// 写入边界＝<c>SourceNormalizer.ToCard</c>（唯一归一化点；失效/错型降级 null 并留痕）。
    /// 字面值与内核 <c>TriggerPayloadKeys.TriggerCard</c> 一致（同一载荷键：入口初值／帧解析共用）。
    /// </summary>
    public const string TriggerCard = Orc.Core.TriggerPayloadKeys.TriggerCard;
}

/// <summary>
/// 指挥流程箱（指挥触发器出参承载；框架内部）：流程处理器写入结局（成功/取消/失败＋原因），
/// 入口方法读取构造结果对象；null＝处理器未写入（结构性异常兜底＝流程故障）。
/// </summary>
internal sealed class CommandFlowBox
{
    /// <summary>流程结局（处理器写入；null＝未写入）。</summary>
    public CommandResult? Result { get; set; }
}

/// <summary>
/// 攻击伤害结算记录（「造成攻击伤害」触发器数据面；**只承载伏击改写标志**）。
/// R11 起：免疫归零迁至 <c>damage.apply</c>、重甲减伤迁至 <c>combat.damage</c>——
/// 本类型不再承载「本轮归零/减伤」容器（伤害归属按来源分层，不再挤在攻击记录里）。
/// </summary>
public sealed class AttackDamageResolution
{
    /// <summary>是否已改写（伏击命中；默认互伤的替代标志）。</summary>
    public bool IsRewritten { get; private set; }

    /// <summary>
    /// 改写方是否已**完整处置**本次攻击（S11 加性；伏击＝已走完死亡两端）。
    /// 与 <see cref="IsRewritten"/> 不混用：<c>IsRewritten</c>＝伏击已改写判定，<c>Handled</c>＝改写方已落地后果。
    /// </summary>
    public bool Handled { get; private set; }

    /// <summary>置改写标志（伏击逻辑命中时调用）。</summary>
    public void MarkRewritten() => IsRewritten = true;

    /// <summary>置「已完整处置」标志（改写方完成后果落地后调用）。</summary>
    public void MarkHandled() => Handled = true;
}
