using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Cards;
using Orc.Game.Damage;
using Orc.Game.Model;
using Orc.Game.Players;
using Orc.Game.Actions;

namespace Orc.Foundation.Actions;

/// <summary>
/// 伤害动作实体（S5a）：三个**触发端**（<c>combat.damage</c>/<c>effect.damage</c>/<c>battle.damage</c>）
/// ＋恰一个**处理端**（<c>damage.apply</c>）。
/// <para>
/// 职责边界：触发端只改 <see cref="DamageEventView.Amount"/> 或加条件，不得扣数值；
/// 处理端默认 handler（**自有边**，外来改写挂在其前）是**唯一**调用
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

        // U3：四端＝引擎成员（地址由契约自报）；实例＝过渡期内部执行面（管道门面按地址取回）。
        CombatDamage = MemberAddress.Create(AddressScope.Engine, EngineProcesses.CombatDamage);
        EffectDamage = MemberAddress.Create(AddressScope.Engine, EngineProcesses.EffectDamage);
        BattleDamage = MemberAddress.Create(AddressScope.Engine, EngineProcesses.BattleDamage);
        Apply = MemberAddress.Create(AddressScope.Engine, EngineProcesses.DamageApply);


        // 三触发端各恰一条自有边（默认转发，S2 第 3 条）：读改写后的 Amount，原样交给处理端。
        ActionEdges.Attach<DamageEventView>(_engine, EngineProcesses.CombatDamage, ForwardAsync);
        ActionEdges.Attach<DamageEventView>(_engine, EngineProcesses.EffectDamage, ForwardAsync);
        ActionEdges.Attach<DamageEventView>(_engine, EngineProcesses.BattleDamage, ForwardAsync);

        // 处理端自有边（唯一调用动作服务面处）——外来改写（重甲／免疫）挂在其前。
        ActionEdges.Attach<DamageApplyView>(_engine, EngineProcesses.DamageApply, HandleApplyAsync);

        // v4-only 收口：每个端**恰一条**自有边（该端默认流程，见上）。曾经的第二次 AttachMemberEnd 与
        // 上面的 ActionEdges.Attach 落在同一成员同一自有边段——同一次伤害被转发/落地两遍（数值翻倍）。

        Ends = new[]
        {
            new ActionTriggerEnd("对战伤害触发端", CombatDamage),
            new ActionTriggerEnd("效果伤害触发端", EffectDamage),
            new ActionTriggerEnd("战斗伤害触发端", BattleDamage),
            new ActionTriggerEnd(ActionTriggerEnd.HandleRole, Apply),
        };
    }

    /// <inheritdoc />
    public MemberAddress CombatDamage { get; }

    /// <inheritdoc />
    public MemberAddress EffectDamage { get; }

    /// <inheritdoc />
    public MemberAddress BattleDamage { get; }

    /// <inheritdoc />
    public MemberAddress Apply { get; }

    /// <inheritdoc />
    public IReadOnlyList<ActionTriggerEnd> Ends { get; }

    /// <inheritdoc />
    public Task<ActionResult> CombatAsync(
        Card? source, Card target, int amount, Card? triggerCard = null, CancellationToken ct = default)
        => RunAsync(EngineProcesses.CombatDamage, DamageKinds.Combat, source, target, amount, triggerCard, ct);

    /// <inheritdoc />
    public Task<ActionResult> EffectAsync(
        Card? source, Card target, int amount, Card? triggerCard = null, CancellationToken ct = default)
        => RunAsync(EngineProcesses.EffectDamage, DamageKinds.Effect, source, target, amount, triggerCard, ct);

    /// <inheritdoc />
    public Task<ActionResult> BattleAsync(
        Card? source, Card target, int amount, Card? triggerCard = null, CancellationToken ct = default)
        => RunAsync(EngineProcesses.BattleDamage, DamageKinds.Battle, source, target, amount, triggerCard, ct);

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
        // b4 收尾：处理端＝引擎成员 `damage-apply`（免疫／封顶等组件包装挂在这条成员上）。
        // 数据面仍是**新的一份**（与旧行为同：触发端与处理端各有自己的数据面，改写不跨端回写）。
        return ForwardToApplyAsync(data, ct);
    }

    /// <summary>转发到处理端（成员优先；成员面不可用＝回落旧触发器，绝不静默丢伤害）。</summary>
    private async Task ForwardToApplyAsync(IDictionary<string, object?> data, CancellationToken ct)
    {
        if (!await ModelMemberDispatch
                .InvokeEngineMemberAsync(_engine, EngineProcesses.DamageApply, new Context(data), ct)
                .ConfigureAwait(false))
        {
            await _engine.InvokeMemberAsync(EngineProcesses.DamageApply, new Context(data), ct).ConfigureAwait(false);
        }
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
        string member,
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

        // 该端＝引擎成员（组件包装／挂载边在这条成员上，自有边＝转发）。§27：不再有第二条旧触发器路径。
        await _engine.InvokeMemberAsync(member, new Context(data), ct).ConfigureAwait(false);

        return new ActionResult(ActionId.Damage, result);
    }
}
