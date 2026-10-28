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
internal sealed partial class MatchActionServices :
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
        state.Line?.TryRemove(unit);
        state.IsDestroyed = true;

        // ②.5 宿主侧死亡信号（`card.dying`）：**亡计内容的主驱动点**——内容＝卡实体上挂在
        // `engine#card.dying` 的 v4 组件成员（DeathrattleRules.ContentSignal），因此"结算"发生在
        // 置毁之后、词条注销之前，与本流程的时序契约同源，不再由 C# 侧另行驱动一回。
        // 隔离口径（域内决定，不是总线口径）：内容组件抛错不得留下半死状态——记录后继续清理；
        // 取消类照旧穿透（与全仓取消语义一致）。
        var contentThrew = false;
        try
        {
            await GameUpdates.EmitCardDying(_engine, unit, killer, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            contentThrew = true;
            RecordDeathrattleError(ex);
        }

        // ③ 亡计结算**兜底驱动**（b／§29）：主驱动已在 ②.5 完成，这里只对"内容组件没声明 card.dying 挂载"
        // 的形态生效——驱动的是同一个成员，已结算过的实例被 `one-shot` 额度挡住＝零副作用（恰一次由额度保证）。
        // ②.5 已经抛过错的实例不再重试（避免同一次死亡把内容跑两遍）。
        if (!contentThrew)
        {
            try
            {
                if (_retrigger is not null)
                {
                    await _retrigger.ExecuteDeathrattleAsync(unit, ct).ConfigureAwait(false);
                }
                else
                {
                    await DeathrattleRules.ExecuteAsync(unit, _engine, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                RecordDeathrattleError(ex);
            }
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

        // ⑦ 效果卸载：卡上效果容器随 `Effect` 家族删除——效果＝编排实体上的 v4 组件，
        //    由实体销毁（CardBase.Destroy → entity.Destroy）统一卸载；此处不再枚举效果列表。

        // ⑧ 位置字段置空（实例保留可查询）。
        state.Line = null;

        // ⑨ card.died（恰一次；击杀者为改写后的归属）。
        await GameUpdates.EmitCardDied(_engine, unit, killer, ct, source: killer).ConfigureAwait(false);
    }

    /// <summary>亡计内容异常隔离记录（域内决定：死亡必须完成——旧口径 "结算异常隔离、清理继续" 的承载点）。</summary>
    private void RecordDeathrattleError(Exception ex)
        => _engine.RootStream.WriteLog(
            "亡计",
            $"亡计内容执行异常（隔离，死亡流程继续）：{ex.Message}",
            LogLevel.Error,
            new[] { $"exception:{ex.GetType().Name}" },
            new Dictionary<string, object?>
            {
                ["exceptionType"] = ex.GetType().FullName,
                ["message"] = ex.Message,
            });

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
