using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Actions;

namespace Orc.Foundation.Actions;

/// <summary>
/// 指令施放动作实现（S13 第 6 条；三选一方案 a＝动作层包装）：
/// 只把 <see cref="OrderFlow"/> 作为唯一 flow 执行一次，并把结果翻译为 <see cref="ActionResult"/>。
/// <para>
/// 结果语义：<c>Interrupted</c>（链级中断）＝<see cref="ActionOutcomeKind.Skipped"/> +
/// <see cref="ActionFailureKind.Cancelled"/>（牌留手、不扣费）；其余（含被反制 <c>Stop</c>）＝
/// <see cref="ActionOutcomeKind.Applied"/>——指令照常扣费/离手，只是效果不触发。
/// </para>
/// </summary>
public sealed class OrderAction : IOrderAction
{
    private readonly OrderFlow _flow;

    /// <summary>创建指令施放动作。</summary>
    /// <param name="flow">内核指令流程（引擎级单实例；<c>LogicEngine.OrderFlow</c>）。</param>
    /// <exception cref="ArgumentNullException">flow 为 null。</exception>
    public OrderAction(OrderFlow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        _flow = flow;
    }

    /// <inheritdoc />
    public async Task<ActionResult> ExecuteAsync(
        OrderCard card, Card? target, int amount, Card? triggerCard = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);

        // 登记序、全部主动效果（禁止 FirstOrDefault——多主动效果必须全跑）。
        var effects = card.Effects.OfType<ICastAction>().ToArray();

        var result = new ActionTargetResult(card.Ref, card, amount);
        var stream = await _flow.ExecuteAsync(card, effects, target, amount, ct).ConfigureAwait(false);

        if (stream.Interrupted)
        {
            result.MarkSkipped(ActionFailureKind.Cancelled); // 牌留手、不扣费（Interrupt 两阶段全停）
        }
        else
        {
            result.MarkApplied(0); // 施放完成（含被反制 Stop：照常扣费/离手，效果不触发）
        }

        return new ActionResult(ActionId.Order, result);
    }
}
