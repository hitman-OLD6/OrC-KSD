using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Cards;

namespace Orc.Game.Actions;

/// <summary>
/// 指令施放动作（S13 第 6 条）：动作实体接口——**唯一动作入口**。
/// <para>
/// 实现内部只做动作层包装：调 <see cref="OrderFlow.ExecuteAsync(Card, IReadOnlyList{ICastAction}, Card?, int, CancellationToken)"/>
/// （唯一 flow），再把 flow 结果翻译为 <see cref="ActionResult"/>。禁止出现第二个指令编排单元。
/// </para>
/// <para>
/// 主动效果的承载（v4 收口）：**卡实体上的组件边**——施放结算在 <c>engine#order.cast</c> 的宣告点发
/// <c>card.played</c>，订阅该信号的组件边随之执行；旧"指令实例集"（<c>ICastAction</c>）不再有生产来源，
/// 动作实现只传空集（旧面参数保留在 <see cref="Orc.Cards.OrderFlow"/> 签名上供内核测试直接驱动）。
/// </para>
/// <para>
/// **两端是引擎成员（S13／U3 裁决）**：<c>engine#order.cast</c>（宣告＋结算）／<c>engine#order.finalize</c>（收尾）。
/// OrderFlow 是指令流程的合法一环，两端与其它 8 个动作同处 <c>engine</c> 根下——因此「动作两端都是引擎成员」
/// 对 21/21 端成立。实例仍由内核 flow 持有（<see cref="Ends"/> 里以旧身份交给宿主装配），随旧类型在 U5 退役。
/// </para>
/// </summary>
public interface IOrderAction
{
    /// <summary>指令施放**触发端**的成员地址（<c>engine#order.cast</c>）。</summary>
    MemberAddress Cast { get; }

    /// <summary>指令**收尾**处理端的成员地址（<c>engine#order.finalize</c>）。</summary>
    MemberAddress Finalize { get; }

    /// <summary>两端登记项（施放、收尾；实例来自内核唯一 flow）。</summary>
    IReadOnlyList<ActionTriggerEnd> Ends { get; }

    /// <summary>
    /// 施放一张指令卡：反制检查 → 宣告/施放结算 →〔未中断时〕收尾。
    /// </summary>
    /// <param name="card">施放的指令卡（主动效果持有者兼来源）。</param>
    /// <param name="target">施放目标（可空）。</param>
    /// <param name="amount">施放数值。</param>
    /// <param name="triggerCard">触发者卡牌（可空；接口统一形态——P4 的施放归因取 <paramref name="card"/> 自身，
    /// 故该参数当前不参与编排）。</param>
    /// <param name="ct">取消。</param>
    Task<ActionResult> ExecuteAsync(
        OrderCard card, Card? target, int amount, Card? triggerCard = null, CancellationToken ct = default);
}
