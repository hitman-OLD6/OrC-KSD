using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Cards;
using Orc.Game.Model;
using Orc.Game.Actions;

namespace Orc.Foundation.Actions;

/// <summary>
/// 死亡动作实体（S8）：一个**触发端** ＋ **处理端拆出的两个成员**（免死守卫 <c>death.veto</c>／死亡链 <c>death.handle</c>）。
/// <para>
/// 两个旧的并列取消口（外部拦截器布尔 + 词条程序 <c>death.cancel</c>）都收敛为 <c>death.veto</c> 上的
/// **同一条外来挂载边**（<c>Rewrite</c> 段——早于免死守卫所在 <c>Veto</c> 段）；只有一个调用位置，
/// 按既有次序（拦截器先、词条后）由注册优先级表达。
/// </para>
/// <para>
/// <b>为什么拆两个成员</b>（R1i band 修订，2026-10-29）：band 模型下"一个成员的自有边落在一段"，
/// 而旧的 <c>death.handle</c> 横跨 <c>Veto</c>（免死守卫）与 <c>Apply</c>（死亡链）两段。拆开后两段由
/// 段序表达，而不是"同一成员内前一条边 Stop 掉后一条"。
/// </para>
/// <para>
/// <b>Stop 跨成员语义</b>：<c>ctx.Stop()</c> 的作用域仍是**当前成员**（S2a），不外溢——
/// 因此"免死守卫停了，死亡链不跑"由 <see cref="ForwardAsync"/> 显式完成：先调 <c>death.veto</c>，
/// 读它的 <see cref="MemberExecutionResult.Stopped"/>，命中就不再调 <c>death.handle</c>。
/// 这与旧模型"同成员内 Veto 段停掉 Apply 段"逐项等价（旧行为的 Stop 边界正是这个处理端）。
/// 注意这也覆盖"改写方（<c>Rewrite</c> 段外来边）直接 Stop"：端点实体的 Stop 穿透到 <c>death.veto</c>，
/// 守卫被跳过，<c>death.veto</c> 的 Stopped 仍为 true，死亡链同样不跑。
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

        // U5-a：两端＝引擎成员；实例＝过渡期内部执行面（挂接点经 ActionEnds 取回）。
        // 处理端地址仍是 death.handle（动作目录的"处理端"＝死亡链落地）；免死守卫是拆出的内部成员。
        Trigger = MemberAddress.Create(AddressScope.Engine, EngineProcesses.Death);
        Handle = MemberAddress.Create(AddressScope.Engine, EngineProcesses.DeathHandle);

        // 自有边：触发端前置／转发；处理端＝免死守卫（death.veto 的 Veto 段）与死亡链（death.handle 的 Apply 段）。
        // 两段的分段由成员声明的 band 表达，不再依赖"同成员内声明序"。
        ActionEdges.Attach<DeathTriggerView>(_engine, EngineProcesses.Death, ValidateAsync);
        ActionEdges.Attach<DeathTriggerView>(_engine, EngineProcesses.Death, ForwardAsync);
        ActionEdges.Attach<DeathHandleView>(_engine, EngineProcesses.DeathVeto, GuardAsync);
        ActionEdges.Attach<DeathHandleView>(_engine, EngineProcesses.DeathHandle, HandleApplyAsync);

        Ends = new[]
        {
            new ActionTriggerEnd("触发端", Trigger),
            new ActionTriggerEnd(ActionTriggerEnd.HandleRole, Handle),
        };
    }

    /// <inheritdoc />
    public MemberAddress Trigger { get; }

    /// <inheritdoc />
    public MemberAddress Handle { get; }

    /// <inheritdoc />
    public IReadOnlyList<ActionTriggerEnd> Ends { get; }

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

        await _engine.InvokeMemberAsync(EngineProcesses.Death, new Context(data), ct).ConfigureAwait(false);

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

    /// <summary>
    /// 转发到处理端（触发端默认 handler；<c>Disposition</c> 默认显式写入 <c>null</c>）。
    /// <para>
    /// 处理端＝两个成员：先 <c>death.veto</c>（<c>Rewrite</c> 段外来改写 → <c>Veto</c> 段免死守卫），
    /// 再 <c>death.handle</c>（<c>Veto</c>／<c>Apply</c> 之上的死亡链落地）。
    /// <b>免死命中＝<c>death.veto</c> 的 Stopped 为 true ⇒ 不发 <c>death.handle</c></b>——
    /// 这是本方法存在的唯一理由：<c>ctx.Stop()</c> 的作用域限于当前成员（S2a），不会自己跨成员生效。
    /// </para>
    /// </summary>
    private async Task ForwardAsync(DeathTriggerView view, Context ctx, CancellationToken ct)
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

        // 同一份数据面串起两个成员：守卫写入的 ShouldDie／Result 对死亡链可见（R1c 的写回语义）。
        var veto = await _engine
            .InvokeMemberAsync(EngineProcesses.DeathVeto, new Context(data), ct)
            .ConfigureAwait(false);
        if (veto.Stopped || veto.ValidationRejected)
        {
            return; // 免死（守卫 Stop）或改写方直接 Stop：死亡链不执行（与旧"同成员内 Veto 停 Apply"等价）
        }

        await _engine.InvokeMemberAsync(EngineProcesses.DeathHandle, new Context(data), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 免死守卫（<c>death.veto</c> 的 <c>Veto</c> 段）：<c>ShouldDie=false</c> ＝ 免死——有留痕（log）、零状态改动、
    /// 结果 <c>Blocked(DeathPrevented)</c>；命中即 <c>ctx.Stop()</c>（停本成员剩余单元）。
    /// <para>
    /// 死亡链不执行不靠本方法：Stop 的作用域只到当前成员，跨成员由 <see cref="ForwardAsync"/> 读
    /// <see cref="MemberExecutionResult.Stopped"/> 决定是否调用 <c>death.handle</c>。
    /// </para>
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
