using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Players;

namespace Orc.Game.Actions;

/// <summary>
/// 抽牌动作（S7/S10 动作接口）：两入口（卡组顶／指定牌）共用同一处理端。
/// </summary>
public interface IDrawAction
{
    /// <summary>卡组顶抽牌**触发端**。</summary>
    Trigger<DrawTriggerView> Top { get; }

    /// <summary>指定牌抽取**触发端**。</summary>
    Trigger<DrawTriggerView> Specific { get; }

    /// <summary>抽牌**处理端**（两入口共用）。</summary>
    Trigger<DrawHandleView> Handle { get; }

    /// <summary>抽卡组顶（移除由本动作触发端完成；空卡组＝集合契约的既有抛错）。</summary>
    Task<ActionResult> DrawTopAsync(Player player, Card? triggerCard = null, CancellationToken ct = default);

    /// <summary>抽取指定卡实例（前置不满足＝<c>TargetInvalid</c>、零变更零信号）。</summary>
    Task<ActionResult> DrawSpecificAsync(
        CardBase card, Player player, Card? triggerCard = null, CancellationToken ct = default);
}

/// <summary>
/// 抽牌的**动作服务面**（S4）：触发端取件 ＋ 处理端发射 <c>card.drawn</c>。
/// <para>
/// 唯一调用者＝抽牌动作的触发端/处理端默认 handler。手牌写入不在此面——处理端必须调「加入手牌」子动作。
/// </para>
/// </summary>
public interface IDrawActionServices
{
    /// <summary>卡组顶取件（移除并返回实例；空卡组＝集合契约的明确抛错）。</summary>
    CardBase TakeTopFromDeck(Player player);

    /// <summary>指定实例取件（移除；调用方已校验归属与在组）。</summary>
    void TakeSpecificFromDeck(Player player, CardBase card);

    /// <summary>发射 <c>card.drawn</c>（恰一次；处理端在加入手牌之前调用）。</summary>
    Task EmitCardDrawnAsync(Player player, CardBase card, CancellationToken ct = default);
}
