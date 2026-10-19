using Orc.Cards;
using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Players;
using Orc.Game.Actions;

namespace Orc.Foundation.Actions;

/// <summary>
/// 抽牌动作实体（S7）：两个**触发端**（卡组顶＝<c>DrawInstance</c>；指定牌＝<c>RemoveInstance</c>）
/// ＋恰一个**处理端**。
/// <para>
/// 取件差异只存在于触发端；此后「移除（已由触发端完成）→ <c>card.drawn</c> → 加入手牌（子动作）
/// →〔满手〕爆牌分支」完全共用，处理端零入口类型分支。
/// </para>
/// <para>
/// <c>card.drawn</c> 由处理端恰发一次，且**先于**调用加入手牌动作——满手／非满手共用同一位置。
/// </para>
/// <para>
/// 实现注记：取件（<c>DrawInstance</c> / <c>RemoveInstance</c>）在**触发端包装步骤**内执行，而不在
/// <c>Initiate</c> 段 handler 内——空卡组必须保持集合契约的 <c>InvalidOperationException</c> 上抛语义
/// （<c>Trigger</c> 会把 handler 内异常收口为 <c>ContractFailure</c>，不再是可捕获的业务抛错）。
/// 语义上它仍是「触发端差异」：两入口只有取件不同，之后的「drawn → 加入手牌」完全共用。
/// </para>
/// </summary>
public sealed class DrawAction : IDrawAction
{
    private const int DefaultPriority = 100;

    private readonly LogicEngine _engine;
    private readonly IDrawActionServices _services;
    private readonly IAddToHandAction _addToHand;

    /// <summary>创建抽牌动作（两触发端与共享处理端的默认 handler 都在本类内注册）。</summary>
    /// <param name="engine">对局引擎（执行流挂载点）。</param>
    /// <param name="services">动作服务面（取件／<c>card.drawn</c>；实现由宿主提供）。</param>
    /// <param name="addToHand">子动作：加入手牌（满手时由它完成爆牌）。</param>
    /// <exception cref="ArgumentNullException">任一参数为 null。</exception>
    public DrawAction(LogicEngine engine, IDrawActionServices services, IAddToHandAction addToHand)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(addToHand);
        _engine = engine;
        _services = services;
        _addToHand = addToHand;

        Top = new Trigger<DrawTriggerView>("卡组顶抽牌触发器", bandType: typeof(ActionTriggerBands));
        Specific = new Trigger<DrawTriggerView>("指定牌抽取触发器", bandType: typeof(ActionTriggerBands));
        Handle = new Trigger<DrawHandleView>("抽牌处理触发器", bandType: typeof(ActionHandleBands));

        Top.Register("通用前置", ValidateAsync, ActionTriggerBands.Initiate, DefaultPriority);
        Top.Register("转发到处理端", ForwardAsync, ActionTriggerBands.Commit, DefaultPriority);
        Specific.Register("通用前置", ValidateAsync, ActionTriggerBands.Initiate, DefaultPriority);
        Specific.Register("转发到处理端", ForwardAsync, ActionTriggerBands.Commit, DefaultPriority);
        Handle.Register("抽牌落地", HandleApplyAsync, ActionHandleBands.Apply, DefaultPriority);
    }

    /// <summary>卡组顶抽牌**触发端**。</summary>
    public Trigger<DrawTriggerView> Top { get; }

    /// <summary>指定牌抽取**触发端**。</summary>
    public Trigger<DrawTriggerView> Specific { get; }

    /// <summary>抽牌**处理端**（两入口共用；<c>card.drawn</c> 在此恰发一次）。</summary>
    public Trigger<DrawHandleView> Handle { get; }

    /// <inheritdoc />
    public async Task<ActionResult> DrawTopAsync(
        Player player, Card? triggerCard = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(player);

        // 取件＝触发端差异（卡组顶）；空卡组沿用集合契约的明确抛错（不经触发器——见类注释）。
        var card = _services.TakeTopFromDeck(player);
        return await RunAsync(Top, player, card, triggerCard, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ActionResult> DrawSpecificAsync(
        CardBase card, Player player, Card? triggerCard = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(player);

        if (!ReferenceEquals(card.Owner, player)
            || !card.Life.IsAlive
            || !player.Deck.ContainsInstance(card))
        {
            var rejected = new ActionTargetResult(card.Ref, card, 1);
            rejected.MarkTargetInvalid();
            return new ActionResult(ActionId.Draw, rejected);
        }

        // 取件＝触发端差异（指定实例）；此后与卡组顶路径完全共用。
        _services.TakeSpecificFromDeck(player, card);
        return await RunAsync(Specific, player, card, triggerCard, ct).ConfigureAwait(false);
    }

    /// <summary>通用前置（两个触发端共用）：取件结果必须存活且归属抽牌玩家。</summary>
    private static Task ValidateAsync(DrawTriggerView view, Context ctx, CancellationToken ct)
    {
        if (!view.Card.Life.IsAlive || !ReferenceEquals(view.Card.Owner, view.Player))
        {
            view.Result?.MarkTargetInvalid();
            ctx.Stop();
        }

        return Task.CompletedTask;
    }

    /// <summary>转发到处理端（两触发端各自恰注册一次）。</summary>
    private Task ForwardAsync(DrawTriggerView view, Context ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(view);
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [nameof(DrawHandleView.Player)] = view.Player,
            [nameof(DrawHandleView.Card)] = view.Card,
            [nameof(DrawHandleView.Result)] = view.Result,
        };
        if (view.TriggerCard is not null)
        {
            data[nameof(DrawHandleView.TriggerCard)] = view.TriggerCard;
        }

        return Handle.InvokeAsync(_engine, data, ct);
    }

    /// <summary>处理端默认执行体：先 <c>card.drawn</c>（恰一次），再交加入手牌子动作。</summary>
    private async Task HandleApplyAsync(DrawHandleView view, Context ctx, CancellationToken ct)
    {
        await _services.EmitCardDrawnAsync(view.Player, view.Card, ct).ConfigureAwait(false);

        var added = await _addToHand.AddAsync(view.Card, view.Player, view.TriggerCard, ct).ConfigureAwait(false);
        var addedTarget = added.Targets.Count > 0 ? added.Targets[0] : null;
        if (addedTarget is { Outcome: ActionOutcomeKind.Applied })
        {
            view.Result?.MarkApplied(1);
        }
        else
        {
            view.Result?.MarkBlocked(addedTarget?.Reason ?? ActionFailureKind.Blocked);
        }
    }

    private async Task<ActionResult> RunAsync(
        Trigger<DrawTriggerView> trigger, Player player, CardBase card, Card? triggerCard, CancellationToken ct)
    {
        var result = new ActionTargetResult(card.Ref, card, 1);
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [nameof(DrawTriggerView.Player)] = player,
            [nameof(DrawTriggerView.Card)] = card,
            [nameof(DrawTriggerView.Result)] = result,
        };
        if (triggerCard is not null)
        {
            data[TriggerPayloadKeys.TriggerCard] = triggerCard;
        }

        await trigger.InvokeAsync(_engine, data, ct).ConfigureAwait(false);

        if (result.Outcome == ActionOutcomeKind.Skipped)
        {
            result.MarkBlocked(ActionFailureKind.Blocked);
        }

        return new ActionResult(ActionId.Draw, result);
    }
}
