using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Cards;
using Orc.Game.Players;

namespace Orc.Game.Actions;

/// <summary>
/// 「加入手牌」动作（S7/S10 动作接口）：独立可复用——抽牌是其主要调用者，创建入牌／回手／孵化等
/// 非抽牌路径同样经本接口（不得再各自直写手牌）。
/// <para>
/// **两端是引擎成员（S13／U3）**：契约只暴露成员地址（<see cref="MemberAddress"/>，<c>engine#hand.add</c>），
/// 不再暴露 <see cref="Orc.Core.Trigger{TView}"/> 实例——实例是实现内部细节，随旧类型在 U5 退役。
/// 宿主按 <see cref="Ends"/> 装配（地址＝登记身份，实例＝旧身份，两者同一条登记项）。
/// </para>
/// </summary>
public interface IAddToHandAction
{
    /// <summary>加入手牌**触发端**的成员地址（<c>engine#hand.add</c>）。</summary>
    MemberAddress Trigger { get; }

    /// <summary>加入手牌**处理端**的成员地址（<c>engine#hand.add.handle</c>；<c>Apply</c> 段＝入列表或爆牌）。</summary>
    MemberAddress Handle { get; }

    /// <summary>两端登记项（触发端、处理端；成员地址＋旧身份触发器）。宿主据此装配对局注册表。</summary>
    IReadOnlyList<ActionTriggerEnd> Ends { get; }

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
