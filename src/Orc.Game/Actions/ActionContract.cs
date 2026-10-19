using Orc.Core;
using Orc.Game.Cards;

namespace Orc.Game.Actions;

/// <summary>
/// 动作标识（S1；稳定键——装配期校验插件追加动作不得与内置 8 个冲突）。
/// 值即对外契约的稳定 id（如 <c>damage</c>），不随类型改名漂移。
/// </summary>
/// <param name="Value">稳定键（非空）。</param>
public readonly record struct ActionId(string Value)
{
    /// <summary>伤害结算（P1 唯一实例化者）。</summary>
    public static readonly ActionId Damage = new("damage");

    /// <summary>卡牌移动（P2；三入口——被指挥／被控／撤退——共用同一处理端）。</summary>
    public static readonly ActionId Move = new("move");

    /// <summary>抽牌（P2；卡组顶／指定牌两触发端 → 同一处理端）。</summary>
    public static readonly ActionId Draw = new("draw");

    /// <summary>加入手牌（P2；抽牌的子动作，亦可被非抽牌路径复用；满手＝爆牌失败分支）。</summary>
    public static readonly ActionId HandAdd = new("hand.add");

    /// <summary>死亡（P2；<c>Rewrite</c> 单改写位承载免死／击杀者／处置指向）。</summary>
    public static readonly ActionId Death = new("death");

    /// <summary>加入战场（P3；部署／加入两触发端 → 共享处理端「单位化 + 落位 + <c>card.placed</c>」）。</summary>
    public static readonly ActionId JoinBattlefield = new("join.battlefield");

    /// <summary>攻击（P3；<c>unit.attack</c> 触发端 → 攻击处理端）。</summary>
    public static readonly ActionId Attack = new("attack");

    /// <summary>指令施放（P4；<c>OrderFlow</c> 唯一 flow——反制 → 结算 → 收尾）。</summary>
    public static readonly ActionId Order = new("order.cast");

    /// <summary>内置八个动作的枚举序（装配期校验与来源查询的单一真源）。</summary>
    public static readonly IReadOnlyList<ActionId> All = new[]
    {
        HandAdd, Draw, Damage, Death, Attack, Move, JoinBattlefield, Order,
    };

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// 单目标动作结局（S3；入口分支只按本字段与 <see cref="ActionTargetResult.Disposition"/> 分支）。
/// </summary>
public enum ActionOutcomeKind
{
    /// <summary>已落地（含被改写后仍落地）。</summary>
    Applied = 0,

    /// <summary>到达处理端但被否决（免死/免疫/<c>Veto</c>）——有留痕、有 <see cref="ActionTargetResult.Reason"/>。</summary>
    Blocked = 1,

    /// <summary>前置阶段判定目标不合法（越界/已死/槽位被占）——未进入处理端。</summary>
    TargetInvalid = 2,

    /// <summary>编排层主动跳过（前置不满足/上游取消）——双方都无意向。</summary>
    Skipped = 3,
}

/// <summary>
/// 失败/否决类别（S3）：随阶段增补，不伪造动作专属原因；<c>null</c>＝非失败。
/// </summary>
public enum ActionFailureKind
{
    /// <summary>前置不满足（未进入处理端）。</summary>
    Precondition = 0,

    /// <summary>被否决（到达处理端后被阻断）。</summary>
    Blocked = 1,

    /// <summary>目标不合法。</summary>
    TargetInvalid = 2,

    /// <summary>动作被取消（上游中断）。</summary>
    Cancelled = 3,

    /// <summary>手牌已满（加入手牌失败分支）。</summary>
    HandFull = 4,

    /// <summary>死亡被阻止（免死）。</summary>
    DeathPrevented = 5,

    /// <summary>触发端验证判定器拒绝（未进入事件链；入口据此映射「执行被拒」）。</summary>
    ValidationRejected = 6,
}

/// <summary>
/// 动作处置指向（S3）：<c>null</c>＝沿用动作默认处置；非 <c>null</c> 由入口分支执行。
/// </summary>
public enum ActionDisposition
{
    /// <summary>改为加入手牌（R12 死亡→回手）。</summary>
    ToHand = 0,
}

/// <summary>
/// 结果改写标记（S3）：仅诊断/观测（事件流留痕与断言）用，**不得作为入口分支依据**。
/// </summary>
[Flags]
public enum ActionRewrites
{
    /// <summary>未发生改写。</summary>
    None = 0,

    /// <summary>数值被改写。</summary>
    Amount = 1,

    /// <summary>来源被改写。</summary>
    Source = 2,

    /// <summary>目标被改写。</summary>
    Target = 4,

    /// <summary>击杀者归属被改写。</summary>
    Killer = 8,

    /// <summary>处置指向被改写。</summary>
    Disposition = 16,
}

/// <summary>
/// 单目标的动作结果（S3；底层动作对每个目标分别返回一条）。
/// <para>
/// 每次执行创建一份，由处理端默认 handler 回填实扣量/结局；入口在动作返回后只读。
/// </para>
/// </summary>
public sealed class ActionTargetResult
{
    /// <summary>创建单目标结果（初始结局＝<see cref="ActionOutcomeKind.Skipped"/>，由处理端改写）。</summary>
    public ActionTargetResult(Ref<Entity> target, CardBase? subject, int requestedAmount)
    {
        ArgumentNullException.ThrowIfNull(target);
        Target = target;
        Subject = subject;
        RequestedAmount = requestedAmount;
    }

    /// <summary>目标引用（可能已失效）。</summary>
    public Ref<Entity> Target { get; }

    /// <summary>具体实例（返回载体；**不作分支依据**）。</summary>
    public CardBase? Subject { get; }

    /// <summary>结局（入口分支只看本字段与 <see cref="Disposition"/>）。</summary>
    public ActionOutcomeKind Outcome { get; private set; } = ActionOutcomeKind.Skipped;

    /// <summary>请求量（入口快照）。</summary>
    public int RequestedAmount { get; }

    /// <summary>实扣量（处理端落地后的实际变化；0＝无变化）。</summary>
    public int AppliedAmount { get; private set; }

    /// <summary>处置指向（<c>null</c>＝沿用动作默认处置；由处理端改写、入口执行）。</summary>
    public ActionDisposition? Disposition { get; set; }

    /// <summary>改写标记（仅诊断；不作分支依据）。</summary>
    public ActionRewrites Rewrites { get; private set; }

    /// <summary>失败/否决类别（非 <see cref="ActionOutcomeKind.Applied"/> 时可有值）。</summary>
    public ActionFailureKind? Reason { get; private set; }

    /// <summary>标记已落地（含被改写后仍落地；实扣量可为 0）。</summary>
    public void MarkApplied(int appliedAmount)
    {
        Outcome = ActionOutcomeKind.Applied;
        AppliedAmount = appliedAmount;
        Reason = null;
    }

    /// <summary>标记被否决（到达处理端后被阻断；有留痕）。</summary>
    public void MarkBlocked(ActionFailureKind reason)
    {
        Outcome = ActionOutcomeKind.Blocked;
        AppliedAmount = 0;
        Reason = reason;
    }

    /// <summary>标记目标不合法（未进入处理端）。</summary>
    public void MarkTargetInvalid()
    {
        Outcome = ActionOutcomeKind.TargetInvalid;
        AppliedAmount = 0;
        Reason = ActionFailureKind.TargetInvalid;
    }

    /// <summary>标记编排层主动跳过（未进入处理端；<paramref name="reason"/> 说明为何跳过）。</summary>
    public void MarkSkipped(ActionFailureKind reason)
    {
        Outcome = ActionOutcomeKind.Skipped;
        AppliedAmount = 0;
        Reason = reason;
    }

    /// <summary>追加改写标记（诊断用）。</summary>
    public void AddRewrite(ActionRewrites rewrite) => Rewrites |= rewrite;
}

/// <summary>
/// 一次动作执行的统一结果（S3）：按目标分别返回；<see cref="Targets"/> 至少一条。
/// </summary>
public sealed class ActionResult
{
    /// <summary>创建结果。</summary>
    public ActionResult(ActionId id, IReadOnlyList<ActionTargetResult> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        Id = id;
        Targets = targets;
    }

    /// <summary>创建单目标结果（便捷构造）。</summary>
    public ActionResult(ActionId id, ActionTargetResult target)
        : this(id, new[] { target })
    {
    }

    /// <summary>动作标识（稳定键）。</summary>
    public ActionId Id { get; }

    /// <summary>按目标分别返回的结果（不会为空；多目标按目标序）。</summary>
    public IReadOnlyList<ActionTargetResult> Targets { get; }
}
