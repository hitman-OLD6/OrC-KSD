using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Cards;
using Orc.Game.Model;
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
/// <para>
/// **U3**：两端升为引擎成员（<c>engine#order.cast</c>／<c>engine#order.finalize</c>）。实例仍由内核唯一 flow 持有
/// （阶段 1／阶段 2 两个触发器），本类只负责**自报地址＋端清单**供目录装配——与其它 8 个动作同一形态，
/// 因此动作目录不再有"两端无地址"的特例。
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

        Cast = MemberAddress.Create(AddressScope.Engine, EngineProcesses.OrderCast);
        Finalize = MemberAddress.Create(AddressScope.Engine, EngineProcesses.OrderFinalize);
        Ends = new[]
        {
            new ActionTriggerEnd("触发端", Cast),
            new ActionTriggerEnd(ActionTriggerEnd.HandleRole, Finalize),
        };
    }

    /// <inheritdoc />
    public MemberAddress Cast { get; }

    /// <inheritdoc />
    public MemberAddress Finalize { get; }

    /// <inheritdoc />
    public IReadOnlyList<ActionTriggerEnd> Ends { get; }

    /// <inheritdoc />
    public async Task<ActionResult> ExecuteAsync(
        OrderCard card, Card? target, int amount, Card? triggerCard = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);

        // v4 收口的施放语义：主动效果**不再是"指令实例集"**——它就是卡实体上的组件边
        // （施放结算在 <c>engine#order.cast</c> 的宣告点发 <c>card.played</c>，订阅该信号的组件边随之执行；
        // 卡上没有组件＝没有主动效果，这是卡的声明问题，不是流程的缺口）。
        // 因此这里显式传"无旧面指令"（旧 `CardCastActions.Of` 空壳已删除）：两阶段流程
        // （反制检查 → 宣告 → 结算 → 收尾）照常走完，结算段只剩声明序上的组件/外来边。
        var result = new ActionTargetResult(card.Ref, card, amount);
        var interrupted = await _flow.ExecuteAsync(card, (ICastAction?)null, target, amount, ct).ConfigureAwait(false);

        if (interrupted)
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
