using Orc.Cards;
using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Players;

namespace Orc.Game.Sandbox;

/// <summary>
/// 沙盒调试原语族（引擎保留触发器）：六个触发器各恰一个内置 handler，**构造完成即封闭**（handler 集固定），
/// 且实例只由 <see cref="Match"/> 以 internal 字段持有——公开面只暴露原语入口方法（双防线：不可达 ＋ 封闭态）。
/// <para>
/// 授权：由装配方在构造期决定（<see cref="MatchOptions.SandboxAuthorized"/>，缺省未授权）；
/// 未授权时全部原语一律返回 <see cref="SandboxFailure.NotAuthorized"/>、零状态变更。
/// </para>
/// <para>
/// 执行语义＝**等效于卡牌效果**：只跳过校验层（点数／回合／手牌／相位／选靶），保留硬门槛
/// （槽位容量、槽位被占、已单位化、实例唯一归属、已销毁），失败经 <see cref="SandboxPrimitiveResult"/> 回传、不抛异常。
/// </para>
/// <para>
/// 落地底座＝引擎唯一写点：<c>MatchCardService.CreateAsync / PlaceToHandAsync / PlaceToDeckTopAsync /
/// PlaceToSupportLineAsync</c>、<c>PlayManager.JoinUnitAsync</c>、<c>MatchActionServices.LandMoveAsync /
/// ApplyDeathAsync</c>、<c>Engine.DestroyCard</c>、<c>HandLimitBurn.DestroyAndEmitDiscardedAsync</c>、
/// <c>PlayerManager.DrawCard</c>、<c>ResourceManager.GainPointsAsync / LosePointsAsync</c>。
/// 禁止动作入口（不扣费、不校验相位/回合/手牌）。
/// </para>
/// </summary>
public sealed class SandboxPrimitives
{
    private const string ZoneHand = "hand";
    private const string ZoneDeckTop = "deck_top";
    private const string LineSupport = "support";
    private const string LineFrontline = "frontline";

    private readonly Match _match;
    private readonly bool _authorized;

    private readonly Trigger<SandboxPrimitiveView> _instantiate;
    private readonly Trigger<SandboxPrimitiveView> _unitize;
    private readonly Trigger<SandboxPrimitiveView> _move;
    private readonly Trigger<SandboxPrimitiveView> _remove;
    private readonly Trigger<SandboxPrimitiveView> _setPoints;
    private readonly Trigger<SandboxPrimitiveView> _draw;

    /// <summary>
    /// 构造原语族（对局装配期一次）：每个原语「注册唯一内置 handler → 立即封闭」；
    /// 任一注册失败直接抛出（缺 handler ⇒ 实例不产出，不存在"可触发但缺 handler"的中间态）。
    /// </summary>
    /// <exception cref="ArgumentNullException">match 为 null。</exception>
    internal SandboxPrimitives(Match match, bool authorized)
    {
        ArgumentNullException.ThrowIfNull(match);
        _match = match;
        _authorized = authorized;

        _instantiate = CreateSealedTrigger("sandbox.instantiate", "instantiate", HandleInstantiateAsync, out var instantiateRegistration);
        _unitize = CreateSealedTrigger("sandbox.unitize", "unitize", HandleUnitizeAsync, out _);
        _move = CreateSealedTrigger("sandbox.move", "move", HandleMoveAsync, out _);
        _remove = CreateSealedTrigger("sandbox.remove", "remove", HandleRemoveAsync, out _);
        _setPoints = CreateSealedTrigger("sandbox.points", "set_points", HandleSetPointsAsync, out _);
        _draw = CreateSealedTrigger("sandbox.draw", "draw", HandleDrawAsync, out _);

        InstantiateRegistration = instantiateRegistration;
    }

    /// <summary>
    /// instantiate 触发器内置注册项的句柄（**仅测试可达**：用于断言封闭后包装器注册被拒；
    /// 不构成对外注册入口——触发器已封闭，任何注册调用一律抛）。
    /// </summary>
    internal TriggerRegistration InstantiateRegistration { get; }

    // ═════════════════════════════════════════════════════════════════════════
    // 公开入口（六个原语）
    // 顺序固定：①授权 → ②参数 → ③硬门槛（不满足＝不 Invoke、零副作用）→ ④触发内置 handler
    //           → ⑤读结果载体 → ⑥审计留痕（每次调用恰一条，只进日志、不 BeginAction、不产段）。
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 实例化一张卡并落到指定去向：<c>hand</c>（满手＝爆牌，成功语义）／<c>deck_top</c>（静默）／
    /// <c>support</c>＋<paramref name="slot"/>／<c>frontline</c>＋<paramref name="slot"/>（精确槽位）。
    /// </summary>
    /// <param name="definitionId">已注册的卡定义 id。</param>
    /// <param name="owner">归属玩家（＝落点玩家）。</param>
    /// <param name="zone"><c>hand</c> / <c>deck_top</c> / <c>support</c> / <c>frontline</c>。</param>
    /// <param name="slot">线内槽位索引（<paramref name="zone"/> 为两条阵线时必填，0..4）。</param>
    public async Task<SandboxPrimitiveResult> InstantiateAsync(
        string definitionId, Player owner, string zone, int? slot = null, CancellationToken ct = default)
    {
        var summary = $"definitionId={definitionId}, owner=#{owner?.Index}, zone={zone}, slot={slot?.ToString() ?? "-"}";
        const string primitive = "instantiate";

        if (!_authorized)
        {
            return Reject(primitive, SandboxFailure.NotAuthorized, summary);
        }

        if (!TryReadInstantiate(definitionId, owner, zone, slot, out var request, out var reason))
        {
            return Reject(primitive, reason, summary);
        }

        if (CheckInstantiateGates(request) is { } gate)
        {
            return Reject(primitive, gate, summary);
        }

        var result = new SandboxPrimitiveResult();
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["DefinitionId"] = request.DefinitionId,
            ["Owner"] = request.Owner,
            ["Zone"] = request.Zone,
            ["Line"] = request.Zone is LineSupport or LineFrontline ? request.Zone : null,
            ["Slot"] = request.Zone is LineSupport or LineFrontline ? request.SlotIndex : null,
            ["Result"] = result,
        };

        await _instantiate.InvokeAsync(_match.Engine, data, ct).ConfigureAwait(false);
        WriteAudit(primitive, $"{summary} → {Describe(result)}");
        return result;
    }

    /// <summary>
    /// 把单位卡单位化到指定精确槽（加入链唯一写点 <c>JoinUnitAsync</c>）：发 <c>card.placed</c> ＋ <c>unit.joined</c>。
    /// 卡若在该玩家手牌中，先移出手牌（无信号）——避免「实例双归属」破坏引擎不变量。
    /// </summary>
    /// <param name="card">待单位化的单位卡。</param>
    /// <param name="line"><c>support</c> 或 <c>frontline</c>。</param>
    /// <param name="slot">线内槽位索引（0..4）。</param>
    public async Task<SandboxPrimitiveResult> UnitizeAsync(
        Card card, string line, int slot, CancellationToken ct = default)
    {
        var summary = $"ref={RefOf(card)}, line={line}, slot={slot}";
        const string primitive = "unitize";

        if (!_authorized)
        {
            return Reject(primitive, SandboxFailure.NotAuthorized, summary);
        }

        if (!TryReadTarget(card, line, slot, out var request, out var reason))
        {
            return Reject(primitive, reason, summary);
        }

        if (CheckUnitizeGates(request) is { } gate)
        {
            return Reject(primitive, gate, summary);
        }

        var result = new SandboxPrimitiveResult();
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Card"] = request.Card,
            ["Line"] = request.Line,
            ["Slot"] = request.SlotIndex,
            ["Result"] = result,
        };

        await _unitize.InvokeAsync(_match.Engine, data, ct).ConfigureAwait(false);
        WriteAudit(primitive, $"{summary} → {Describe(result)}");
        return result;
    }

    /// <summary>
    /// 把在场单位挪到另一空槽（效果侧唯一写点 <c>LandMoveAsync</c>）：发 <c>unit.position.changed</c>；
    /// 不扣行动费、不消耗行动次数。跨线允许。
    /// </summary>
    public async Task<SandboxPrimitiveResult> MoveAsync(
        Card card, string line, int slot, CancellationToken ct = default)
    {
        var summary = $"ref={RefOf(card)}, line={line}, slot={slot}";
        const string primitive = "move";

        if (!_authorized)
        {
            return Reject(primitive, SandboxFailure.NotAuthorized, summary);
        }

        if (!TryReadTarget(card, line, slot, out var request, out var reason))
        {
            return Reject(primitive, reason, summary);
        }

        if (CheckMoveGates(request) is { } gate)
        {
            return Reject(primitive, gate, summary);
        }

        var result = new SandboxPrimitiveResult();
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Card"] = request.Card,
            ["Line"] = request.Line,
            ["Slot"] = request.SlotIndex,
            ["Result"] = result,
        };

        await _move.InvokeAsync(_match.Engine, data, ct).ConfigureAwait(false);
        WriteAudit(primitive, $"{summary} → {Describe(result)}");
        return result;
    }

    /// <summary>
    /// 移除实体（无 <c>mode</c> 参数——分支由当前区域自动决定）：
    /// 手牌 → 弃置链（<c>card.destroyed</c> → <c>card.discarded</c>）／在场单位 → 死亡链（<c>card.dying</c> → <c>card.died</c>）／
    /// 其余 → 一步式销毁（<c>card.destroyed</c>）。
    /// </summary>
    public async Task<SandboxPrimitiveResult> RemoveAsync(Card card, CancellationToken ct = default)
    {
        var summary = $"ref={RefOf(card)}";
        const string primitive = "remove";

        if (!_authorized)
        {
            return Reject(primitive, SandboxFailure.NotAuthorized, summary);
        }

        if (card is null)
        {
            return Reject(primitive, SandboxFailure.InvalidArguments, summary);
        }

        if (CheckRemoveGates(card) is { } gate)
        {
            return Reject(primitive, gate, summary);
        }

        var result = new SandboxPrimitiveResult();
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Card"] = card,
            ["Result"] = result,
        };

        await _remove.InvokeAsync(_match.Engine, data, ct).ConfigureAwait(false);
        WriteAudit(primitive, $"{summary} → {Describe(result)}");
        return result;
    }

    /// <summary>
    /// 把玩家指挥点数设为绝对值（效果侧合成：升＝<c>GainPointsAsync</c>、降＝<c>LosePointsAsync</c>）：
    /// 升＝<c>point.gained</c> → <c>point.changed</c>；降＝<c>point.lost</c> → <c>point.changed</c>；同值＝零信号。
    /// 不受槽数限制（超出＝成功＋<c>points-exceed-slots</c> 告警）；判定器改写增量时＝成功＋<c>points-adjusted</c> 告警。
    /// </summary>
    public async Task<SandboxPrimitiveResult> SetPointsAsync(
        Player owner, int points, CancellationToken ct = default)
    {
        var summary = $"owner=#{owner?.Index}, points={points}";
        const string primitive = "points";

        if (!_authorized)
        {
            return Reject(primitive, SandboxFailure.NotAuthorized, summary);
        }

        if (owner is null || points < 0)
        {
            return Reject(primitive, SandboxFailure.InvalidArguments, summary);
        }

        if (CheckState() is { } stateReason)
        {
            return Reject(primitive, stateReason, summary);
        }

        var result = new SandboxPrimitiveResult();
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Owner"] = owner,
            ["Amount"] = points,
            ["Result"] = result,
        };

        await _setPoints.InvokeAsync(_match.Engine, data, ct).ConfigureAwait(false);
        WriteAudit(primitive, $"{summary} → {Describe(result)}");
        return result;
    }

    /// <summary>
    /// 抽 <paramref name="count"/> 张到手牌（效果侧唯一写点 <c>PlayerManager.DrawCard</c>；每张独立结局）：
    /// 未满手＝<c>card.drawn</c> → <c>card.hand.add</c>；满手＝<c>card.drawn</c> → <c>card.destroyed</c> → <c>card.burned</c>（成功语义）。
    /// </summary>
    public async Task<SandboxPrimitiveResult> DrawAsync(
        Player owner, int count = 1, CancellationToken ct = default)
    {
        var summary = $"owner=#{owner?.Index}, count={count}";
        const string primitive = "draw";

        if (!_authorized)
        {
            return Reject(primitive, SandboxFailure.NotAuthorized, summary);
        }

        if (owner is null || count < 0)
        {
            return Reject(primitive, SandboxFailure.InvalidArguments, summary);
        }

        if (CheckState() is { } stateReason)
        {
            return Reject(primitive, stateReason, summary);
        }

        if (count > 0 && owner.Deck.Count == 0)
        {
            return Reject(primitive, SandboxFailure.DeckEmpty, summary);
        }

        var result = new SandboxPrimitiveResult();
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Owner"] = owner,
            ["Amount"] = count,
            ["Result"] = result,
        };

        await _draw.InvokeAsync(_match.Engine, data, ct).ConfigureAwait(false);
        WriteAudit(primitive, $"{summary} → {Describe(result)}");
        return result;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 内置 handler（D11：首行参数校验失败一律写回 Result.Reason，禁止抛异常）
    // ═════════════════════════════════════════════════════════════════════════

    private async Task HandleInstantiateAsync(SandboxPrimitiveView view, Context ctx, CancellationToken ct)
    {
        if (view.Result is not { } result)
        {
            return; // 无载体（非本族入口的触发）＝无处回传；保持"不抛"口径
        }

        if (!TryReadInstantiate(view.DefinitionId, view.Owner, view.Zone, view.Slot, out var request, out var reason))
        {
            Fail(result, reason);
            return;
        }

        if (CheckInstantiateGates(request) is { } gate)
        {
            Fail(result, gate);
            return;
        }

        await RunInstantiateAsync(request, result, ct).ConfigureAwait(false);
    }

    private async Task HandleUnitizeAsync(SandboxPrimitiveView view, Context ctx, CancellationToken ct)
    {
        if (view.Result is not { } result)
        {
            return;
        }

        if (!TryReadTarget(view.Card, view.Line, view.Slot, out var request, out var reason))
        {
            Fail(result, reason);
            return;
        }

        if (CheckUnitizeGates(request) is { } gate)
        {
            Fail(result, gate);
            return;
        }

        // 手牌中的同一实例先移出手牌（集合操作、零信号）——单位化后不得「既在手牌又在场」。
        var unit = (UnitCard)request.Card;
        if (unit.Owner is { } owner && owner.Hand.Contains(unit))
        {
            owner.Hand.Remove(unit);
        }

        var slot = request.ResolvedSlot!;
        var joined = await _match.PlayManager.JoinUnitAsync(unit, slot, ct).ConfigureAwait(false);
        if (joined.Status == PlayResultStatus.Success)
        {
            Succeed(result, unit, request.Line, slot.Index);
            return;
        }

        Fail(result, MapPlayFailure(joined.FailureReason));
    }

    private async Task HandleMoveAsync(SandboxPrimitiveView view, Context ctx, CancellationToken ct)
    {
        if (view.Result is not { } result)
        {
            return;
        }

        if (!TryReadTarget(view.Card, view.Line, view.Slot, out var request, out var reason))
        {
            Fail(result, reason);
            return;
        }

        if (CheckMoveGates(request) is { } gate)
        {
            Fail(result, gate);
            return;
        }

        var unit = (UnitCard)request.Card;
        var slot = request.ResolvedSlot!;
        var moved = await _match.CommandManager.ActionServices.LandMoveAsync(unit, slot, ct).ConfigureAwait(false);
        if (moved)
        {
            Succeed(result, unit, request.Line, slot.Index);
            return;
        }

        Fail(result, MapMoveFailure(unit, slot));
    }

    private async Task HandleRemoveAsync(SandboxPrimitiveView view, Context ctx, CancellationToken ct)
    {
        if (view.Result is not { } result)
        {
            return;
        }

        if (view.Card is not { } card)
        {
            Fail(result, SandboxFailure.InvalidArguments);
            return;
        }

        if (CheckRemoveGates(card) is { } gate)
        {
            Fail(result, gate);
            return;
        }

        // 分支判定顺序固定：手牌 → 在场单位（UnitStateData 且未毁）→ 其余。
        if (card is CardBase handCard && OwnerOf(card) is { } owner && owner.Hand.Contains(handCard))
        {
            owner.Hand.Remove(handCard);
            await HandLimitBurn.DestroyAndEmitDiscardedAsync(_match.Engine, owner, handCard, ct).ConfigureAwait(false);
            Succeed(result, card, line: null, slotIndex: null);
            return;
        }

        if (card is UnitCard unit && unit.TryGetData<UnitStateData>(out var state) && !state.IsDestroyed)
        {
            await _match.CommandManager.ActionServices.ApplyDeathAsync(unit, killer: null, ct).ConfigureAwait(false);
            Succeed(result, card, line: null, slotIndex: null);
            return;
        }

        await _match.Engine.DestroyCard(card).ConfigureAwait(false);
        Succeed(result, card, line: null, slotIndex: null);
    }

    private async Task HandleSetPointsAsync(SandboxPrimitiveView view, Context ctx, CancellationToken ct)
    {
        if (view.Result is not { } result)
        {
            return;
        }

        if (view.Owner is not { } owner || view.Amount is not { } target)
        {
            Fail(result, SandboxFailure.InvalidArguments);
            return;
        }

        if (CheckState() is { } stateReason)
        {
            Fail(result, stateReason);
            return;
        }

        var before = owner.Points;
        var delta = target - before;
        if (delta > 0)
        {
            await _match.ResourceManager.GainPointsAsync(owner, delta, ct).ConfigureAwait(false);
        }
        else if (delta < 0)
        {
            await _match.ResourceManager.LosePointsAsync(owner, -delta, ct).ConfigureAwait(false);
        }

        var after = owner.Points;
        result.Ok = true;
        result.Reason = null;
        result.Before = before;
        result.After = after;
        result.Delta = after - before;
        result.Actual = after;

        if (after != target)
        {
            // D10：接受判定器对增量的改写（效果侧真实语义），但不静默——成功 + points-adjusted（附 requested/actual）。
            result.Warning = SandboxFailure.WarningPointsAdjusted;
        }
        else if (target > owner.PointSlots)
        {
            result.Warning = SandboxFailure.WarningPointsExceedSlots;
        }
    }

    private async Task HandleDrawAsync(SandboxPrimitiveView view, Context ctx, CancellationToken ct)
    {
        if (view.Result is not { } result)
        {
            return;
        }

        if (view.Owner is not { } owner || view.Amount is not { } count || count < 0)
        {
            Fail(result, SandboxFailure.InvalidArguments);
            return;
        }

        if (CheckState() is { } stateReason)
        {
            Fail(result, stateReason);
            return;
        }

        for (var i = 0; i < count; i++)
        {
            if (owner.Deck.Count == 0)
            {
                break; // 每张独立：已抽出的不回滚；首张即空＝由下方统一判定
            }

            var card = await _match.PlayerManager.DrawCard(owner, ct).ConfigureAwait(false);
            if (owner.Hand.Contains(card))
            {
                result.Drawn.Add(card.Id.ToString("N"));
            }
            else
            {
                result.BurnedRefs.Add(card.Id.ToString("N"));
                result.Burned = true;
            }
        }

        if (result.Drawn.Count == 0 && result.BurnedRefs.Count == 0)
        {
            Fail(result, SandboxFailure.DeckEmpty);
            return;
        }

        result.Ok = true;
        result.Reason = null;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 原语执行体（唯一落地段：只经引擎唯一写点）
    // ═════════════════════════════════════════════════════════════════════════

    private async Task RunInstantiateAsync(InstantiateRequest request, SandboxPrimitiveResult result, CancellationToken ct)
    {
        CardBase card;
        try
        {
            card = await _match.CardService.CreateAsync(request.DefinitionId, request.Owner, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            Fail(result, SandboxFailure.DefinitionUnknown); // 未注册定义：先于任何状态变更、无残留
            return;
        }
        catch (InvalidOperationException)
        {
            Fail(result, CheckState() ?? SandboxFailure.PlacementRejected);
            return;
        }

        switch (request.Zone)
        {
            case ZoneHand:
            {
                var placed = await _match.CardService.PlaceToHandAsync(card, request.Owner, ct).ConfigureAwait(false);
                if (placed.Status == CardPlaceStatus.Placed)
                {
                    Succeed(result, card, line: null, slotIndex: null);
                    return;
                }

                if (placed.Status == CardPlaceStatus.Burned)
                {
                    Succeed(result, card, line: null, slotIndex: null);
                    result.Burned = true; // 满手＝爆牌（成功语义）
                    return;
                }

                await _match.Engine.DestroyCard(card).ConfigureAwait(false);
                Fail(result, MapPlaceFailure(placed.FailureReason));
                return;
            }

            case ZoneDeckTop:
            {
                await _match.CardService.PlaceToDeckTopAsync(card, request.Owner, ct).ConfigureAwait(false);
                Succeed(result, card, line: null, slotIndex: null); // 静默（卡组操作本身无信号）
                return;
            }

            default:
            {
                if (card is not UnitCard unit)
                {
                    await _match.Engine.DestroyCard(card).ConfigureAwait(false);
                    Fail(result, SandboxFailure.NotAUnitCard);
                    return;
                }

                var slot = request.ResolvedSlot!;
                if (request.Zone == LineSupport)
                {
                    var placed = await _match.CardService
                        .PlaceToSupportLineAsync(unit, request.Owner, slot, ct).ConfigureAwait(false);
                    if (placed.Status == CardPlaceStatus.Placed)
                    {
                        Succeed(result, unit, request.Zone, slot.Index);
                        return;
                    }

                    await _match.Engine.DestroyCard(unit).ConfigureAwait(false);
                    Fail(result, MapPlaceFailure(placed.FailureReason));
                    return;
                }

                // 前线槽：JoinUnitAsync 是「加入链唯一写点」，结构上接受任意空槽（不经 ContainsSlot 组合校验）。
                var joined = await _match.PlayManager.JoinUnitAsync(unit, slot, ct).ConfigureAwait(false);
                if (joined.Status == PlayResultStatus.Success)
                {
                    Succeed(result, unit, request.Zone, slot.Index);
                    return;
                }

                await _match.Engine.DestroyCard(unit).ConfigureAwait(false);
                Fail(result, MapPlayFailure(joined.FailureReason));
                return;
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 参数解析（入口与 handler 共用：同一口径、同一原因）
    // ═════════════════════════════════════════════════════════════════════════

    private bool TryReadInstantiate(
        string? definitionId, Player? owner, string? zone, int? slot,
        out InstantiateRequest request, out string reason)
    {
        request = default;

        if (string.IsNullOrWhiteSpace(definitionId))
        {
            reason = SandboxFailure.InvalidArguments;
            return false;
        }

        if (owner is null)
        {
            reason = SandboxFailure.InvalidArguments;
            return false;
        }

        if (zone is not (ZoneHand or ZoneDeckTop or LineSupport or LineFrontline))
        {
            reason = SandboxFailure.InvalidArguments;
            return false;
        }

        var needsSlot = zone is LineSupport or LineFrontline;
        var index = slot ?? 0;
        if (needsSlot && slot is null)
        {
            reason = SandboxFailure.InvalidArguments;
            return false;
        }

        Slot? resolved = null;
        if (needsSlot && !TryResolveSlot(owner, zone!, index, out resolved, out reason))
        {
            return false;
        }

        request = new InstantiateRequest(definitionId!, owner, zone!, needsSlot ? index : 0,
            needsSlot ? resolved : null);
        reason = string.Empty;
        return true;
    }

    private bool TryReadTarget(Card? card, string? line, int? slot, out TargetRequest request, out string reason)
    {
        request = default;
        Slot? resolved = null;

        if (card is null || line is not (LineSupport or LineFrontline) || slot is not { } index)
        {
            reason = SandboxFailure.InvalidArguments;
            return false;
        }

        if (!TryResolveSlot(OwnerOf(card), line!, index, out resolved, out reason))
        {
            return false;
        }

        request = new TargetRequest(card, line!, index, resolved);
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 槽位解析（线名 ＋ 线内索引）：越界＝<c>slot-out-of-range</c>；解析结果不在该线内＝<c>slot-not-owned</c>。
    /// 风险 1 的应对＝在此承担线归属（引擎原子层不校验线归属）。
    /// </summary>
    private bool TryResolveSlot(Player? player, string line, int index, out Slot? slot, out string reason)
    {
        slot = null;

        if (player is null)
        {
            reason = SandboxFailure.SlotNotOwned;
            return false;
        }

        var battleLine = line == LineSupport
            ? _match.Battlefield.GetSupportLine(player)
            : _match.Battlefield.FrontLine;

        if (index < 0 || index >= battleLine.Count)
        {
            reason = SandboxFailure.SlotOutOfRange;
            return false;
        }

        var candidate = battleLine[index];
        for (var i = 0; i < battleLine.Count; i++)
        {
            if (ReferenceEquals(battleLine[i], candidate))
            {
                slot = candidate;
                reason = string.Empty;
                return true;
            }
        }

        reason = SandboxFailure.SlotNotOwned;
        return false;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 硬门槛（入口进链前检查：不满足＝不 Invoke、零副作用；handler 内再查一遍＝防竞争）
    // ═════════════════════════════════════════════════════════════════════════

    private string? CheckInstantiateGates(InstantiateRequest request)
    {
        if (CheckState() is { } stateReason)
        {
            return stateReason;
        }

        if (request.Zone is LineSupport or LineFrontline
            && request.ResolvedSlot is { IsEmpty: false })
        {
            return SandboxFailure.TargetSlotOccupied;
        }

        return null;
    }

    private string? CheckUnitizeGates(TargetRequest request)
    {
        if (CheckState() is { } stateReason)
        {
            return stateReason;
        }

        if (!request.Card.Life.IsAlive)
        {
            return SandboxFailure.EntityDestroyed;
        }

        if (request.Card is not UnitCard)
        {
            return SandboxFailure.NotAUnitCard;
        }

        if (request.Card.TryGetData<UnitStateData>(out _))
        {
            return SandboxFailure.UnitAlreadyUnitized;
        }

        if (request.ResolvedSlot is { IsEmpty: false })
        {
            return SandboxFailure.TargetSlotOccupied;
        }

        return null;
    }

    private string? CheckMoveGates(TargetRequest request)
    {
        if (CheckState() is { } stateReason)
        {
            return stateReason;
        }

        if (!request.Card.Life.IsAlive)
        {
            return SandboxFailure.EntityDestroyed;
        }

        if (request.Card is not UnitCard unit
            || !unit.TryGetData<UnitStateData>(out var state)
            || state.Position is null
            || state.IsDestroyed)
        {
            return SandboxFailure.NotOnBattlefield;
        }

        if (ReferenceEquals(state.Position, request.ResolvedSlot))
        {
            return SandboxFailure.SameSlot;
        }

        if (request.ResolvedSlot is { IsEmpty: false })
        {
            return SandboxFailure.TargetSlotOccupied;
        }

        return null;
    }

    private string? CheckRemoveGates(Card card)
    {
        if (CheckState() is { } stateReason)
        {
            return stateReason;
        }

        if (!card.Life.IsAlive)
        {
            return SandboxFailure.EntityDestroyed;
        }

        return null;
    }

    /// <summary>对局态门槛：结束＝<c>game-ended</c>；非"进行"＝<c>phase-blocked</c>。</summary>
    private string? CheckState()
    {
        if (_match.State == MatchState.Ended)
        {
            return SandboxFailure.GameEnded;
        }

        return _match.State == MatchState.InProgress ? null : SandboxFailure.PhaseBlocked;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 结局映射与结果写入
    // ═════════════════════════════════════════════════════════════════════════

    private static string MapPlaceFailure(CardPlaceFailureReason? reason) => reason switch
    {
        CardPlaceFailureReason.TargetSlotOccupied => SandboxFailure.TargetSlotOccupied,
        CardPlaceFailureReason.UnitAlreadyUnitized => SandboxFailure.UnitAlreadyUnitized,
        CardPlaceFailureReason.CategoryMismatch => SandboxFailure.NotAUnitCard,
        _ => SandboxFailure.PlacementRejected,
    };

    private static string MapPlayFailure(PlayFailureReason? reason) => reason switch
    {
        PlayFailureReason.TargetSlotOccupied => SandboxFailure.TargetSlotOccupied,
        PlayFailureReason.UnitAlreadyUnitized => SandboxFailure.UnitAlreadyUnitized,
        PlayFailureReason.GameEnded => SandboxFailure.GameEnded,
        PlayFailureReason.PhaseBlocked => SandboxFailure.PhaseBlocked,
        _ => SandboxFailure.PlacementRejected,
    };

    private static string MapMoveFailure(UnitCard unit, Slot destination)
    {
        if (!unit.TryGetData<UnitStateData>(out var state) || state.IsDestroyed)
        {
            return SandboxFailure.EntityDestroyed;
        }

        if (state.Position is not { } current)
        {
            return SandboxFailure.NotOnBattlefield;
        }

        if (ReferenceEquals(current, destination))
        {
            return SandboxFailure.SameSlot;
        }

        return destination.IsEmpty ? SandboxFailure.PlacementRejected : SandboxFailure.TargetSlotOccupied;
    }

    private static void Succeed(SandboxPrimitiveResult result, Card card, string? line, int? slotIndex)
    {
        result.Ok = true;
        result.Reason = null;
        result.Ref = card.Id.ToString("N");
        result.Line = line;
        result.Slot = slotIndex;
    }

    private static void Fail(SandboxPrimitiveResult result, string reason)
    {
        result.Ok = false;
        result.Reason = reason;
    }

    private static string RefOf(Card? card) => card is null ? "-" : card.Id.ToString("N");

    /// <summary>归属玩家（引擎 <see cref="Card"/> 基类不带归属，经 <see cref="CardBase"/> 读取；未加载＝null）。</summary>
    private static Player? OwnerOf(Card? card) => (card as CardBase)?.Owner;

    /// <summary>入口拒绝（授权／参数／硬门槛）：零状态变更，照样留一条审计痕。</summary>
    private SandboxPrimitiveResult Reject(string primitive, string reason, string summary)
    {
        WriteAudit(primitive, $"{summary} → rejected({reason})");
        return SandboxPrimitiveResult.Fail(reason);
    }

    private static string Describe(SandboxPrimitiveResult result)
        => result.Ok
            ? $"ok(ref={result.Ref ?? "-"}, line={result.Line ?? "-"}, slot={result.Slot?.ToString() ?? "-"}"
              + $"{(result.Burned ? ", burned" : string.Empty)}{(result.Warning is null ? string.Empty : $", warning={result.Warning}")})"
            : $"rejected({result.Reason})";

    /// <summary>
    /// 审计留痕（R4 D3）：每次原语调用恰一条 Log 级条目——只进日志、不进总线（不用 <c>Bus.Emit</c>）、
    /// 不 <c>BeginAction</c>（不产段）。写当前执行者流语义＝原语入口是顶层调用 ⇒ 总流（与 Bus.WriteTrace 同口径）。
    /// </summary>
    private void WriteAudit(string primitive, string message)
        => _match.Engine.RootStream.WriteLog(
            $"sandbox.{primitive}",
            message,
            LogLevel.Info,
            new[] { "sandbox-primitive", primitive });

    // ═════════════════════════════════════════════════════════════════════════
    // 装配：注册唯一内置 handler → 立即封闭
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 「注册唯一内置 handler → 封闭」的一次性装配单元：handler 为 null（装配性错误）＝ 调用期即抛、
    /// 实例不产出；注册成功后再 <see cref="Trigger{TView}.Seal"/>，不存在可注册窗口。
    /// </summary>
    /// <exception cref="ArgumentNullException">handler 为 null（handler 集不齐 ⇒ fail-fast）。</exception>
    internal static Trigger<SandboxPrimitiveView> CreateSealedTrigger(
        string triggerName,
        string eventName,
        Func<SandboxPrimitiveView, Context, CancellationToken, Task>? handler,
        out TriggerRegistration registration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(handler, $"保留触发器 '{triggerName}' 缺少内置 handler（装配性错误——实例不产出）。");

        var trigger = new Trigger<SandboxPrimitiveView>(triggerName);
        registration = trigger.Register(eventName, handler);
        trigger.Seal();
        return trigger;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 诊断面与内部读面
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>授权事实（只读；诊断面导出用）。</summary>
    internal bool IsAuthorized => _authorized;

    /// <summary>六个保留触发器（诊断面导出用；顺序＝装配序）。</summary>
    internal IReadOnlyList<(string Name, Trigger<SandboxPrimitiveView> Trigger)> ReservedTriggers => new[]
    {
        ("sandbox.instantiate", _instantiate),
        ("sandbox.unitize", _unitize),
        ("sandbox.move", _move),
        ("sandbox.remove", _remove),
        ("sandbox.points", _setPoints),
        ("sandbox.draw", _draw),
    };

    /// <summary>
    /// 保留触发器导出（诊断面；只读）：<c>[{ name, sealed, authorized, handlers: [名…] }]</c>。
    /// 可观测、不可写——导出不提供任何注册或触发通道。
    /// </summary>
    public string ExportReservedTriggersJson()
    {
        var items = new List<object>(6);
        foreach (var (name, trigger) in ReservedTriggers)
        {
            var handlers = new List<string>();
            foreach (var entry in trigger.Events)
            {
                handlers.Add(entry.Name);
            }

            items.Add(new
            {
                name,
                @sealed = trigger.IsSealed,
                authorized = _authorized,
                handlers,
            });
        }

        return System.Text.Json.JsonSerializer.Serialize(items);
    }

    // ---------- 请求载体 ----------

    private readonly record struct InstantiateRequest(
        string DefinitionId, Player Owner, string Zone, int SlotIndex, Slot? ResolvedSlot);

    private readonly record struct TargetRequest(Card Card, string Line, int SlotIndex, Slot? ResolvedSlot);
}
