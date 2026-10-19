using Orc.Cards;
using Orc.Core;
using Orc.Game.Actions;
using Orc.Game.Board;
using Orc.Game.Cards;

namespace Orc.Foundation.Actions;

/// <summary>
/// 卡牌移动的**两端共用**视图（S6）：触发端与处理端都读同一组载荷。
/// <para>
/// 触发端只做通用前置（单位在场未毁／目标槽存在且为空／非原槽）；处理端只判「目标是否可落」。
/// 「谁能移动」（回合／<c>CanMove</c>／费用／压制）属**指挥资格**，只出现在指挥入口与候选面，
/// 不出现在本动作的任何一段。
/// </para>
/// </summary>
[ContextView]
public class MoveActionView
{
    /// <summary>被移动的单位（必填）。</summary>
    [Read]
    public virtual UnitCard Unit { get; set; } = null!;

    /// <summary>目标槽位（必填；须存在、为空、非原槽）。</summary>
    [Read]
    public virtual Slot Destination { get; set; } = null!;

    /// <summary>触发者卡牌（效果发起时携带；玩家主动指挥＝空）。</summary>
    [Optional]
    [Read]
    public virtual Card? TriggerCard { get; set; }

    /// <summary>本次执行的结果载体（框架内部；作者不读写）。</summary>
    [Optional]
    [Read]
    public virtual ActionTargetResult? Result { get; set; }
}

/// <summary>
/// 卡牌移动动作实体（S6）：一个**触发端** ＋ 恰一个**处理端**。
/// <para>
/// 三入口（被指挥／被控／撤退）共用本动作：差异（扣费／清行动位／发 <c>unit.acted</c>／方向约束）
/// 只在各自的**入口分支**表达；本类内部无「按入口类型分支」。
/// </para>
/// <para>
/// 动作本身零资源消耗（R16）：本类不读指挥点、不写 <c>CanMove</c>/<c>CanAttack</c>、
/// 不发 <c>unit.acted</c>、不检查压制。
/// </para>
/// <para>
/// P5b：本类迁出宿主程序集（<c>orc.foundation.actions</c>），实现宿主契约 <see cref="IMoveAction"/>；
/// 两端以 <see cref="ActionTriggerEnd"/> 自报，供动作目录按声明装配。
/// </para>
/// </summary>
public sealed class MoveAction : IMoveAction
{
    private const int DefaultPriority = 100;

    private readonly LogicEngine _engine;
    private readonly IMoveActionServices _services;

    /// <summary>创建移动动作（触发端通用前置／默认转发与处理端默认落地都在本类内注册）。</summary>
    /// <param name="engine">对局引擎（执行流挂载点）。</param>
    /// <param name="services">动作服务面（唯一落地口；实现由宿主提供）。</param>
    /// <exception cref="ArgumentNullException">engine / services 为 null。</exception>
    public MoveAction(LogicEngine engine, IMoveActionServices services)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(services);
        _engine = engine;
        _services = services;

        Trigger = new Trigger<MoveActionView>("卡牌移动触发器", bandType: typeof(ActionTriggerBands));
        Handle = new Trigger<MoveActionView>("卡牌移动处理触发器", bandType: typeof(ActionHandleBands));

        Trigger.Register("通用前置", ValidateAsync, ActionTriggerBands.Initiate, DefaultPriority);
        Trigger.Register("转发到处理端", ForwardAsync, ActionTriggerBands.Commit, DefaultPriority);
        Handle.Register("落地移动", HandleLandAsync, ActionHandleBands.Apply, DefaultPriority);

        Ends = new[]
        {
            new ActionTriggerEnd("触发端", Trigger, (registry, layer) => registry.Register(Trigger, layer)),
            new ActionTriggerEnd(ActionTriggerEnd.HandleRole, Handle, (registry, layer) => registry.Register(Handle, layer)),
        };
    }

    /// <summary>移动**触发端**（<c>Initiate</c> 通用前置 → <c>Commit</c> 默认转发）。</summary>
    public Trigger<MoveActionView> Trigger { get; }

    /// <summary>移动**处理端**（<c>Rewrite</c>/<c>Veto</c>/<c>Apply</c>；<c>Apply</c> 默认 handler 是唯一落地口）。</summary>
    public Trigger<MoveActionView> Handle { get; }

    /// <inheritdoc />
    public IReadOnlyList<ActionTriggerEnd> Ends { get; }

    /// <inheritdoc />
    public Task<ActionResult> CommandAsync(
        UnitCard unit, Slot destination, Card? triggerCard = null, CancellationToken ct = default)
        => RunAsync(unit, destination, triggerCard, ct);

    /// <inheritdoc />
    public Task<ActionResult> EffectAsync(
        UnitCard unit, Slot destination, Card? triggerCard = null, CancellationToken ct = default)
        => RunAsync(unit, destination, triggerCard, ct);

    /// <summary>通用前置（触发端）：单位在场未毁／目标槽存在且为空／非原槽；不满足＝目标不合法、零落地。</summary>
    private static Task ValidateAsync(MoveActionView view, Context ctx, CancellationToken ct)
    {
        if (!view.Unit.TryGetData<UnitStateData>(out var state)
            || state.IsDestroyed
            || state.Position is not { } oldSlot
            || ReferenceEquals(oldSlot, view.Destination)
            || !view.Destination.IsEmpty)
        {
            view.Result?.MarkTargetInvalid();
            ctx.Stop();
        }

        return Task.CompletedTask;
    }

    /// <summary>转发到处理端（触发端默认 handler）。</summary>
    private Task ForwardAsync(MoveActionView view, Context ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(view);
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [nameof(MoveActionView.Unit)] = view.Unit,
            [nameof(MoveActionView.Destination)] = view.Destination,
            [nameof(MoveActionView.Result)] = view.Result,
        };
        if (view.TriggerCard is not null)
        {
            data[nameof(MoveActionView.TriggerCard)] = view.TriggerCard;
        }

        return Handle.InvokeAsync(_engine, data, ct);
    }

    /// <summary>处理端默认执行体（唯一调用动作服务面处）。</summary>
    private async Task HandleLandAsync(MoveActionView view, Context ctx, CancellationToken ct)
    {
        if (await _services.LandMoveAsync(view.Unit, view.Destination, ct).ConfigureAwait(false))
        {
            view.Result?.MarkApplied(1);
        }
        else
        {
            view.Result?.MarkTargetInvalid();
        }
    }

    private async Task<ActionResult> RunAsync(
        UnitCard unit, Slot destination, Card? triggerCard, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(destination);

        var result = new ActionTargetResult(unit.Ref, unit, 1);
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [nameof(MoveActionView.Unit)] = unit,
            [nameof(MoveActionView.Destination)] = destination,
            [nameof(MoveActionView.Result)] = result,
        };
        if (triggerCard is not null)
        {
            data[TriggerPayloadKeys.TriggerCard] = triggerCard;
        }

        await Trigger.InvokeAsync(_engine, data, ct).ConfigureAwait(false);

        if (result.Outcome == ActionOutcomeKind.Skipped)
        {
            // 上游被否决（Veto/中断）但无人回填结局：按被阻断收口（有留痕、零落地）。
            result.MarkBlocked(ActionFailureKind.Blocked);
        }

        return new ActionResult(ActionId.Move, result);
    }
}
