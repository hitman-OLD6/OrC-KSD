using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Model;
using Orc.Game.Players;
using Orc.Game.Actions;

namespace Orc.Foundation.Actions;

/// <summary>
/// 「加入手牌」动作实体（S7）：独立可复用；**两端齐全**（触发端 ＋ 处理端），
/// 入口＝<see cref="IAddToHandAction.AddAsync"/>。
/// <para>
/// 爆牌是「加入手牌」的**失败分支**（R11），在**处理端**内完成：<c>DestroyCard</c> → <c>card.burned</c>，
/// 不走弃置路线（<c>card.burned</c> 与 <c>card.discarded</c> 互斥），<c>card.hand.add</c> 零次。
/// </para>
/// </summary>
public sealed class AddToHandAction : IAddToHandAction
{
    private const int DefaultPriority = 100;

    private readonly LogicEngine _engine;
    private readonly IHandActionServices _services;
    private bool _bound;

    /// <summary>创建加入手牌动作（两端默认 handler 都在本类内注册）。</summary>
    /// <param name="engine">对局引擎（执行流挂载点）。</param>
    /// <param name="services">动作服务面（唯一写手牌处；实现由宿主提供）。</param>
    /// <exception cref="ArgumentNullException">engine / services 为 null。</exception>
    public AddToHandAction(LogicEngine engine, IHandActionServices services)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(services);
        _engine = engine;
        _services = services;

        // U5-a：两端＝引擎成员；实例是过渡期内部执行面（U5-b 随旧类型退役）。
        Trigger = MemberAddress.Create(AddressScope.Engine, EngineProcesses.HandAdd);
        Handle = MemberAddress.Create(AddressScope.Engine, EngineProcesses.HandAddHandle);

        Ends = new[]
        {
            new ActionTriggerEnd("触发端", Trigger),
            new ActionTriggerEnd(ActionTriggerEnd.HandleRole, Handle),
        };
    }

    /// <summary>
    /// 把两端的默认流程接成引擎成员上的**自有边**（首次执行时接入；缺编排运行时＝明确失败）。
    /// <para>接入序＝执行序：通用前置 → 转发到处理端／加入手牌（旧 Initiate／Commit／Apply 段由位次表达）。</para>
    /// </summary>
    private void EnsureBound()
    {
        if (_bound)
        {
            return;
        }

        var runtime = _engine.Model
            ?? throw new InvalidOperationException(
                $"加入手牌动作需要已装配的编排运行时（{Trigger}／{Handle} 是引擎成员）：本引擎未挂 Model。");

        runtime.Engine.AttachOwnedEdge(EngineProcesses.HandAdd, MemberEdges.For<AddToHandTriggerView>(ValidateAsync));
        runtime.Engine.AttachOwnedEdge(EngineProcesses.HandAdd, MemberEdges.For<AddToHandTriggerView>(ForwardAsync));
        runtime.Engine.AttachOwnedEdge(EngineProcesses.HandAddHandle, MemberEdges.For<AddToHandHandleView>(HandleApplyAsync));
        _bound = true;
    }

    /// <inheritdoc />
    public MemberAddress Trigger { get; }

    /// <inheritdoc />
    public MemberAddress Handle { get; }

    /// <inheritdoc />
    public IReadOnlyList<ActionTriggerEnd> Ends { get; }

    /// <inheritdoc />
    public async Task<ActionResult> AddAsync(
        CardBase card, Player player, Card? triggerCard = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(player);

        var result = new ActionTargetResult(card.Ref, card, 1);
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [nameof(AddToHandTriggerView.Card)] = card,
            [nameof(AddToHandTriggerView.Player)] = player,
            [nameof(AddToHandTriggerView.Result)] = result,
        };
        if (triggerCard is not null)
        {
            data[TriggerPayloadKeys.TriggerCard] = triggerCard;
        }

        EnsureBound();
        await _engine.InvokeMemberAsync(EngineProcesses.HandAdd, new Context(data), ct).ConfigureAwait(false);

        if (result.Outcome == ActionOutcomeKind.Skipped)
        {
            result.MarkBlocked(ActionFailureKind.Blocked);
        }

        return new ActionResult(ActionId.HandAdd, result);
    }

    /// <summary>通用前置（触发端）：卡存活、未销毁、归属＝落点玩家、尚未在手牌中。</summary>
    private static Task ValidateAsync(AddToHandTriggerView view, Context ctx, CancellationToken ct)
    {
        if (!view.Card.Life.IsAlive
            || !ReferenceEquals(view.Card.Owner, view.Player)
            || view.Player.Hand.Contains(view.Card))
        {
            view.Result?.MarkTargetInvalid();
            ctx.Stop();
        }

        return Task.CompletedTask;
    }

    /// <summary>转发到处理端（触发端默认 handler）。</summary>
    private Task ForwardAsync(AddToHandTriggerView view, Context ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(view);
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [nameof(AddToHandHandleView.Card)] = view.Card,
            [nameof(AddToHandHandleView.Player)] = view.Player,
            [nameof(AddToHandHandleView.Result)] = view.Result,
        };
        if (view.TriggerCard is not null)
        {
            data[nameof(AddToHandHandleView.TriggerCard)] = view.TriggerCard;
        }

        return _engine.InvokeMemberAsync(EngineProcesses.HandAddHandle, new Context(data), ct);
    }

    /// <summary>处理端默认执行体：满手＝爆牌失败分支；未满＝入列表＋<c>card.hand.add</c>。</summary>
    private async Task HandleApplyAsync(AddToHandHandleView view, Context ctx, CancellationToken ct)
    {
        if (HandLimitBurn.IsAtLimit(view.Player))
        {
            await _services.BurnCardAtHandLimitAsync(view.Player, view.Card, ct).ConfigureAwait(false);
            view.Result?.MarkBlocked(ActionFailureKind.HandFull);
            return;
        }

        await _services.AddCardToHandAsync(view.Player, view.Card, ct).ConfigureAwait(false);
        view.Result?.MarkApplied(1);
    }
}
