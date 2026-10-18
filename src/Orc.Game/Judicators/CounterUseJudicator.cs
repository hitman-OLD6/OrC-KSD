using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Players;

namespace Orc.Game.Judicators;

/// <summary>
/// 反制使用检查判定器（J2；反制使用验证点专属——逐点专属判定器之一）：
/// 触发器验证（使用反制的触发器）与入口预检（打出管理器前置判定）的单一判定源。
/// 规则：①仅己方回合（该卡所属玩家是当前行动方；回合上下文经卡上 <see cref="CardBase.TurnPlayerProvider"/> 读取）；
/// ②未激活时另检查指挥点（已激活＝取消流程、无条件通过）。
/// 输出契约：不合法时携带拒绝类别（<see cref="CounterUseRejection"/>——两类可辨识；入口映射按取数语义消费）；
/// 类别逻辑＝卡侧只读逻辑 <see cref="CounterCard.EvaluateUse"/>（判定器调用链内的实现细节——入口侧不保留独立类别逻辑）。
/// 无状态：零装配期注入（全部经被判定对象只读读取）；改写＝moding（反制验证改写——触发器验证与入口预检同步生效）。
/// </summary>
internal sealed class CounterUseJudicator : ValidationJudicator
{
    private readonly Func<Player, bool>? _turnOwner;

    /// <summary>创建判定器。</summary>
    /// <param name="turnOwner">回合归属判定通道（R4；缺省＝null＝按卡上 TurnPlayerProvider 引用比较）。</param>
    internal CounterUseJudicator(Func<Player, bool>? turnOwner = null) => _turnOwner = turnOwner;

    /// <inheritdoc />
    protected override ValidationVerdict ValidateLogic(IReadOnlyList<Ref<Entity>> refs, Ref<Entity>? subject)
    {
        if (subject is null || !subject.IsAlive || subject.Value is not CounterCard card)
        {
            // 载体缺失/不符＝契约不符（fail-fast 族；运行期由触发路径契约兜底承接）——绑定方固定提供所属卡引用，运行期不可达。
            throw new ArgumentException("反制使用检查判定器载荷不符：被判定对象须为存活反制卡引用（fail-fast）。");
        }

        var owner = card.Owner;
        if (owner is null)
        {
            return ValidationVerdict.Invalid(CounterUseRejection.NotOwnerTurn);
        }

        // R4：回合归属经判定器同源判定；否则保持卡上提供器路径（独立构造兼容）。
        if (_turnOwner is not null && !_turnOwner(owner))
        {
            return ValidationVerdict.Invalid(CounterUseRejection.NotOwnerTurn);
        }

        var rejection = _turnOwner is not null
            ? card.EvaluateUse(owner) // 回合已判定＝以 owner 作为当前行动方传给卡侧（保留费用检查）
            : card.EvaluateUse(card.TurnPlayerProvider?.Invoke());
        return rejection is null ? ValidationVerdict.Valid : ValidationVerdict.Invalid(rejection);
    }
}
