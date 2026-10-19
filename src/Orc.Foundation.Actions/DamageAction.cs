using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Damage;
using Orc.Game.Players;
using Orc.Game.Actions;

namespace Orc.Foundation.Actions;

/// <summary>
/// 伤害动作实体（S5a）：三个**触发端**（<c>combat.damage</c>/<c>effect.damage</c>/<c>battle.damage</c>）
/// ＋恰一个**处理端**（<c>damage.apply</c>）。
/// <para>
/// 职责边界：触发端只改 <see cref="DamageEventView.Amount"/> 或加条件，不得扣数值；
/// 处理端 <see cref="ActionHandleBands.Apply"/> 的默认 handler 是**唯一**调用
/// <see cref="IDamageActionServices.ApplyDamageAsync"/> 的地方（唯一数值门户调用点）。
/// </para>
/// <para>
/// 归属：对局级，由 <see cref="Damage.DamagePipeline"/> 构造并持有（与 <c>AttackDamageTrigger</c> 同层）——
/// 独立构造的 <c>CommandManager</c> 自带一份，<c>Match</c> 无需新增装配点。
/// </para>
/// </summary>
public sealed class DamageAction : IDamageAction
{
    private const int DefaultPriority = 100;

    private readonly LogicEngine _engine;
    private readonly IDamageActionServices _services;

    /// <summary>创建伤害动作（触发端默认转发/处理端默认落地在本类内注册）。</summary>
    /// <param name="engine">对局引擎（执行流挂载点/伤害信号发射）。</param>
    /// <param name="services">动作服务面（唯一落地口；实现由宿主提供）。</param>
    /// <exception cref="ArgumentNullException">engine / services 为 null。</exception>
    public DamageAction(LogicEngine engine, IDamageActionServices services)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(services);
        _engine = engine;
        _services = services;

        CombatDamage = new Trigger<DamageEventView>(
            "卡牌对战伤害触发器", bandType: typeof(ActionTriggerBands));
        EffectDamage = new Trigger<DamageEventView>(
            "效果伤害触发器", bandType: typeof(ActionTriggerBands));
        BattleDamage = new Trigger<DamageEventView>(
            "战斗伤害触发器", bandType: typeof(ActionTriggerBands));
        Apply = new Trigger<DamageApplyView>(
            "伤害处理触发器", bandType: typeof(ActionHandleBands));

        // 三触发端各恰注册一次 Commit 默认转发（S2 第 3 条）：读改写后的 Amount，原样交给处理端。
        CombatDamage.Register("转发到处理端", ForwardAsync, ActionTriggerBands.Commit, DefaultPriority);
        EffectDamage.Register("转发到处理端", ForwardAsync, ActionTriggerBands.Commit, DefaultPriority);
        BattleDamage.Register("转发到处理端", ForwardAsync, ActionTriggerBands.Commit, DefaultPriority);

        // 处理端默认落地（S2：Apply 段 priority 100）——唯一调用动作服务面处。
        Apply.Register("扣除数值", HandleApplyAsync, ActionHandleBands.Apply, DefaultPriority);
    }

    /// <summary>卡牌对战伤害触发端（<c>combat.damage</c>；重甲只挂这里）。</summary>
    public Trigger<DamageEventView> CombatDamage { get; }

    /// <summary>效果伤害触发端（<c>effect.damage</c>）。</summary>
    public Trigger<DamageEventView> EffectDamage { get; }

    /// <summary>战斗伤害触发端（<c>battle.damage</c>）。</summary>
    public Trigger<DamageEventView> BattleDamage { get; }

    /// <summary>伤害处理端（<c>damage.apply</c>；唯一调用数值门户）。</summary>
    public Trigger<DamageApplyView> Apply { get; }

    /// <inheritdoc />
    public Task<ActionResult> CombatAsync(
        Card? source, Card target, int amount, Card? triggerCard = null, CancellationToken ct = default)
        => RunAsync(CombatDamage, DamageKinds.Combat, source, target, amount, triggerCard, ct);

    /// <inheritdoc />
    public Task<ActionResult> EffectAsync(
        Card? source, Card target, int amount, Card? triggerCard = null, CancellationToken ct = default)
        => RunAsync(EffectDamage, DamageKinds.Effect, source, target, amount, triggerCard, ct);

    /// <inheritdoc />
    public Task<ActionResult> BattleAsync(
        Card? source, Card target, int amount, Card? triggerCard = null, CancellationToken ct = default)
        => RunAsync(BattleDamage, DamageKinds.Battle, source, target, amount, triggerCard, ct);

    /// <summary>转发到处理端（触发端默认 handler 的公共体；供三端复用）。</summary>
    private Task ForwardAsync(DamageEventView view, Context ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(view);
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [nameof(DamageApplyView.Source)] = view.Source,
            [nameof(DamageApplyView.Target)] = view.Target,
            [nameof(DamageApplyView.Amount)] = view.Amount,
            [nameof(DamageApplyView.Kind)] = view.Kind,
            [nameof(DamageApplyView.IsZeroed)] = false,
            [nameof(DamageApplyView.Result)] = view.Result,
        };
        return Apply.InvokeAsync(_engine, data, ct);
    }

    /// <summary>处理端默认执行体（读结果位后落地——**唯一调用动作服务面处**）。</summary>
    private async Task HandleApplyAsync(DamageApplyView view, Context ctx, CancellationToken ct)
    {
        var amount = view.IsZeroed ? 0 : view.Amount;
        var dealt = await _services.ApplyDamageAsync(view.Source, view.Target, amount, view.Kind, ct)
            .ConfigureAwait(false);
        view.Result?.MarkApplied(dealt);
    }

    private async Task<ActionResult> RunAsync(
        Trigger<DamageEventView> trigger,
        string kind,
        Card? source,
        Card target,
        int amount,
        Card? triggerCard,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        if (!DamageKinds.IsKnown(kind))
        {
            throw new ArgumentException($"未知伤害种类 '{kind}'（只接受 combat/effect/battle）。", nameof(kind));
        }

        var result = new ActionTargetResult(target.Ref, target as CardBase, amount);
        if (target is not (UnitCard or Hq))
        {
            result.MarkTargetInvalid();
            return new ActionResult(ActionId.Damage, result); // 非单位非 HQ：无伤害语义（零副作用）
        }

        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [nameof(DamageEventView.Source)] = source,
            [nameof(DamageEventView.Target)] = target,
            [nameof(DamageEventView.Amount)] = amount,
            [nameof(DamageEventView.Kind)] = kind,
            [nameof(DamageEventView.Result)] = result,
        };

        // 触发者卡牌显式传递（S10）：不依赖「应该会继承」；null 时不写键（帧继承保持既有语义）。
        if (triggerCard is not null)
        {
            data[TriggerPayloadKeys.TriggerCard] = triggerCard;
        }

        await trigger.InvokeAsync(_engine, data, ct).ConfigureAwait(false);
        return new ActionResult(ActionId.Damage, result);
    }
}
