using Orc.Cards;
using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Players;

namespace Orc.Game.Actions;

/// <summary>
/// 加入战场**触发端**视图（S9；有序列表模型）：部署与两个加入端共用同一视图——落位由 <see cref="Gap"/> 承载
/// （线＋插入序号＋快照；快照失效即拒绝），两段差异只在 band 内的步骤，不在视图。
/// </summary>
[ContextView]
public class JoinBattlefieldTriggerView
{
    /// <summary>将入场的单位（必填）。</summary>
    [Read]
    public virtual UnitCard Card { get; set; } = null!;

    /// <summary>目标插入空隙（必填；须为当前快照的有效空隙）。</summary>
    [Read]
    public virtual FormationGap Gap { get; set; } = null!;

    /// <summary>部署方（部署入口必填；加入入口可空——加入不要求归属）。</summary>
    [Optional]
    [Read]
    public virtual Player? Player { get; set; }

    /// <summary>触发者卡牌（效果发起时携带；普通打出＝空）。</summary>
    [Optional]
    [Read]
    public virtual Card? TriggerCard { get; set; }

    /// <summary>本次执行的结果载体（框架内部；入口只读结局）。</summary>
    [Optional]
    [Read]
    public virtual ActionTargetResult? Result { get; set; }
}

/// <summary>
/// 加入战场**处理端**视图（S9；有序列表模型）：共享处理端只读「入场单位 + 目标空隙」，无入口类型标志。
/// </summary>
[ContextView]
public class JoinBattlefieldHandleView
{
    /// <summary>将入场的单位（必填）。</summary>
    [Read]
    public virtual UnitCard Card { get; set; } = null!;

    /// <summary>目标插入空隙（必填）。</summary>
    [Read]
    public virtual FormationGap Gap { get; set; } = null!;

    /// <summary>部署方（可空——加入入口不要求归属；处理端不使用）。</summary>
    [Optional]
    [Read]
    public virtual Player? Player { get; set; }

    /// <summary>触发者卡牌（<c>card.placed</c> 的来源）。</summary>
    [Optional]
    [Read]
    public virtual Card? TriggerCard { get; set; }

    /// <summary>本次执行的结果载体（框架内部；默认 handler 回填结局）。</summary>
    [Optional]
    [Read]
    public virtual ActionTargetResult? Result { get; set; }
}
