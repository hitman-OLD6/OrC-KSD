using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Cards;
using Orc.Game.Commanding;

namespace Orc.Game.Actions;

/// <summary>
/// 攻击动作（S11；S10 动作接口）：<c>unit.attack</c> **触发端**（验证 + 三段 band + <c>Commit</c> 转发）
/// ＋**攻击处理端**（<c>Rewrite</c>/<c>Veto</c>/<c>Apply</c>）。
/// <para>
/// 依赖图（S10）：攻击 → 伤害（构造注入 <see cref="IDamageAction"/>）；**不注入** <see cref="IDeathAction"/>——
/// 普通死亡由 <c>card.stat.changed</c> 被动衔接；伏击改写由伏击效果自己走完整死亡两端。
/// </para>
/// </summary>
public interface IAttackAction
{
    /// <summary>攻击**触发端**（<c>engine#unit-attack</c>）。</summary>
    MemberAddress Trigger { get; }

    /// <summary>攻击**处理端**（<c>engine#attack-damage</c>）。</summary>
    MemberAddress Handle { get; }

    /// <summary>构造注入的伤害动作（互伤的唯一结算通道；替换实现后仍可读）。</summary>
    IDamageAction Damage { get; }

    /// <summary>指挥入口发起的攻击（无射程绕过）。</summary>
    Task<ActionResult> CommandAsync(
        UnitCard attacker, Ref<Entity> target, Card? triggerCard = null, CancellationToken ct = default);

    /// <summary>效果发起的攻击（<paramref name="bypassRange"/>＝效果改写的射程判定；零行动经济面）。</summary>
    Task<ActionResult> EffectAsync(
        UnitCard attacker, Ref<Entity> target, bool bypassRange = false,
        Card? triggerCard = null, CancellationToken ct = default);
}
