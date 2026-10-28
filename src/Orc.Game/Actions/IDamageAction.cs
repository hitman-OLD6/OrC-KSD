using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Damage;

namespace Orc.Game.Actions;

/// <summary>
/// 伤害动作（S5a 样板；S10 动作接口）：三个**触发端**（按来源分）＋恰一个**处理端**。
/// <para>
/// 触发端只改写 <c>Amount</c> 或加条件，不得扣数值；处理端只经
/// <see cref="IDamageActionServices.ApplyDamageAsync"/> 落地（唯一数值门户调用点）。
/// </para>
/// <para>
/// **两端是引擎成员（S13／U3）**：公开面只给成员地址；旧身份实例随 <see cref="Ends"/> 交给
/// <see cref="DamagePipeline"/> 与宿主装配（重甲／免疫的 band 挂接点经门面按地址取回同一份实例），
/// 随旧类型在 U5 退役。
/// </para>
/// </summary>
public interface IDamageAction
{
    /// <summary>卡牌对战伤害**触发端**的成员地址（<c>engine#combat-damage</c>；重甲只挂这里）。</summary>
    MemberAddress CombatDamage { get; }

    /// <summary>效果伤害**触发端**的成员地址（<c>engine#effect-damage</c>）。</summary>
    MemberAddress EffectDamage { get; }

    /// <summary>战斗伤害**触发端**的成员地址（<c>engine#battle-damage</c>）。</summary>
    MemberAddress BattleDamage { get; }

    /// <summary>伤害**处理端**的成员地址（<c>engine#damage-apply</c>；唯一调用数值门户）。</summary>
    MemberAddress Apply { get; }

    /// <summary>四端登记项（三触发端＋处理端）。</summary>
    IReadOnlyList<ActionTriggerEnd> Ends { get; }

    /// <summary>卡牌对战伤害（<c>combat.damage</c>；重甲只减这一类）。</summary>
    Task<ActionResult> CombatAsync(
        Card? source, Card target, int amount, Card? triggerCard = null, CancellationToken ct = default);

    /// <summary>效果伤害（<c>effect.damage</c>）。</summary>
    Task<ActionResult> EffectAsync(
        Card? source, Card target, int amount, Card? triggerCard = null, CancellationToken ct = default);

    /// <summary>战斗伤害（<c>battle.damage</c>；无射程/守护/伏击/反击资格）。</summary>
    Task<ActionResult> BattleAsync(
        Card? source, Card target, int amount, Card? triggerCard = null, CancellationToken ct = default);
}

/// <summary>
/// 伤害结算的**动作服务面**（S4）：处理端 handler 访问对局的受控写面——具名操作、不交出管理器实例。
/// <para>
/// 唯一调用者＝伤害动作默认处理端 handler（S4 对照表）。信号发射权按动作归属：本接口只发
/// <c>unit.damage.dealt</c>（伤害动作的语义产物），不得提供 <c>unit.acted</c> 等其他动作/行动信号。
/// </para>
/// </summary>
public interface IDamageActionServices
{
    /// <summary>
    /// 落地一次伤害：读改写后的量 → 数值门户扣减 → 实际扣减 &gt; 0 才发 <c>unit.damage.dealt</c>。
    /// 已死亡单位 / 非单位非 HQ → 返回 0、不抛（战斗同时结算需要「先死者不再扣」）。
    /// </summary>
    /// <param name="source">施动方（可为 null＝无归属）。</param>
    /// <param name="target">受方（单位或 HQ）。</param>
    /// <param name="amount">已过触发端改写的伤害量（≤0 ＝不扣）。</param>
    /// <param name="kind">伤害种类（<c>Orc.Game.Damage.DamageKinds</c> 三值之一；诊断/扩展用）。</param>
    /// <param name="ct">取消。</param>
    /// <returns>实际扣减量（单位＝防御减少量；HQ＝生命减少量；0＝无变化）。</returns>
    Task<int> ApplyDamageAsync(
        Card? source, Card target, int amount, string kind, CancellationToken ct = default);
}
