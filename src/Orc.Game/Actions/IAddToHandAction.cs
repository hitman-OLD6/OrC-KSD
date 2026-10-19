using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Players;

namespace Orc.Game.Actions;

/// <summary>
/// 「加入手牌」动作（S7/S10 动作接口）：独立可复用——抽牌是其主要调用者，创建入牌／回手／孵化等
/// 非抽牌路径同样经本接口（不得再各自直写手牌）。
/// </summary>
public interface IAddToHandAction
{
    /// <summary>加入手牌**触发端**（P5b：替换实现后外部仍需在此注册/改写）。</summary>
    Trigger<AddToHandTriggerView> Trigger { get; }

    /// <summary>加入手牌**处理端**（<c>Apply</c> 段＝入列表或爆牌）。</summary>
    Trigger<AddToHandHandleView> Handle { get; }

    /// <summary>把一张已存在的卡加入玩家手牌；满手＝爆牌失败分支（结果 <c>Blocked(HandFull)</c>）。</summary>
    Task<ActionResult> AddAsync(
        CardBase card, Player player, Card? triggerCard = null, CancellationToken ct = default);
}

/// <summary>
/// 「加入手牌」的**动作服务面**（S4）：处理端 handler 访问对局的受控写面——具名操作、不交出管理器实例。
/// <para>
/// 唯一调用者＝加入手牌动作默认处理端 handler。只发本动作归属信号：
/// <c>card.hand.add</c>（入列表）与 <c>card.burned</c>（爆牌）。
/// </para>
/// </summary>
public interface IHandActionServices
{
    /// <summary>未满手路径：尾部追加到玩家手牌 → <c>card.hand.add</c>（恰一次）。</summary>
    Task AddCardToHandAsync(Player player, CardBase card, CancellationToken ct = default);

    /// <summary>满手路径（爆牌）：销毁〔<c>card.destroyed</c>〕→ <c>card.burned</c>（恰一次）；不进手牌、不发 <c>card.discarded</c>。</summary>
    Task BurnCardAtHandLimitAsync(Player player, CardBase card, CancellationToken ct = default);
}
