using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Players;
using Orc.Game.Actions;

namespace Orc.Foundation.Actions;

/// <summary>
/// 攻击动作实体（S11）：<c>UnitAttackTrigger</c>＝**触发端**（<see cref="ActionTriggerBands"/>），
/// <c>AttackDamageTrigger</c>＝**处理端**（<see cref="ActionHandleBands"/>：<c>Rewrite</c>/<c>Veto</c>/<c>Apply</c>）。
/// <para>
/// <b>执行归属</b>：揭示、<c>KeywordProgramFacade.AttackEvent(before/after)</c>、<c>KeywordRules.RecordAttack</c>、
/// 冲击消耗、HQ／单位目标分支、互伤、<c>unit.combat.survived</c> 全部在**处理侧**执行；
/// <c>unit.acted</c>／扣费／清位仍在指挥入口（S5）。
/// </para>
/// <para>
/// <b>顺序保持</b>：揭示与 <c>AttackEvent(before)</c> 必须在处理端 <c>Rewrite</c> 段（伏击改写）之前执行，
/// 而 <c>Rewrite</c> 是 band 内的最前段——故这两步由处理侧执行体 <see cref="SettleAsync"/> 在处理端触发器之前完成，
/// 而非作为 <c>Rewrite</c> 段的事件（否则伏击的死亡会先于揭示，改变既有观察序）。
/// </para>
/// <para>
/// <b>依赖</b>：构造注入 <see cref="IDamageAction"/>（互伤只经伤害动作）；不注入 <see cref="IDeathAction"/>。
/// </para>
/// </summary>
public sealed class AttackAction : IAttackAction
{
    private const int DefaultPriority = 100;

    private readonly LogicEngine _engine;
    private readonly Trigger<UnitAttackTriggerView> _trigger;
    private readonly Trigger<AttackDamageTriggerView> _handle;
    private readonly IDamageAction _damage;
    private readonly Func<UnitCard, Ref<Entity>, bool> _targetLegal;
    private readonly Func<UnitCard, UnitCard, bool> _counterEligibility;

    /// <summary>创建攻击动作（两端默认链在本类内注册；触发器由对局提供——保留既有公开名与地址）。</summary>
    /// <param name="engine">对局引擎（执行流挂载点／信号发射）。</param>
    /// <param name="trigger">攻击**触发端**（<c>UnitAttackTrigger</c>；band 方案＝<see cref="ActionTriggerBands"/>）。</param>
    /// <param name="handle">攻击**处理端**（<c>AttackDamageTrigger</c>；band 方案＝<see cref="ActionHandleBands"/>）。</param>
    /// <param name="damage">伤害动作（互伤的唯一结算通道）。</param>
    /// <param name="targetLegal">目标合法性判定（效果攻击的射程/守护判定；在效果来源作用域内调用）。</param>
    /// <param name="counterEligibility">反击资格判定通道（默认互伤区）。</param>
    /// <exception cref="ArgumentNullException">任一参数为 null。</exception>
    public AttackAction(
        LogicEngine engine,
        Trigger<UnitAttackTriggerView> trigger,
        Trigger<AttackDamageTriggerView> handle,
        IDamageAction damage,
        Func<UnitCard, Ref<Entity>, bool> targetLegal,
        Func<UnitCard, UnitCard, bool> counterEligibility)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(damage);
        ArgumentNullException.ThrowIfNull(targetLegal);
        ArgumentNullException.ThrowIfNull(counterEligibility);

        _engine = engine;
        _trigger = trigger;
        _handle = handle;
        _damage = damage;
        _targetLegal = targetLegal;
        _counterEligibility = counterEligibility;

        // 触发端：发起前校验 → 转发到处理端（Commit 恰注册一次，priority 100）。
        _trigger.Register("攻击前置校验", ValidateAsync, ActionTriggerBands.Initiate, DefaultPriority);
        _trigger.Register("转发到处理端", SettleAsync, ActionTriggerBands.Commit, DefaultPriority);

        // 处理端：默认攻击结算（唯一落地段）。
        _handle.Register("默认攻击结算", ApplyAsync, ActionHandleBands.Apply, DefaultPriority);
    }

    /// <summary>攻击**触发端**（与 <c>CommandManager.UnitAttackTrigger</c> 同一实例）。</summary>
    public Trigger<UnitAttackTriggerView> Trigger => _trigger;

    /// <summary>攻击**处理端**（与 <c>CommandManager.AttackDamageTrigger</c> 同一实例）。</summary>
    public Trigger<AttackDamageTriggerView> Handle => _handle;

    /// <summary>伤害动作（互伤通道；诊断可读）。</summary>
    public IDamageAction Damage => _damage;

    /// <inheritdoc />
    public async Task<ActionResult> CommandAsync(
        UnitCard attacker, Ref<Entity> target, Card? triggerCard = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(target);
        return await InvokeAsync(
            attacker, target, triggerCard, new ActionTargetResult(target, target.Value as CardBase, 1), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ActionResult> EffectAsync(
        UnitCard attacker, Ref<Entity> target, bool bypassRange = false,
        Card? triggerCard = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(target);

        var result = new ActionTargetResult(target, target.Value as CardBase, 1);

        // 效果来源作用域：射程绕过与「效果攻击」标记的读取点均在触发端校验/处理端内。
        using var scope = KeywordProgramFacade.BeginEffectAttack(attacker, bypassRange);
        if (!_targetLegal(attacker, target))
        {
            result.MarkSkipped(ActionFailureKind.Precondition); // 目标不合法（射程/守护）：未发起、零副作用
            return new ActionResult(ActionId.Attack, result);
        }

        return await InvokeAsync(attacker, target, triggerCard, result, ct).ConfigureAwait(false);
    }

    private async Task<ActionResult> InvokeAsync(
        UnitCard attacker, Ref<Entity> target, Card? triggerCard, ActionTargetResult result, CancellationToken ct)
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [CommandDataKeys.Attacker] = attacker.Ref,
            [CommandDataKeys.Target] = target,
            [nameof(UnitAttackTriggerView.Result)] = result,
        };

        // 触发者卡牌显式传递（S10）：不依赖「应该会继承」；null 时不写键（帧继承保持既有语义）。
        if (triggerCard is not null)
        {
            data[CommandDataKeys.TriggerCard] = triggerCard;
        }

        var stream = await _trigger.InvokeAsync(_engine, data, ct).ConfigureAwait(false);
        if (stream.Outcome == ExecutionOutcome.ValidationRejected)
        {
            result.MarkSkipped(ActionFailureKind.ValidationRejected);
        }
        else if (stream.Outcome == ExecutionOutcome.ContractFailure)
        {
            result.MarkSkipped(ActionFailureKind.Cancelled);
        }

        return new ActionResult(ActionId.Attack, result);
    }

    /// <summary>发起前校验（触发端 <c>Initiate</c>）：载荷结构错误＝本次动作不发起（零副作用）。</summary>
    private static Task ValidateAsync(UnitAttackTriggerView view, Context ctx, CancellationToken ct)
    {
        if (view.Attacker is not { IsAlive: true } attackerRef || attackerRef.Value is not UnitCard
            || view.Target is not { IsAlive: true })
        {
            view.Result?.MarkTargetInvalid();
            ctx.Interrupt();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 转发到处理端（触发端 <c>Commit</c>）：处理侧执行体——<c>AttackEvent(before)</c> → 揭示 →
    /// 处理端触发器（<c>Rewrite</c>/<c>Veto</c>/<c>Apply</c>）→ <c>AttackEvent(after)</c> → 记账 → 冲击消耗。
    /// </summary>
    private async Task SettleAsync(UnitAttackTriggerView view, Context ctx, CancellationToken ct)
    {
        if (view.Attacker is not { IsAlive: true } attackerRef || attackerRef.Value is not UnitCard attacker
            || view.Target is not { IsAlive: true } targetRef)
        {
            ctx.Interrupt();
            return;
        }

        var targetEntity = targetRef.Value;
        var combatCompleted = false;
        try
        {
            await KeywordProgramFacade.AttackEvent(attacker, targetEntity as Card, true, ct);

            // S2（隐蔽机制·揭示触发点）：攻击执行段入口（复验后、伤害结算前）——
            // 参与攻击的隐蔽单位各自揭示：先攻击者（动作发起方）、后被攻击者（单位目标）；
            // HQ 不作被揭示方（攻击 HQ 时仅攻击者揭示）。
            await CovertRules.RevealAsync(attacker, ct);
            if (targetEntity is UnitCard targetUnit)
            {
                await CovertRules.RevealAsync(targetUnit, ct);
            }

            // 目标类型合法性（结构兜底）：只接受敌方 HQ 或单位；其余＝链中止、零结算。
            // HQ／单位的目标分支本身在**处理端**（唯一落地段），此处不做分支结算。
            if (targetEntity is not UnitCard && targetEntity is not Hq)
            {
                ctx.Interrupt();
                return;
            }

            if (targetEntity is Hq ownHq && ReferenceEquals(ownHq.Owner, attacker.Owner))
            {
                ctx.Interrupt(); // 己方 HQ：非法目标
                return;
            }

            var data = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [CommandDataKeys.Attacker] = attackerRef,
                [CommandDataKeys.Target] = targetRef,
                [CommandDataKeys.Resolution] = new AttackDamageResolution(),
                [nameof(AttackDamageTriggerView.Result)] = view.Result,
            };
            if (view.TriggerCard is not null)
            {
                data[CommandDataKeys.TriggerCard] = view.TriggerCard;
            }

            await _handle.InvokeAsync(_engine, data, ct);
            combatCompleted = true;
        }
        finally
        {
            try
            {
                if (combatCompleted)
                {
                    await KeywordProgramFacade.AttackEvent(attacker, targetEntity as Card, false, ct);
                }
                else
                {
                    foreach (var component in KeywordProgramRuntime.Components(attacker))
                    {
                        await KeywordProgramFacade.ClearTemporary(component, ct);
                    }
                }
            }
            finally
            {
                if (KeywordProgramFacade.TakeTemporary(attacker))
                {
                    await GameUpdates.EmitKeywordAttackFinished(_engine, attacker, CancellationToken.None);
                }
            }
        }

        // 执行段记账（词条侧）：奋战——本轮已攻次数 +1（先执行（含记账）→ 后更新）。
        KeywordRules.RecordAttack(attacker);

        // 冲击消耗（攻击即消耗）：到达执行段公共尾部即消耗——与对象类型（单位/HQ/伏击者）、
        // 反击/伏击是否起效均无关（「到达尾部」为唯一判据）；存活防御：攻击者已死亡＝跳过。
        if (KeywordRules.HasKeyword(attacker, KeywordIds.Shock)
            && !attacker.GetData<UnitStateData>().IsDestroyed)
        {
            await attacker.Keywords.RevokeAsync(KeywordIds.Shock);
        }
    }

    /// <summary>
    /// 默认攻击结算（处理端 <c>Apply</c>；唯一落地段）：
    /// <list type="number">
    /// <item><c>Handled=true &amp;&amp; IsRewritten=true</c>：改写方已完整处置（伏击走死亡两端）——跳过伤害，
    /// 双方按存活发 <c>unit.combat.survived</c>（免死时攻击者存活 → 另发一条）。</item>
    /// <item><c>Handled=true &amp;&amp; IsRewritten=false</c>：防御——写 Error 留痕并回退正常伤害（禁止静默吞伤害）。</item>
    /// <item>HQ 目标：伤害经伤害动作；不反击、不发 <c>unit.combat.survived</c>。</item>
    /// <item>单位目标：互伤（攻击力为互扣前快照）→ 死亡由 <c>card.stat.changed</c> 被动衔接 →
    /// 双方按存活发 <c>unit.combat.survived</c>（被攻击者在前、攻击者在后）。</item>
    /// </list>
    /// </summary>
    private async Task ApplyAsync(AttackDamageTriggerView view, Context ctx, CancellationToken ct)
    {
        if (view.Attacker is not { IsAlive: true } attackerRef || attackerRef.Value is not UnitCard attacker
            || view.Target is not { IsAlive: true } targetRef
            || view.Resolution is not AttackDamageResolution resolution)
        {
            ctx.Interrupt(); // 载荷缺失（结构性错误）：本次结算不发生
            return;
        }

        var targetEntity = targetRef.Value;

        if (resolution.Handled)
        {
            if (resolution.IsRewritten)
            {
                // 伏击改写：改写方已完整处置（含死亡两端）——攻击者不造成伤害、被攻击者不受伤。
                // 存活性由信号自身判定：已阵亡者零发射（免死＝攻击者存活 ⇒ 另发攻击者幸存）。
                if (targetEntity is UnitCard survivedTarget)
                {
                    await EmitCombatSurvivedAsync(_engine, survivedTarget, ct);
                }

                await EmitCombatSurvivedAsync(_engine, attacker, ct);
                view.Result?.MarkApplied(1);
                return;
            }

            // 防御：置了 Handled 却没处置——留痕后回退正常伤害（不抛、不中断）。
            _engine.RootStream.WriteLog(
                "造成攻击伤害触发器",
                $"攻击结算被标记为已处置（Handled）但未改写（IsRewritten=false）——回退正常伤害。"
                + $"攻击者='{attacker.Name}' 目标='{targetEntity?.GetType().Name}'。",
                LogLevel.Error,
                new[] { "attack:handled-without-rewrite" });
        }

        // 死亡冻结：任一方已死亡＝尸体不参与数值结算（真实对局不可达——候选过滤；防御于测试/异常驱动路径）。
        if (attacker.GetData<UnitStateData>().IsDestroyed
            || (targetEntity is UnitCard frozen && frozen.GetData<UnitStateData>().IsDestroyed))
        {
            return;
        }

        if (targetEntity is Hq hq && !ReferenceEquals(hq.Owner, attacker.Owner))
        {
            // HQ 简路：伤害＝攻击者攻击力有效值；不反击、不走单位互伤；归零→终局由 HQ 侧统一响应。
            var damage = Math.Max(0, attacker.Modifiers.GetEffectiveValue(CardStatFields.Attack));
            await _damage.CombatAsync(attacker, hq, damage, view.TriggerCard, ct);
            view.Result?.MarkApplied(1);
            return;
        }

        if (targetEntity is not UnitCard target)
        {
            ctx.Interrupt();
            return;
        }

        // 默认基础互伤：按反击豁免判定表（同时结算、以互扣前有效值为基准；豁免方不结算反击伤害）。
        // 两条伤害各自经「对战伤害」触发端 → 处理端；重甲在 combat.damage 改写、免疫在 damage.apply 归零。
        var damageToTarget = Math.Max(0, attacker.Modifiers.GetEffectiveValue(CardStatFields.Attack));
        var damageToAttacker = Math.Max(0, target.Modifiers.GetEffectiveValue(CardStatFields.Attack));
        var counterAttacks = _counterEligibility(attacker, target);

        await _damage.CombatAsync(attacker, target, damageToTarget, view.TriggerCard, ct);

        if (counterAttacks)
        {
            await _damage.CombatAsync(target, attacker, damageToAttacker, view.TriggerCard, ct);
        }

        // 交战存活信号（死亡判定之后才发——已阵亡者不发射）：被攻击者在前、攻击者在后。
        await EmitCombatSurvivedAsync(_engine, target, ct);
        await EmitCombatSurvivedAsync(_engine, attacker, ct);
        view.Result?.MarkApplied(1);
    }

    /// <summary>发射 unit.combat.survived（E1-39）：仅对**未阵亡**的参战单位发射（恰一次；已死亡＝零发射）。</summary>
    private static Task EmitCombatSurvivedAsync(LogicEngine engine, UnitCard unit, CancellationToken ct)
        => unit.GetData<UnitStateData>().IsDestroyed
            ? Task.CompletedTask
            : GameUpdates.EmitUnitCombatSurvived(engine, unit, ct);
}
