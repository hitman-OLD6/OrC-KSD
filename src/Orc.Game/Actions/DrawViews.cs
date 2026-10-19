using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Players;

namespace Orc.Game.Actions;

/// <summary>
/// 抽牌**触发端**视图（S7）：两入口（卡组顶／指定牌）共用同一形状——差异只在取件（本视图的
/// <see cref="Card"/> 由哪个触发端产出），取件完成后原样交给同一个处理端。
/// </summary>
[ContextView]
public class DrawTriggerView
{
    /// <summary>抽牌玩家（必填）。</summary>
    [Read]
    public virtual Player Player { get; set; } = null!;

    /// <summary>已从卡组取出的卡（必填；取件在触发端完成）。</summary>
    [Read]
    public virtual CardBase Card { get; set; } = null!;

    /// <summary>触发者卡牌（效果发起时携带；回合抽牌＝空）。</summary>
    [Optional]
    [Read]
    public virtual Card? TriggerCard { get; set; }

    /// <summary>本次执行的结果载体（框架内部；作者不读写）。</summary>
    [Optional]
    [Read]
    public virtual ActionTargetResult? Result { get; set; }
}

/// <summary>
/// 抽牌**处理端**视图（S7）：<c>Apply</c> 段先发 <c>card.drawn</c>（恰一次），再调「加入手牌」子动作。
/// </summary>
[ContextView]
public class DrawHandleView
{
    /// <summary>抽牌玩家（必填）。</summary>
    [Read]
    public virtual Player Player { get; set; } = null!;

    /// <summary>已从卡组取出的卡（必填）。</summary>
    [Read]
    public virtual CardBase Card { get; set; } = null!;

    /// <summary>触发者卡牌（与触发端一致；跨动作透传给加入手牌）。</summary>
    [Optional]
    [Read]
    public virtual Card? TriggerCard { get; set; }

    /// <summary>本次执行的结果载体（框架内部；默认 handler 回填结局）。</summary>
    [Optional]
    [Read]
    public virtual ActionTargetResult? Result { get; set; }
}
