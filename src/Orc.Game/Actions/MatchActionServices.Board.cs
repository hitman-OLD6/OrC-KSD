using Orc.Cards;
using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Players;

namespace Orc.Game.Actions;

/// <summary>
/// 动作服务面的「有序列表模型」段（partial）：移动落地、加入落地、落位解析、前线门禁。
/// <para>全部只经战场/环境只读面与卡侧单源入口，不持有管理器实例。</para>
/// </summary>
internal sealed partial class MatchActionServices
{
    /// <inheritdoc />
    public async Task<bool> LandMoveAsync(UnitCard unit, FormationGap destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(destination);

        if (!unit.TryGetData<UnitStateData>(out var state) || state.IsDestroyed)
        {
            return false;
        }

        if (state.Line is not { } oldLine)
        {
            return false;
        }

        var oldIndex = oldLine.IndexOf(unit);
        if (oldIndex < 0 || !destination.IsCurrent)
        {
            return false;
        }

        var targetLine = destination.Line;
        var ordinal = destination.Ordinal;
        var oldPosition = new BoardPosition(oldLine, oldIndex);

        // 同一线内移动：移除后插入点右侧元素序号 -1，需按移除后语义校正。
        var insertAt = ReferenceEquals(targetLine, oldLine) && ordinal > oldIndex ? ordinal - 1 : ordinal;

        if (!oldLine.TryRemove(unit))
        {
            return false;
        }

        if (!targetLine.TryInsertAt(insertAt, unit))
        {
            oldLine.TryInsertAt(oldIndex, unit); // 回滚（不留矛盾中间态）
            return false;
        }

        state.Line = targetLine;
        await GameUpdates.EmitUnitPositionChanged(
            _engine, unit, oldPosition, new BoardPosition(targetLine, insertAt), ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> UnitizeOnJoinAsync(
        UnitCard card, FormationGap gap, Card? triggerCard, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(gap);

        // 结构不变量（矛盾中间态防护）：空隙须为当前快照、未单位化的卡可单位化——违反＝false、零落地。
        if (!gap.IsCurrent || card.TryGetData<UnitStateData>(out _))
        {
            return false;
        }

        // 单位化的唯一执行面（卡侧单源入口）：加单位组件＋指挥组件＋按空隙插入＋跑链一次。
        await card.InvokeUnitizeAsync(gap, ct).ConfigureAwait(false);
        if (!card.TryGetData<UnitStateData>(out _))
        {
            return false; // 单位化未完成（结构性拒绝）：不发射 card.placed
        }

        await GameUpdates.EmitCardPlaced(_engine, card, ct, triggerCard ?? card).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public Task InvokeDeployKeywordAsync(
        UnitCard card, Player? player, FormationGap gap, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(gap);
        return card.InvokeDeployKeywordAsync(player, gap, ct);
    }

    /// <inheritdoc />
    public FormationGap? ResolveJoinGap(UnitCard card, string zone, int? index, out JoinPlacementFailure failure)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentException.ThrowIfNullOrWhiteSpace(zone);
        failure = JoinPlacementFailure.None;

        if (card.TryGetData<UnitStateData>(out _))
        {
            failure = JoinPlacementFailure.AlreadyOnBoard;
            return null;
        }

        var line = ResolveTargetLine(card, zone);
        var gaps = line?.GetInsertionGaps();
        if (gaps is null || gaps.Count == 0)
        {
            failure = JoinPlacementFailure.LineFull;
            return null;
        }

        // 优先级 ①：显式序号（插入后的位置）。
        if (index is { } ordinal)
        {
            if (ordinal < 0 || ordinal >= gaps.Count)
            {
                failure = JoinPlacementFailure.IndexOutOfRange;
                return null;
            }

            return gaps[ordinal];
        }

        // 优先级 ②：守护 → HQ 右侧（仅支援线；HQ 不在一线之外）。
        if (IsGuard(card) && line is not null && ReferenceEquals(line, SupportLineOf(card)))
        {
            var hqIndex = Battlefield.IndexOfHq(line);
            if (hqIndex >= 0)
            {
                var right = hqIndex + 1;
                if (right < gaps.Count)
                {
                    return gaps[right];
                }

                if (hqIndex < gaps.Count)
                {
                    return gaps[hqIndex]; // 兜底：HQ 已在最右，落其左侧
                }
            }
        }

        // 优先级 ③：默认最左。
        return gaps[0];
    }

    /// <inheritdoc />
    public bool EnemyHoldsFrontline(Player? owner)
    {
        if (BattlefieldOf(owner) is not { } battlefield || owner is null)
        {
            return false;
        }

        foreach (var member in battlefield.FrontLine.Members)
        {
            if (member is UnitCard unit
                && unit.TryGetData<UnitStateData>(out var state)
                && !state.IsDestroyed
                && !ReferenceEquals(unit.Owner, owner))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>落位目标线解析（zone＝frontline／support）。</summary>
    private BattleLine? ResolveTargetLine(UnitCard card, string zone) => zone switch
    {
        "frontline" => BattlefieldOf(card.Owner)?.FrontLine,
        "support" => SupportLineOf(card),
        _ => null,
    };

    /// <summary>单位归属方的支援线（无归属/无战场＝null）。</summary>
    private static BattleLine? SupportLineOf(UnitCard card)
        => card.Owner is { } owner ? BattlefieldOf(owner)?.GetSupportLine(owner) : null;

    /// <summary>玩家所属对局的战场（未装配＝null）。</summary>
    private static Battlefield? BattlefieldOf(Player? player)
        => player?.Environment?.Battlefield;

    /// <summary>守护判定（双通道：代码标记／「守护」词条）。</summary>
    private static bool IsGuard(UnitCard card)
        => card.Definition.IsGuard || KeywordRules.HasKeyword(card, KeywordIds.Guard);
}
