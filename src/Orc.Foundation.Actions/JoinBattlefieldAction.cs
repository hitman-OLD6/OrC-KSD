using Orc.Cards;
using Orc.Core;
using Orc.Game.Board;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Players;
using Orc.Game.Actions;

namespace Orc.Foundation.Actions;

/// <summary>
/// 加入战场动作实体（S9）：**两个触发端**（部署／加入）＋恰一个**共享处理端**。
/// <para>
/// 部署触发端：<see cref="ActionTriggerBands.Initiate"/> 代价校验 → <see cref="ActionTriggerBands.Announce"/>
/// 发 <c>card.played</c> → <see cref="ActionTriggerBands.Commit"/> 先跑部署词条（priority 50）→ 默认转发（priority 100）。
/// </para>
/// <para>
/// 加入触发端：<see cref="ActionTriggerBands.Initiate"/> 前置 → <see cref="ActionTriggerBands.Commit"/> 直接转发。
/// </para>
/// <para>
/// 处理端：单位化 + 落位 + <c>card.placed</c>（唯一落地口，零入口类型分支）。入口收尾（<c>unit.deployed</c>／
/// <c>unit.joined</c>、扣费、离手、<c>OnDeployChainFinalized</c>）留在入口，不进处理端。
/// </para>
/// </summary>
public sealed class JoinBattlefieldAction : IJoinBattlefieldAction
{
    /// <summary>部署词条在部署触发端 <c>Commit</c> 段的位置（默认转发在 100）。</summary>
    private const int DeployKeywordPriority = 50;

    private const int DefaultPriority = 100;

    private readonly LogicEngine _engine;
    private readonly IJoinActionServices _services;

    /// <summary>创建加入战场动作（两端默认链与处理端默认落地在本类内注册）。</summary>
    /// <param name="engine">对局引擎（执行流挂载点／信号发射）。</param>
    /// <param name="services">动作服务面（唯一落地口；实现由宿主提供）。</param>
    /// <exception cref="ArgumentNullException">engine / services 为 null。</exception>
    public JoinBattlefieldAction(LogicEngine engine, IJoinActionServices services)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(services);
        _engine = engine;
        _services = services;

        Deploy = new Trigger<JoinBattlefieldTriggerView>(
            "加入战场·部署触发器", bandType: typeof(ActionTriggerBands));
        Join = new Trigger<JoinBattlefieldTriggerView>(
            "加入战场·加入触发器", bandType: typeof(ActionTriggerBands));
        Handle = new Trigger<JoinBattlefieldHandleView>(
            "加入战场处理触发器", bandType: typeof(ActionHandleBands));

        // 部署触发端：代价校验（发起前）→ 宣告 → 部署词条（Commit 前部）→ 默认转发。
        Deploy.Register("部署代价校验", ValidateDeployAsync, ActionTriggerBands.Initiate, DefaultPriority);
        Deploy.Register("打出宣告", AnnounceAsync, ActionTriggerBands.Announce, DefaultPriority);
        Deploy.Register("部署词条", HandleDeployKeywordsAsync, ActionTriggerBands.Commit, DeployKeywordPriority);
        Deploy.Register("转发到处理端", ForwardAsync, ActionTriggerBands.Commit, DefaultPriority);

        // 加入触发端：前置校验（无宣告段）→ 直接转发。
        Join.Register("加入前置", ValidateJoinAsync, ActionTriggerBands.Initiate, DefaultPriority);
        Join.Register("转发到处理端", ForwardAsync, ActionTriggerBands.Commit, DefaultPriority);

        // 共享处理端：单位化 + 落位 + card.placed。
        Handle.Register("单位化与落位", HandleApplyAsync, ActionHandleBands.Apply, DefaultPriority);

    }

    /// <summary>部署**触发端**。</summary>
    public Trigger<JoinBattlefieldTriggerView> Deploy { get; }

    /// <summary>加入**触发端**。</summary>
    public Trigger<JoinBattlefieldTriggerView> Join { get; }

    /// <summary>共享**处理端**（单位化 + 落位 + <c>card.placed</c>）。</summary>
    public Trigger<JoinBattlefieldHandleView> Handle { get; }

    /// <inheritdoc />
    public async Task<ActionResult> DeployAsync(
        UnitCard card, Slot slot, Player player, Card? triggerCard = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(player);

        var result = new ActionTargetResult(card.Ref, card, 1);
        if (!CheckStructure(card, slot, result))
        {
            return new ActionResult(ActionId.JoinBattlefield, result);
        }

        await Deploy.InvokeAsync(_engine, BuildData(card, slot, player, triggerCard, result), ct)
            .ConfigureAwait(false);
        return new ActionResult(ActionId.JoinBattlefield, result);
    }

    /// <inheritdoc />
    public async Task<ActionResult> JoinAsync(
        UnitCard card, Slot slot, Card? triggerCard = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(slot);

        var result = new ActionTargetResult(card.Ref, card, 1);
        if (!CheckStructure(card, slot, result))
        {
            return new ActionResult(ActionId.JoinBattlefield, result);
        }

        await Join.InvokeAsync(_engine, BuildData(card, slot, player: null, triggerCard, result), ct)
            .ConfigureAwait(false);
        return new ActionResult(ActionId.JoinBattlefield, result);
    }

    /// <summary>结构性前置（两条路径共用；不满足＝<c>TargetInvalid</c>、零状态变更）。</summary>
    private static bool CheckStructure(UnitCard card, Slot slot, ActionTargetResult result)
    {
        if (!slot.IsEmpty || card.TryGetData<UnitStateData>(out _))
        {
            result.MarkTargetInvalid();
            return false;
        }

        return true;
    }

    private static Dictionary<string, object?> BuildData(
        UnitCard card, Slot slot, Player? player, Card? triggerCard, ActionTargetResult result)
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [nameof(JoinBattlefieldTriggerView.Card)] = card,
            [nameof(JoinBattlefieldTriggerView.Slot)] = slot,
            [nameof(JoinBattlefieldTriggerView.Result)] = result,
        };

        if (player is not null)
        {
            data[nameof(JoinBattlefieldTriggerView.Player)] = player;
        }

        if (triggerCard is not null)
        {
            data[TriggerPayloadKeys.TriggerCard] = triggerCard;
        }

        return data;
    }

    /// <summary>部署代价校验（发起前；点数不足＝拒绝、零状态变更）。</summary>
    private static Task ValidateDeployAsync(JoinBattlefieldTriggerView view, Context ctx, CancellationToken ct)
    {
        if (view.Player is { } player)
        {
            var cost = view.Card.Modifiers.GetEffectiveValue(CardStatFields.DeployCost);
            if (player.Points < cost)
            {
                view.Result?.MarkSkipped(ActionFailureKind.Precondition);
                ctx.Stop();
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>加入前置（发起前）：结构性错误＝拒绝。</summary>
    private static Task ValidateJoinAsync(JoinBattlefieldTriggerView view, Context ctx, CancellationToken ct)
    {
        if (!view.Slot.IsEmpty || view.Card.TryGetData<UnitStateData>(out _))
        {
            view.Result?.MarkTargetInvalid();
            ctx.Stop();
        }

        return Task.CompletedTask;
    }

    /// <summary>打出宣告（<c>card.played</c>——部署入口唯一宣告点；R9：Source＝触发者，缺省回退本卡）。</summary>
    private Task AnnounceAsync(JoinBattlefieldTriggerView view, Context ctx, CancellationToken ct)
        => GameUpdates.EmitCardPlayed(
            _engine, view.Card, view.Player!, ct, view.TriggerCard ?? view.Card);

    /// <summary>部署词条（<c>Commit</c> 段前部；零信号；未进入处理端）。</summary>
    private Task HandleDeployKeywordsAsync(JoinBattlefieldTriggerView view, Context ctx, CancellationToken ct)
        => _services.InvokeDeployKeywordAsync(view.Card, view.Player, view.Slot, ct);

    /// <summary>默认转发到处理端（两个触发端共用；处理端不知道入口类型）。</summary>
    private Task ForwardAsync(JoinBattlefieldTriggerView view, Context ctx, CancellationToken ct)
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [nameof(JoinBattlefieldHandleView.Card)] = view.Card,
            [nameof(JoinBattlefieldHandleView.Slot)] = view.Slot,
            [nameof(JoinBattlefieldHandleView.Result)] = view.Result,
        };

        if (view.Player is not null)
        {
            data[nameof(JoinBattlefieldHandleView.Player)] = view.Player;
        }

        if (view.TriggerCard is not null)
        {
            data[nameof(JoinBattlefieldHandleView.TriggerCard)] = view.TriggerCard;
        }

        return Handle.InvokeAsync(_engine, data, ct);
    }

    /// <summary>处理端默认落地（唯一调用动作服务面处）：单位化 + 落位 + <c>card.placed</c>。</summary>
    private async Task HandleApplyAsync(JoinBattlefieldHandleView view, Context ctx, CancellationToken ct)
    {
        var applied = await _services.UnitizeOnJoinAsync(view.Card, view.Slot, view.TriggerCard, ct)
            .ConfigureAwait(false);
        if (!applied)
        {
            view.Result?.MarkTargetInvalid();
            ctx.Stop();
            return;
        }

        view.Result?.MarkApplied(1);
    }
}
