using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Actions;

namespace Orc.Foundation.Actions;

/// <summary>
/// 死亡动作实体（S8）：一个**触发端** ＋ 恰一个**处理端**。
/// <para>
/// 两个旧的并列取消口（外部拦截器布尔 + 词条程序 <c>death.cancel</c>）都收敛为处理端
/// <see cref="ActionHandleBands.Rewrite"/> 段的 handler；只有一个调用位置，按既有次序（拦截器先、词条后）
/// 由注册优先级表达。
/// </para>
/// <para>
/// <b>处置执行者</b>：<see cref="ExecuteAsync"/> 这个**动作层包装**在拿到 <see cref="ActionResult"/> 后
/// 读 <see cref="ActionTargetResult.Disposition"/> 并执行回手组合（与指令动作的 <c>IOrderAction</c> 同构）。
/// 处理端不调「加入手牌」动作。
/// </para>
/// </summary>
public sealed class DeathAction : IDeathAction
{
    private const int DefaultPriority = 100;

    private readonly LogicEngine _engine;
    private readonly IDeathActionServices _services;
    private readonly IAddToHandAction _addToHand;

    /// <summary>创建死亡动作（触发端转发／免死守卫／死亡链落地都在本类内注册）。</summary>
    /// <param name="engine">对局引擎（执行流挂载点）。</param>
    /// <param name="services">动作服务面（唯一死亡链落地口；实现由宿主提供）。</param>
    /// <param name="addToHand">回手组合的子动作（<c>Disposition=ToHand</c> 时由动作层包装调用）。</param>
    /// <exception cref="ArgumentNullException">任一参数为 null。</exception>
    public DeathAction(LogicEngine engine, IDeathActionServices services, IAddToHandAction addToHand)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(addToHand);
        _engine = engine;
        _services = services;
        _addToHand = addToHand;

        Trigger = new Trigger<DeathTriggerView>("卡牌死亡触发器", bandType: typeof(ActionTriggerBands));
        Handle = new Trigger<DeathHandleView>("死亡处理触发器", bandType: typeof(ActionHandleBands));

        Trigger.Register("通用前置", ValidateAsync, ActionTriggerBands.Initiate, DefaultPriority);
        Trigger.Register("转发到处理端", ForwardAsync, ActionTriggerBands.Commit, DefaultPriority);
        Handle.Register("免死守卫", GuardAsync, ActionHandleBands.Veto, DefaultPriority);
        Handle.Register("执行死亡链", HandleApplyAsync, ActionHandleBands.Apply, DefaultPriority);
    }

    /// <summary>死亡**触发端**。</summary>
    public Trigger<DeathTriggerView> Trigger { get; }

    /// <summary>死亡**处理端**（<c>Rewrite</c> 单改写位 / <c>Veto</c> 免死守卫 / <c>Apply</c> 死亡链）。</summary>
    public Trigger<DeathHandleView> Handle { get; }

    /// <inheritdoc />
    public async Task<ActionResult> ExecuteAsync(
        UnitCard unit, Card? killer = null, Card? triggerCard = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unit);

        var result = new ActionTargetResult(unit.Ref, unit, 1);
        if (!unit.TryGetData<UnitStateData>(out var state) || state.IsDestroyed)
        {
            result.MarkTargetInvalid(); // 已死者不再进入死亡链（幂等；零信号）
            return new ActionResult(ActionId.Death, result);
        }

        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [nameof(DeathTriggerView.Unit)] = unit,
            [nameof(DeathTriggerView.ShouldDie)] = true,
            [nameof(DeathTriggerView.Result)] = result,
        };
        if (killer is not null)
        {
            data[nameof(DeathTriggerView.Killer)] = killer;
        }

        if (triggerCard is not null)
        {
            data[TriggerPayloadKeys.TriggerCard] = triggerCard;
        }

        await Trigger.InvokeAsync(_engine, data, ct).ConfigureAwait(false);

        if (result.Outcome == ActionOutcomeKind.Skipped)
        {
            // 改写方直接 ctx.Stop()（未走 Veto 守卫）＝同样是免死：收口为 Blocked 并留痕。
            result.MarkBlocked(ActionFailureKind.DeathPrevented);
            _engine.RootStream.WriteLog(
                "卡牌死亡触发器",
                $"单位 '{unit.Name}' 的消灭被改写为不发生（免死）。",
                LogLevel.Info,
                new[] { "death:prevented" });
        }

        // 入口分支（S8/R12）：处置指向由动作层包装执行——处理端不调其他动作。
        if (result is { Outcome: ActionOutcomeKind.Applied, Disposition: ActionDisposition.ToHand })
        {
            await ReturnToHandAsync(unit, triggerCard, ct).ConfigureAwait(false);
        }

        return new ActionResult(ActionId.Death, result);
    }

    /// <summary>通用前置（触发端）：单位在场且未毁（已毁＝幂等跳过，零副作用）。</summary>
    private static Task ValidateAsync(DeathTriggerView view, Context ctx, CancellationToken ct)
    {
        if (!view.Unit.TryGetData<UnitStateData>(out var state) || state.IsDestroyed)
        {
            view.Result?.MarkTargetInvalid();
            ctx.Stop();
        }

        return Task.CompletedTask;
    }

    /// <summary>转发到处理端（触发端默认 handler；<c>Disposition</c> 默认显式写入 <c>null</c>）。</summary>
    private Task ForwardAsync(DeathTriggerView view, Context ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(view);
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [nameof(DeathHandleView.Unit)] = view.Unit,
            [nameof(DeathHandleView.ShouldDie)] = view.ShouldDie,
            [nameof(DeathHandleView.Disposition)] = null,
            [nameof(DeathHandleView.Result)] = view.Result,
        };
        if (view.Killer is not null)
        {
            data[nameof(DeathHandleView.Killer)] = view.Killer;
        }

        if (view.TriggerCard is not null)
        {
            data[nameof(DeathHandleView.TriggerCard)] = view.TriggerCard;
        }

        return Handle.InvokeAsync(_engine, data, ct);
    }

    /// <summary>
    /// 免死守卫（<c>Veto</c> 段）：<c>ShouldDie=false</c> ＝ 免死——有留痕（log）、零状态改动、结果
    /// <c>Blocked(DeathPrevented)</c>；命中即停止本级后续（<c>Apply</c> 不执行）。
    /// </summary>
    private Task GuardAsync(DeathHandleView view, Context ctx, CancellationToken ct)
    {
        if (view.ShouldDie)
        {
            return Task.CompletedTask;
        }

        view.Result?.MarkBlocked(ActionFailureKind.DeathPrevented);
        ctx.Engine?.RootStream.WriteLog(
            "死亡处理触发器",
            $"单位 '{view.Unit.Name}' 的消灭被改写为不发生（免死）。",
            LogLevel.Info,
            new[] { "death:prevented" });
        ctx.Stop();
        return Task.CompletedTask;
    }

    /// <summary>处理端默认执行体（唯一调用动作服务面处）：执行死亡链并回填结局／处置指向。</summary>
    private async Task HandleApplyAsync(DeathHandleView view, Context ctx, CancellationToken ct)
    {
        await _services.ApplyDeathAsync(view.Unit, view.Killer, ct).ConfigureAwait(false);
        view.Result?.MarkApplied(1);
        if (view.Result is not null)
        {
            view.Result.Disposition = view.Disposition;
        }
    }

    /// <summary>回手组合（复用 <c>return.hand</c> 的形态：解析定义 id → 新建同定义实例 → 加入手牌）。</summary>
    private async Task ReturnToHandAsync(UnitCard unit, Card? triggerCard, CancellationToken ct)
    {
        if (await _services.CreateReturnInstanceAsync(unit, ct).ConfigureAwait(false) is not { } created)
        {
            return;
        }

        if (created.Owner is not { } owner)
        {
            return;
        }

        await _addToHand.AddAsync(created, owner, triggerCard, ct).ConfigureAwait(false);
    }
}
