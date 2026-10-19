using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;

namespace Orc.Game.Actions;

/// <summary>
/// 死亡**触发端**视图（S8）：<see cref="ShouldDie"/> 由触发端写入 payload（默认 <c>true</c>）。
/// </summary>
[ContextView]
public class DeathTriggerView
{
    /// <summary>将死亡的单位（必填）。</summary>
    [Read]
    public virtual UnitCard Unit { get; set; } = null!;

    /// <summary>击杀者归属（可空＝无归属驱动来源，如修饰到期致防御归零）。</summary>
    [Optional]
    [Read]
    public virtual Card? Killer { get; set; }

    /// <summary>是否死亡（触发端默认写入 <c>true</c>；处理端 <c>Rewrite</c> 段可改写）。</summary>
    [Read]
    public virtual bool ShouldDie { get; set; }

    /// <summary>触发者卡牌（效果发起时携带；普通死亡＝空）。</summary>
    [Optional]
    [Read]
    public virtual Card? TriggerCard { get; set; }

    /// <summary>本次执行的结果载体（框架内部；作者不读写）。</summary>
    [Optional]
    [Read]
    public virtual ActionTargetResult? Result { get; set; }
}

/// <summary>
/// 死亡**处理端**视图（S8）：<c>Rewrite</c> 段是**唯一**改写位——可改写是否死亡、击杀者归属、处置指向。
/// </summary>
[ContextView]
public class DeathHandleView
{
    /// <summary>将死亡的单位（必填）。</summary>
    [Read]
    public virtual UnitCard Unit { get; set; } = null!;

    /// <summary>击杀者归属（<c>Rewrite</c> 段可改写；改写结果进入 <c>card.died</c> 载荷）。</summary>
    [Optional]
    [Mutate]
    public virtual Card? Killer { get; set; }

    /// <summary>是否死亡（<c>false</c> ＝免死：零状态改动、仅 log 留痕、结果 <c>Blocked(DeathPrevented)</c>）。</summary>
    [Mutate]
    public virtual bool ShouldDie { get; set; }

    /// <summary>处置指向（<c>null</c> ＝沿用默认处置〔销毁〕；<c>ToHand</c> 由动作层包装在死亡链之后执行回手组合）。</summary>
    [Optional]
    [Mutate]
    public virtual ActionDisposition? Disposition { get; set; }

    /// <summary>触发者卡牌（与触发端一致；跨动作透传给加入手牌）。</summary>
    [Optional]
    [Read]
    public virtual Card? TriggerCard { get; set; }

    /// <summary>本次执行的结果载体（框架内部；默认 handler 回填结局）。</summary>
    [Optional]
    [Read]
    public virtual ActionTargetResult? Result { get; set; }
}
