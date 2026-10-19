using Orc.Cards;
using Orc.Core;
using Orc.Game.Actions;

namespace Orc.Game.Damage;

/// <summary>
/// 伤害三层管道（R11）的**门面**：三条**触发端**（按来源分）＋ 一条**处理端**（唯一扣数值）。
/// <para>
/// S5a 起：动作实体迁入伤害动作实现（P5b 起位于 <c>orc.foundation.actions</c>），本类只保留既有 <c>bool</c> 契约与触发器访问面
/// （调用方零改动）；不再持有第二份扣数值实体。
/// </para>
/// <para>
/// 职责边界：触发端只改 <see cref="DamageEventView.Amount"/> 或加条件，不得扣数值；
/// 处理端 <c>Apply</c> 的默认 handler 是唯一调用 <see cref="IDamageActionServices.ApplyDamageAsync"/> 的地方。
/// </para>
/// <para>
/// 归属：对局级，由 <c>CommandManager</c> 构造并持有（与 <c>AttackDamageTrigger</c> 同层）——
/// 独立构造的 <c>CommandManager</c> 自带一份，<c>Match</c> 无需新增装配点。
/// </para>
/// </summary>
public sealed class DamagePipeline
{
    private readonly IDamageAction _action;

    /// <summary>
    /// 以目录装配的伤害动作构造（S15/P5b：管道只做门面，不再持有实现类型与目录本体；
    /// 唯一构造者为 <c>CommandManager</c>）。
    /// </summary>
    internal DamagePipeline(IDamageAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _action = action;
    }

    /// <summary>伤害动作实体（S11：攻击动作经构造注入本接口——互伤只经伤害动作）。</summary>
    public IDamageAction Action => _action;

    /// <summary>卡牌对战伤害触发端（<c>combat.damage</c>；重甲只挂这里）。</summary>
    public Trigger<DamageEventView> CombatDamage => _action.CombatDamage;

    /// <summary>效果伤害触发端（<c>effect.damage</c>）。</summary>
    public Trigger<DamageEventView> EffectDamage => _action.EffectDamage;

    /// <summary>战斗伤害触发端（<c>battle.damage</c>）。</summary>
    public Trigger<DamageEventView> BattleDamage => _action.BattleDamage;

    /// <summary>伤害处理端（<c>damage.apply</c>；唯一扣数值）。</summary>
    public Trigger<DamageApplyView> Apply => _action.Apply;

    /// <summary>对战伤害：<paramref name="source"/> 打 <paramref name="target"/>，走 <c>combat.damage</c> → <c>damage.apply</c>。</summary>
    /// <returns><c>true</c> ＝处理端已执行（含免疫归零）；<c>false</c> ＝不支持的目标/被中断。</returns>
    /// <exception cref="ArgumentNullException">target 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">amount 为负。</exception>
    public async Task<bool> CombatAsync(Card? source, Card target, int amount, CancellationToken ct = default)
        => IsApplied(await _action.CombatAsync(source, target, amount, triggerCard: null, ct).ConfigureAwait(false));

    /// <summary>效果伤害：走 <c>effect.damage</c> → <c>damage.apply</c>。</summary>
    /// <inheritdoc cref="CombatAsync"/>
    public async Task<bool> EffectAsync(Card? source, Card target, int amount, CancellationToken ct = default)
        => IsApplied(await _action.EffectAsync(source, target, amount, triggerCard: null, ct).ConfigureAwait(false));

    /// <summary>战斗伤害：走 <c>battle.damage</c> → <c>damage.apply</c>。</summary>
    /// <inheritdoc cref="CombatAsync"/>
    public async Task<bool> BattleAsync(Card? source, Card target, int amount, CancellationToken ct = default)
        => IsApplied(await _action.BattleAsync(source, target, amount, triggerCard: null, ct).ConfigureAwait(false));

    /// <summary>门面折算：处理端已执行＝<see cref="ActionOutcomeKind.Applied"/>（与既有 <c>DamageSettlement.Applied</c> 同义）。</summary>
    private static bool IsApplied(ActionResult result)
        => result.Targets.Count > 0 && result.Targets[0].Outcome == ActionOutcomeKind.Applied;
}
