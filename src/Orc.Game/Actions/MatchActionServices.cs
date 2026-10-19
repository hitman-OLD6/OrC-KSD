using Orc.Cards;
using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Players;

namespace Orc.Game.Actions;

/// <summary>
/// 动作服务面的宿主实现（S4）：**internal sealed**、永不迁移（P5 只迁动作定义与默认 handler）。
/// <para>
/// 它是宿主（对局）对插件提供的授权面——插件自带实现即等于自我授权，故实现必须留在 <c>Orc.Game</c>。
/// 本类不持有任何管理器实例；需要的运行期能力一律经构造参数（对象或委托）注入。
/// </para>
/// <para>
/// 覆盖动作：伤害结算（P1）、卡牌移动／抽牌／加入手牌／死亡（P2）。方法名按动作归属唯一（S4 第 3 条）。
/// </para>
/// </summary>
internal sealed class MatchActionServices :
    IDamageActionServices,
    IMoveActionServices,
    IDrawActionServices,
    IHandActionServices,
    IDeathActionServices,
    IJoinActionServices
{
    private readonly LogicEngine _engine;
    private readonly Func<UnitCard, Func<Task>, Task>? _withDamageSource;
    private readonly PincerRegistry? _pincers;
    private readonly RetriggerSystem? _retrigger;

    /// <summary>创建服务实现。</summary>
    /// <param name="engine">对局引擎（信号发射）。</param>
    /// <param name="withDamageSource">伤害来源游标作用域（由宿主提供——致死归属 Killer；缺省＝不设游标）。</param>
    /// <param name="pincers">钳击关系注册表（死亡链的失效通知；缺省＝独立构造，跳过通知）。</param>
    /// <param name="retrigger">再触发服务（亡计结算统一执行面；缺省＝直调执行面降级）。</param>
    internal MatchActionServices(
        LogicEngine engine,
        Func<UnitCard, Func<Task>, Task>? withDamageSource = null,
        PincerRegistry? pincers = null,
        RetriggerSystem? retrigger = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _withDamageSource = withDamageSource;
        _pincers = pincers;
        _retrigger = retrigger;
    }

    // ---------- 伤害结算（P1） ----------

    /// <inheritdoc />
    public async Task<int> ApplyDamageAsync(
        Card? source, Card target, int amount, string kind, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (amount <= 0)
        {
            return 0; // 归零/改写为 0：不进门户、零发射（「0 伤害不算受到伤害」）
        }

        switch (target)
        {
            case UnitCard unit:
            {
                if (!unit.TryGetData<UnitStateData>(out var state) || state.IsDestroyed)
                {
                    return 0; // 已死：尸体不参与数值结算（同时结算的后续方向照常）
                }

                var defenseBefore = unit.Modifiers.GetEffectiveValue(CardStatFields.Defense);
                if (source is UnitCard dealer && _withDamageSource is not null)
                {
                    // E1-47：结算窗口内设置伤害来源游标——门户触发的「防御归零统一死亡衔接」在同一同步链内，
                    // 游标把「谁造成了这次伤害」带给死亡衔接。
                    await _withDamageSource(dealer, () => unit.ApplyDefenseDamageAsync(amount, ct))
                        .ConfigureAwait(false);
                }
                else
                {
                    await unit.ApplyDefenseDamageAsync(amount, ct).ConfigureAwait(false);
                }

                var dealt = defenseBefore - unit.Modifiers.GetEffectiveValue(CardStatFields.Defense);
                if (dealt > 0 && source is UnitCard unitDealer)
                {
                    await GameUpdates.EmitUnitDamageDealt(_engine, unitDealer, unit, dealt, ct).ConfigureAwait(false);
                }

                return dealt;
            }

            case Hq hq:
            {
                var healthBefore = hq.Health;
                await hq.ApplyDamageAsync(amount, ct).ConfigureAwait(false);
                var dealt = healthBefore - hq.Health;
                if (dealt > 0 && source is UnitCard hqDealer)
                {
                    await GameUpdates.EmitUnitDamageDealt(_engine, hqDealer, hq, dealt, ct).ConfigureAwait(false);
                }

                return dealt;
            }

            default:
                return 0;
        }
    }

    // ---------- 卡牌移动（S6） ----------

    /// <inheritdoc />
    public async Task<bool> LandMoveAsync(UnitCard unit, Slot destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(destination);

        if (!unit.TryGetData<UnitStateData>(out var state) || state.IsDestroyed)
        {
            return false;
        }

        if (state.Position is not { } oldSlot)
        {
            return false;
        }

        if (ReferenceEquals(oldSlot, destination) || !destination.IsEmpty)
        {
            return false;
        }

        // 旧槽释放 → 目标槽放入 → 位置字段改写 → 发射更新（恰一次）。
        // 「槽位变更」是动作状态的唯一写点：指挥入口与效果入口都只经这里落地。
        oldSlot.Clear();
        destination.Place(unit);
        state.Position = destination;
        await GameUpdates.EmitUnitPositionChanged(_engine, unit, oldSlot, destination, ct).ConfigureAwait(false);
        return true;
    }

    // ---------- 抽牌（S7） ----------

    /// <inheritdoc />
    public CardBase TakeTopFromDeck(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return player.Deck.DrawInstance(); // 空集合 / 未加载＝集合契约的明确抛错
    }

    /// <inheritdoc />
    public void TakeSpecificFromDeck(Player player, CardBase card)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(card);
        player.Deck.RemoveInstance(card);
    }

    /// <inheritdoc />
    public Task EmitCardDrawnAsync(Player player, CardBase card, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(card);
        return GameUpdates.EmitCardDrawn(_engine, player, card, ct);
    }

    // ---------- 加入手牌（S7） ----------

    /// <inheritdoc />
    public async Task AddCardToHandAsync(Player player, CardBase card, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(card);
        player.Hand.Add(card); // 尾部追加（对齐一切「抽牌/生成到手牌」先例）
        await GameUpdates.EmitCardHandAdd(_engine, player, card, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task BurnCardAtHandLimitAsync(Player player, CardBase card, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(card);
        return HandLimitBurn.BurnAsync(_engine, player, card, ct);
    }

    // ---------- 加入战场（S9） ----------

    /// <inheritdoc />
    public async Task<bool> UnitizeOnJoinAsync(
        UnitCard card, Slot slot, Card? triggerCard, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(slot);

        // 结构不变量（矛盾中间态防护）：仅空槽、未单位化的卡可单位化——违反＝false、零落地。
        if (!slot.IsEmpty || card.TryGetData<UnitStateData>(out _))
        {
            return false;
        }

        // 单位化的**唯一执行面**（卡侧单源入口）：加单位组件＋指挥组件＋实际占用空槽＋跑链一次。
        // 调用者只有本方法（加入战场动作处理端），注入「单位化触发器」的效果照常参与。
        await card.InvokeUnitizeAsync(slot, ct).ConfigureAwait(false);
        if (!card.TryGetData<UnitStateData>(out _))
        {
            return false; // 单位化未完成（结构性拒绝）：不发射 card.placed
        }

        // 放置驱动装载信号：单位化（组件挂载＋槽位占用）成功之后、完成信号（入口发）之前。
        await GameUpdates.EmitCardPlaced(_engine, card, ct, triggerCard ?? card).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public Task InvokeDeployKeywordAsync(
        UnitCard card, Player? player, Slot slot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(slot);
        return card.InvokeDeployKeywordAsync(player, slot, ct);
    }

    // ---------- 死亡（S8） ----------

    /// <inheritdoc />
    public async Task ApplyDeathAsync(UnitCard unit, Card? killer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unit);

        var state = unit.GetData<UnitStateData>();
        if (state.IsDestroyed)
        {
            return; // 死亡恰一次（幂等防护）
        }

        // ① 槽位释放（变空槽）→ ② IsDestroyed 置位。
        state.Position?.Clear();
        state.IsDestroyed = true;

        // 宿主侧死亡效果必须在登记仍存在时结算；完成态的观察仍留在清理之后的 card.died。
        await GameUpdates.EmitCardDying(_engine, unit, killer, ct).ConfigureAwait(false);

        // ③ 亡计结算（置毁之后、词条死亡注销之前；清理前结算）。
        if (_retrigger is not null)
        {
            await _retrigger.ExecuteDeathrattleAsync(unit, ct).ConfigureAwait(false);
        }
        else
        {
            await DeathrattleRules.ExecuteAsync(unit, _engine, ct).ConfigureAwait(false);
        }

        // ④ 词条死亡注销（仅行为撤销：OnRevoke 序列＋运行逻辑注销＋内嵌效果卸载；登记/参值保留可查询）。
        KeywordRules.RevokeAllOnDeath(unit);

        // ⑤ 修饰器清理（含期限订阅随销；数值整合至最终态、零新发射）。
        await unit.Modifiers.ClearAllForDeathAsync(ct).ConfigureAwait(false);

        // ⑥ 钳击失效通知（一方离场——另一方即时失去；同事务内落定）。
        if (_pincers is not null)
        {
            await PincerRules.OnUnitLeftBattlefieldAsync(unit, _pincers, ct).ConfigureAwait(false);
        }

        // ⑦ 效果卸载（容器移除＋OnUnmount＋撤销登记＋总线卸载；托管清理幂等）。
        foreach (var effect in unit.Effects.ToArray())
        {
            unit.RemoveEffect(effect);
        }

        // ⑧ 位置字段置空（实例保留可查询）。
        state.Position = null;

        // ⑨ card.died（恰一次；击杀者为改写后的归属）。
        await GameUpdates.EmitCardDied(_engine, unit, killer, ct, source: killer).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CardBase?> CreateReturnInstanceAsync(UnitCard unit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unit);

        if (unit.Owner is not { } owner || owner.CardService is not { } cardService)
        {
            return null; // 独立构造/未装配：无卡牌工厂可用（零副作用）
        }

        if (!cardService.TryResolveDefinitionId(unit, out var definitionId))
        {
            return null; // 定义未注册于本局：不可回手（零副作用）
        }

        return await cardService.CreateAsync(definitionId, owner, ct).ConfigureAwait(false);
    }
}
