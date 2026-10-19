using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Players;

namespace Orc.Game.Actions;

/// <summary>
/// 「加入手牌」**触发端**视图（S7）：<c>Initiate</c> 做前置（卡存活／归属／未销毁），<c>Commit</c> 默认转发。
/// </summary>
[ContextView]
public class AddToHandTriggerView
{
    /// <summary>待加入的卡（必填）。</summary>
    [Read]
    public virtual CardBase Card { get; set; } = null!;

    /// <summary>落点玩家（必填；＝卡归属玩家）。</summary>
    [Read]
    public virtual Player Player { get; set; } = null!;

    /// <summary>触发者卡牌（抽牌路径＝抽牌动作携带；非抽牌路径＝效果宿主或空）。</summary>
    [Optional]
    [Read]
    public virtual Card? TriggerCard { get; set; }

    /// <summary>本次执行的结果载体（框架内部；作者不读写）。</summary>
    [Optional]
    [Read]
    public virtual ActionTargetResult? Result { get; set; }
}

/// <summary>
/// 「加入手牌」**处理端**视图（S7）：<c>Apply</c> 段完成入列表＋<c>card.hand.add</c>，
/// 或满手时完成爆牌（<c>DestroyCard</c> ＋ <c>card.burned</c>）。
/// </summary>
[ContextView]
public class AddToHandHandleView
{
    /// <summary>待加入的卡（必填）。</summary>
    [Read]
    public virtual CardBase Card { get; set; } = null!;

    /// <summary>落点玩家（必填）。</summary>
    [Read]
    public virtual Player Player { get; set; } = null!;

    /// <summary>触发者卡牌（与触发端一致）。</summary>
    [Optional]
    [Read]
    public virtual Card? TriggerCard { get; set; }

    /// <summary>本次执行的结果载体（框架内部；默认 handler 回填结局）。</summary>
    [Optional]
    [Read]
    public virtual ActionTargetResult? Result { get; set; }
}
