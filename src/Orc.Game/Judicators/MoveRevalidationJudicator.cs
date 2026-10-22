using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Players;

namespace Orc.Game.Judicators;

/// <summary>
/// 移动复验判定器（J2；单位移动验证点专属——逐点专属判定器之一；K3 改造：leg 共享消重）：
/// 执行前兜底复验的验证承载（原 <c>CommandManager.RevalidateMove</c> 逐字迁移）。
/// 规则：终局后拒绝；操作角色引用（单位/原槽位/目标槽位）有效性与在场状态、leg 资格（K3：owner==current／
/// !destroyed／CanMove／被压制／行动费／位置〔源∈支援线〕——经共享 leg 条目单源取用）、位置一致性（复验固有兜底）、
/// 仅推进（目标须为前线空槽——调用点单源）、前线无存活敌方（K3：经 move.frontline-enemy 条目单源取用）。
/// 输入：refs（触发数据第一层引用收集——本验证点：单位/原槽位/目标槽位位于前位；尾部可携带触发者卡引用、不参与复验）
/// ＋subject（被判定对象＝操作单位引用——显式、Ref 形态）。
/// 装配期注入对局级只读设施（对局加载时加载、生命随对局、仅只读使用）：
/// 生命周期（终局门禁）、leg 资格通道（K3 共享条目——每次调用经统一解析点）、战场（布局读取）、
/// 推进前置通道（K3 move.frontline-enemy 条目——与可用性侧单源）。
/// 无状态：不持有跨调用可变状态；改写＝moding（移动复验改写——与攻击复验相互独立；leg/C8 条目改写＝可用性与复验同步）。
/// </summary>
internal sealed class MoveRevalidationJudicator : ValidationJudicator
{
    private readonly MatchLifecycle? _lifecycle;
    private readonly Func<UnitCard, BoardPosition?, LegEligibilityFailure?> _moveLegEligibility;
    private readonly Battlefield _battlefield;
    private readonly Func<Player, bool> _moveFrontlineEnemy;

    /// <summary>创建移动复验判定器（装配期注入对局级只读设施引用）。</summary>
    /// <exception cref="ArgumentNullException">moveLegEligibility / battlefield / moveFrontlineEnemy 为 null。</exception>
    internal MoveRevalidationJudicator(
        MatchLifecycle? lifecycle,
        Func<UnitCard, BoardPosition?, LegEligibilityFailure?> moveLegEligibility,
        Battlefield battlefield,
        Func<Player, bool> moveFrontlineEnemy)
    {
        ArgumentNullException.ThrowIfNull(moveLegEligibility);
        ArgumentNullException.ThrowIfNull(battlefield);
        ArgumentNullException.ThrowIfNull(moveFrontlineEnemy);

        _lifecycle = lifecycle;
        _moveLegEligibility = moveLegEligibility;
        _battlefield = battlefield;
        _moveFrontlineEnemy = moveFrontlineEnemy;
    }

    /// <inheritdoc />
    protected override ValidationVerdict ValidateLogic(IReadOnlyList<Ref<Entity>> refs, Ref<Entity>? subject)
    {
        // X1：refs＝触发数据第一层引用收集（插入序）——操作角色引用（Unit/OldPosition/NewPosition）位于前位，
        // 效果引发情形尾部可携带「触发者卡牌」引用（TriggerCard；不参与复验——去重后计数允许多余项）。
        if (refs.Count < 3)
        {
            return ValidationVerdict.Invalid();
        }

        if (_lifecycle?.IsEnded == true)
        {
            return ValidationVerdict.Invalid(); // 终局后移动流程拒绝（含触发器级对外入口；零副作用、状态不推进）
        }

        if (subject is null || !subject.IsAlive || subject.Value is not UnitCard unit)
        {
            return ValidationVerdict.Invalid();
        }

        if (!refs[1].IsAlive || refs[1].Value is not BoardPosition oldSlot)
        {
            return ValidationVerdict.Invalid();
        }

        if (!refs[2].IsAlive || refs[2].Value is not BoardPosition newPosition)
        {
            return ValidationVerdict.Invalid();
        }

        // 新位置：目标线＋插入序号（快照语义）。
        var newSlot = newPosition;

        // K3：leg 资格（共享条件源——原内联的 owner/destroyed/can/suppressed/cost/位置〔源∈支援线〕检查迁至
        // leg 条目；复验按 bool 语义消费：非 null＝Invalid；编排保持——leg 整体判定位于 refs/终局/subject 检查之后）。
        if (_moveLegEligibility(unit, oldSlot) is not null)
        {
            return ValidationVerdict.Invalid();
        }

        var state = unit.GetData<UnitStateData>();

        // 复验固有：位置一致性（所在线＋序号与载荷一致——复验兜底语义）。
        if (!ReferenceEquals(state.Line, oldSlot.Line) || state.Line.IndexOf(unit) != oldSlot.Index)
        {
            return ValidationVerdict.Invalid();
        }

        // 仅推进/撤退：目标线须为 Destinations 给出的线，且序号在可插入范围内。
        var destinationLine = CommandMovementRules.IsRetreat(_battlefield, unit, oldSlot)
            ? _battlefield.GetSupportLine(unit.Owner!)
            : _battlefield.FrontLine;
        if (!ReferenceEquals(newSlot.Line, destinationLine)
            || newSlot.Index < 0 || newSlot.Index > destinationLine.Size)
        {
            return ValidationVerdict.Invalid();
        }

        // K3：推进前置（经 move.frontline-enemy 条目——与可用性侧单源；owner 非 null 由 leg 资格保证）。
        if (!CommandMovementRules.IsRetreat(_battlefield, unit, oldSlot) && _moveFrontlineEnemy(unit.Owner!))
        {
            return ValidationVerdict.Invalid(); // 推进前置复验（后置项 C；防御性双保险）：前线存在存活敌方单位＝拒绝
        }

        return ValidationVerdict.Valid;
    }
}
