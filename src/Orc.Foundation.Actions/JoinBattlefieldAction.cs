using Orc.Cards;
using Orc.Core;
using Orc.Core.Model;
using Orc.Game.Board;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Model;
using Orc.Game.Players;
using Orc.Game.Actions;

namespace Orc.Foundation.Actions;

/// <summary>
/// 加入战场动作实体（S9；有序列表模型）：**三个触发端**（部署／加入前线／加入支援阵线）＋恰一个**共享处理端**。
/// <para>
/// 部署触发端（**自有边，声明序**）：代价校验 → 打出宣告（发 <c>card.played</c>）→ 部署词条 → 默认转发。
/// </para>
/// <para>
/// 加入触发端（前线／支援阵线）：前置校验 → 直接转发（同为自有边、声明序）。
/// 落位规则＝显式序号 &gt; 守护（HQ 右侧，仅支援线）&gt; 默认最左；前线被敌方存活单位占据＝**报错**（Error 留痕＋失败）；
/// 目标线已满＝**静默失败**（Warning 留痕＋失败）；两者均不抛异常。
/// </para>
/// <para>
/// 处理端：单位化 + 插入阵线 + <c>card.placed</c>（唯一落地口，零入口类型分支）。入口收尾（<c>unit.deployed</c>／
/// <c>unit.joined</c>、扣费、离手、<c>OnDeployChainFinalized</c>）留在入口，不进处理端。
/// </para>
/// </summary>
public sealed class JoinBattlefieldAction : IJoinBattlefieldAction
{
    private const int DefaultPriority = 100;

    private const string FrontLineZone = "frontline";
    private const string SupportLineZone = "support";

    private readonly LogicEngine _engine;
    private readonly IJoinActionServices _services;

    /// <summary>创建加入战场动作（三端默认链与处理端默认落地在本类内注册）。</summary>
    /// <param name="engine">对局引擎（执行流挂载点／信号发射）。</param>
    /// <param name="services">动作服务面（唯一落地口；实现由宿主提供）。</param>
    /// <exception cref="ArgumentNullException">engine / services 为 null。</exception>
    public JoinBattlefieldAction(LogicEngine engine, IJoinActionServices services)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(services);
        _engine = engine;
        _services = services;

        // U3：四端＝引擎成员（地址由契约自报）；实例＝过渡期内部执行面。
        Deploy = MemberAddress.Create(AddressScope.Engine, EngineProcesses.JoinDeploy);
        JoinFrontline = MemberAddress.Create(AddressScope.Engine, EngineProcesses.JoinFrontline);
        JoinSupportLine = MemberAddress.Create(AddressScope.Engine, EngineProcesses.JoinSupport);
        Handle = MemberAddress.Create(AddressScope.Engine, EngineProcesses.JoinHandle);


        // 部署触发端：代价校验 → 宣告 → 部署词条 → 默认转发（**自有边，按声明序**）。
        // D2（U4）：词条先于转发由声明序表达——不再用 50/100 的数字差（R1i：不引入优先级数字）。
        ActionEdges.Attach<JoinBattlefieldTriggerView>(_engine, EngineProcesses.JoinDeploy, ValidateDeployAsync);
        ActionEdges.Attach<JoinBattlefieldTriggerView>(_engine, EngineProcesses.JoinDeploy, AnnounceAsync);
        ActionEdges.Attach<JoinBattlefieldTriggerView>(_engine, EngineProcesses.JoinDeploy, HandleDeployKeywordsAsync);
        ActionEdges.Attach<JoinBattlefieldTriggerView>(_engine, EngineProcesses.JoinDeploy, ForwardAsync);

        // 加入前线触发端：敌对占线门禁 → 加入前置 → 直接转发。
        ActionEdges.Attach<JoinBattlefieldTriggerView>(_engine, EngineProcesses.JoinFrontline, ValidateFrontlineAsync);
        ActionEdges.Attach<JoinBattlefieldTriggerView>(_engine, EngineProcesses.JoinFrontline, ValidateJoinAsync);
        ActionEdges.Attach<JoinBattlefieldTriggerView>(_engine, EngineProcesses.JoinFrontline, ForwardAsync);

        // 加入支援阵线触发端：结构前置 → 直接转发。
        ActionEdges.Attach<JoinBattlefieldTriggerView>(_engine, EngineProcesses.JoinSupport, ValidateJoinAsync);
        ActionEdges.Attach<JoinBattlefieldTriggerView>(_engine, EngineProcesses.JoinSupport, ForwardAsync);

        // 共享处理端：单位化 + 插入阵线 + card.placed。
        ActionEdges.Attach<JoinBattlefieldHandleView>(_engine, EngineProcesses.JoinHandle, HandleApplyAsync);

        Ends = new[]
        {
            new ActionTriggerEnd("部署触发端", Deploy),
            new ActionTriggerEnd("加入前线触发端", JoinFrontline),
            new ActionTriggerEnd("加入支援阵线触发端", JoinSupportLine),
            new ActionTriggerEnd(ActionTriggerEnd.HandleRole, Handle),
        };
    }

    /// <inheritdoc />
    public MemberAddress Deploy { get; }

    /// <inheritdoc />
    public MemberAddress JoinFrontline { get; }

    /// <inheritdoc />
    public MemberAddress JoinSupportLine { get; }

    /// <inheritdoc />
    public MemberAddress Handle { get; }

    /// <inheritdoc />
    public IReadOnlyList<ActionTriggerEnd> Ends { get; }

    /// <inheritdoc />
    public async Task<ActionResult> DeployAsync(
        UnitCard card, FormationGap gap, Player player, Card? triggerCard = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(gap);
        ArgumentNullException.ThrowIfNull(player);

        var result = new ActionTargetResult(card.Ref, card, 1);
        if (!CheckStructure(card, gap, result))
        {
            return new ActionResult(ActionId.JoinBattlefield, result);
        }

        await _engine.InvokeMemberAsync(EngineProcesses.JoinDeploy, new Context(BuildData(card, gap, player, triggerCard, result)), ct)
            .ConfigureAwait(false);
        return new ActionResult(ActionId.JoinBattlefield, result);
    }

    /// <inheritdoc />
    public Task<ActionResult> JoinFrontlineAsync(
        UnitCard card, int? index = null, Card? triggerCard = null, CancellationToken ct = default)
        => JoinZoneAsync(card, FrontLineZone, index, triggerCard, ct);

    /// <inheritdoc />
    public Task<ActionResult> JoinSupportLineAsync(
        UnitCard card, int? index = null, Card? triggerCard = null, CancellationToken ct = default)
        => JoinZoneAsync(card, SupportLineZone, index, triggerCard, ct);

    /// <inheritdoc />
    public async Task<ActionResult> JoinAtAsync(
        UnitCard card, FormationGap gap, Card? triggerCard = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(gap);

        var result = new ActionTargetResult(card.Ref, card, 1);
        if (!CheckStructure(card, gap, result))
        {
            return new ActionResult(ActionId.JoinBattlefield, result);
        }

        var member = ReferenceEquals(gap.Line, SupportLineOf(card)) ? EngineProcesses.JoinSupport : EngineProcesses.JoinFrontline;
        await _engine.InvokeMemberAsync(member, new Context(BuildData(card, gap, player: null, triggerCard, result)), ct)
            .ConfigureAwait(false);
        return new ActionResult(ActionId.JoinBattlefield, result);
    }

    /// <summary>按区域加入（落位解析 → 触发端）。</summary>
    private async Task<ActionResult> JoinZoneAsync(
        UnitCard card, string zone, int? index, Card? triggerCard, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(card);

        var result = new ActionTargetResult(card.Ref, card, 1);
        var gap = _services.ResolveJoinGap(card, zone, index, out var failure);
        if (gap is null)
        {
            ReportPlacementFailure(card, zone, failure);
            if (failure == JoinPlacementFailure.EnemyHoldsFrontline)
            {
                result.MarkTargetInvalid();
            }
            else
            {
                result.MarkSkipped(ActionFailureKind.Precondition);
            }

            return new ActionResult(ActionId.JoinBattlefield, result);
        }

        if (!CheckStructure(card, gap, result))
        {
            return new ActionResult(ActionId.JoinBattlefield, result);
        }

        var member = zone == FrontLineZone ? EngineProcesses.JoinFrontline : EngineProcesses.JoinSupport;
        await _engine.InvokeMemberAsync(member, new Context(BuildData(card, gap, player: null, triggerCard, result)), ct)
            .ConfigureAwait(false);
        return new ActionResult(ActionId.JoinBattlefield, result);
    }

    /// <summary>落位失败留痕（敌方占线＝Error〔"报错"〕；其余＝Warning〔静默失败〕；均不抛）。</summary>
    private void ReportPlacementFailure(UnitCard card, string zone, JoinPlacementFailure failure)
    {
        var level = failure == JoinPlacementFailure.EnemyHoldsFrontline ? LogLevel.Error : LogLevel.Warning;
        var message = failure switch
        {
            JoinPlacementFailure.EnemyHoldsFrontline =>
                $"单位 '{card.Name}' 加入前线被拒绝：前线存在存活敌方单位（效果须自行先判定前线归属）。",
            JoinPlacementFailure.LineFull =>
                $"单位 '{card.Name}' 加入{(zone == FrontLineZone ? "前线" : "支援阵线")}被拒绝：目标阵线已满（容量上限）。",
            JoinPlacementFailure.IndexOutOfRange =>
                $"单位 '{card.Name}' 加入被拒绝：目标序号越界。",
            JoinPlacementFailure.AlreadyOnBoard =>
                $"单位 '{card.Name}' 加入被拒绝：该单位已在场。",
            _ => $"单位 '{card.Name}' 加入被拒绝（{failure}）。",
        };

        _engine.RootStream.WriteLog(
            "加入战场",
            message,
            level,
            new[] { $"reason:{failure}" },
            new Dictionary<string, object?> { ["card"] = card.Name, ["zone"] = zone });
    }

    /// <summary>结构性前置（两条路径共用；不满足＝<c>TargetInvalid</c>、零状态变更）。</summary>
    private static bool CheckStructure(UnitCard card, FormationGap gap, ActionTargetResult result)
    {
        if (!gap.IsCurrent || card.TryGetData<UnitStateData>(out _))
        {
            result.MarkTargetInvalid();
            return false;
        }

        return true;
    }

    private static Dictionary<string, object?> BuildData(
        UnitCard card, FormationGap gap, Player? player, Card? triggerCard, ActionTargetResult result)
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [nameof(JoinBattlefieldTriggerView.Card)] = card,
            [nameof(JoinBattlefieldTriggerView.Gap)] = gap,
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
        if (!view.Gap.IsCurrent || view.Card.TryGetData<UnitStateData>(out _))
        {
            view.Result?.MarkTargetInvalid();
            ctx.Stop();
        }

        return Task.CompletedTask;
    }

    /// <summary>加入前线门禁（发起前）：前线存在存活敌方单位＝报错拒绝（效果须自行先判定）。</summary>
    private Task ValidateFrontlineAsync(JoinBattlefieldTriggerView view, Context ctx, CancellationToken ct)
    {
        if (_services.EnemyHoldsFrontline(view.Card.Owner))
        {
            ReportPlacementFailure(view.Card, FrontLineZone, JoinPlacementFailure.EnemyHoldsFrontline);
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
        => _services.InvokeDeployKeywordAsync(view.Card, view.Player, view.Gap, ct);

    /// <summary>默认转发到处理端（三个触发端共用；处理端不知道入口类型）。</summary>
    private Task ForwardAsync(JoinBattlefieldTriggerView view, Context ctx, CancellationToken ct)
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [nameof(JoinBattlefieldHandleView.Card)] = view.Card,
            [nameof(JoinBattlefieldHandleView.Gap)] = view.Gap,
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

        return _engine.InvokeMemberAsync(EngineProcesses.JoinHandle, new Context(data), ct);
    }

    /// <summary>处理端默认落地（唯一调用动作服务面处）：单位化 + 插入阵线 + <c>card.placed</c>。</summary>
    private async Task HandleApplyAsync(JoinBattlefieldHandleView view, Context ctx, CancellationToken ct)
    {
        var applied = await _services.UnitizeOnJoinAsync(view.Card, view.Gap, view.TriggerCard, ct)
            .ConfigureAwait(false);
        if (!applied)
        {
            view.Result?.MarkTargetInvalid();
            ctx.Stop();
            return;
        }

        view.Result?.MarkApplied(1);
    }

    private static BattleLine? SupportLineOf(UnitCard card)
        => GameEnvironment.ResolveFor(card)?.Battlefield is { } battlefield && card.Owner is { } owner
            ? battlefield.GetSupportLine(owner)
            : null;
}
