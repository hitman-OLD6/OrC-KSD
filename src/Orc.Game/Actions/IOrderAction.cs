using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;

namespace Orc.Game.Actions;

/// <summary>
/// 指令施放动作（S13 第 6 条）：动作实体接口——**唯一动作入口**。
/// <para>
/// 实现内部只做动作层包装：调 <see cref="OrderFlow.ExecuteAsync(Card, IReadOnlyList{ICastAction}, Card?, int, CancellationToken)"/>
/// （唯一 flow），再把 flow 结果翻译为 <see cref="ActionResult"/>。禁止出现第二个指令编排单元。
/// </para>
/// <para>
/// 主动效果集合＝<c>card.Effects.OfType&lt;ICastAction&gt;()</c>（登记序、全部）——不取首个。
/// </para>
/// </summary>
public interface IOrderAction
{
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
