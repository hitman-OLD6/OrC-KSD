using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Players;
using Orc.Game.Ui;
using Orc.UI.Contracts;

namespace Orc.UI;

// ─────────────────────────────────────────────────────────────────────────────
// 投影提供者实现（S6·D2/D6）：由前端 `OrcCardReader` + `OrcMatchReader` 的投影逻辑迁入（形状逐字保持）。
// 只读引擎查询面；**不推断规则**（可用性/数值取自引擎）。隐藏信息边界：对手手牌只降为数，不产卡视图。
// 说明：`MatchId` 由前端在**构造 provider 时**传入（引擎侧无对局 id 概念）；美术与（可选）丰富定义亦由前端注入。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>投影提供者工厂（S6·P6f）：前端以 matchId ＋ 美术解析器（可选丰富定义解析器）构造实例并自持。</summary>
public sealed class UiProjectionProviderFactory : IUiProjectionProviderFactory
{
    /// <inheritdoc />
    public IUiProjectionProvider Create(
        Func<string, string>? artLookup,
        string matchId,
        Func<string, UiCardDefinition?>? definitionLookup = null)
        => new UiProjectionProvider(artLookup, matchId, definitionLookup);
}

/// <summary>
/// 整盘投影提供者（S7·D2）：把活 <c>Match</c> 按单一视角投影为 <see cref="UiMatchView"/>。
/// **每帧调用**；抛错由调用方按运行期隔离处理（保留上一帧 + 记录错误）。
/// </summary>
public sealed class UiProjectionProvider : IUiProjectionProvider
{
    private readonly Func<string, string>? _artLookup;
    private readonly string _matchId;
    private readonly Func<string, UiCardDefinition?>? _definitionLookup;

    /// <summary>创建提供者。</summary>
    /// <param name="artLookup">卡 id → 美术路径（引擎无美术概念；前端自持该表）。</param>
    /// <param name="matchId">对局 id（前端自持；投影的 <c>MatchId</c> 字段）。</param>
    /// <param name="definitionLookup">卡 id → 前端丰富定义（可空＝按引擎定义构造）。</param>
    public UiProjectionProvider(
        Func<string, string>? artLookup = null,
        string matchId = "",
        Func<string, UiCardDefinition?>? definitionLookup = null)
    {
        _artLookup = artLookup;
        _matchId = matchId ?? "";
        _definitionLookup = definitionLookup;
    }

    /// <summary>
    /// **单卡投影**（S8）：给"不在整盘视图中"的卡（打出的指令、被弃/爆掉的牌等）取一枚卡视图。
    /// 表现层（段→演出）消费；与 <see cref="Project"/> 同源，不另造第二套卡读面。
    /// </summary>
    /// <exception cref="ArgumentNullException">match／card／viewer 为 null。</exception>
    public UiCardView ReadCard(Match match, Card card, Player viewer, string zone, int slotIndex)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(viewer);
        return Read(match, card, viewer, zone, slotIndex);
    }

    /// <inheritdoc />
    public UiMatchView Project(Match match, Player viewer)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(viewer);

        var opponent = OpponentOf(match, viewer);
        var phase = match.Phase switch
        {
            MatchPhase.Mulligan => "mulligan",
            MatchPhase.Play => "play",
            _ => "over",
        };

        var index = new Dictionary<string, Card>();
        var hand = new List<UiCardView>();
        for (var i = 0; i < viewer.Hand.Count; i++)
        {
            if (viewer.Hand[i] is CardBase card)
            {
                hand.Add(Read(match, card, viewer, "hand", i));
                index[KeyOf(card)] = card;
            }
        }

        var selfLine = Row(match, viewer, viewer, front: false, index)
            .Concat(Row(match, viewer, viewer, front: true, index)).ToArray();
        var enemyLine = opponent is null
            ? Array.Empty<UiCardView>()
            : Row(match, opponent, viewer, front: false, index)
                .Concat(Row(match, opponent, viewer, front: true, index)).ToArray();
        if (opponent is not null)
        {
            _ = HqOf(match, opponent, viewer, index);
        }

        var selfHq = HqOf(match, viewer, viewer, index);

        return new UiMatchView
        {
            MatchId = _matchId,
            Turn = TurnOf(match),
            SelfPlayerName = "我方",
            EnemyPlayerName = "对手",
            Phase = phase,
            ResultTitle = ResultTitle(match, viewer),
            ResultReason = ResultReasonOf(match),
            Outcome = OutcomeOf(match, viewer),
            ConcedingSide = ConcedingSideOf(match, viewer),
            FinalTurn = match.Winner is null ? null : TurnOf(match),
            ActivePlayerSide = ActiveSideOf(match, viewer, phase),
            SelfKredits = viewer.Points,
            SelfMaxKredits = viewer.PointSlots,
            SelfHandCount = viewer.Hand.Count,
            SelfDeckCount = viewer.Deck.Count,
            EnemyKredits = opponent?.Points,
            EnemyMaxKredits = opponent?.PointSlots,
            EnemyHandCount = opponent?.Hand.Count,
            EnemyDeckCount = opponent?.Deck.Count,
            SelfCounterCount = viewer.Hand.Count(h => h is CardBase c
                && c.TryGetData<CounterActivationData>(out var a) && a.IsActive),
            FrontLineSlotCount = match.Battlefield.FrontLine.Capacity,
            SupportLineSlotCount = match.Battlefield.GetSupportLine(viewer.Index).Capacity,
            SelfHand = hand,
            SelfLine = selfLine,
            SelfHq = selfHq,
            EnemyLine = enemyLine,
            EnemyHq = opponent is null ? null : HqOf(match, opponent, viewer, new Dictionary<string, Card>()),
        };
    }

    // ---------------------------------------------------------------- 卡 → 视图

    private UiCardView Read(Match match, Card card, Player viewer, string zone, int slotIndex)
    {
        var typed = card as CardBase;
        var hq = card as Hq;
        var definition = hq is not null ? HqDefinition(hq) : ReadDefinition(match, typed!);
        var owner = hq?.Owner ?? typed?.Owner;
        var side = owner is null || owner.Index == viewer.Index ? "self" : "enemy";
        var unitized = card.TryGetData<UnitStateData>(out var state);

        var attack = hq is not null ? null : unitized ? Effective(typed!, CardStatFields.Attack) : definition.BaseAttack;
        var defense = hq is not null ? hq.Health : unitized ? Effective(typed!, CardStatFields.Defense) : definition.BaseDefense;
        var opCost = hq is not null ? null : unitized ? Effective(typed!, CardStatFields.OperateCost) : definition.BaseOpCost;
        var keywords = Keywords(card);
        var present = KeywordRegistry.Registered.Where(keywords.Has).ToArray();

        return new UiCardView
        {
            Uid = KeyOf(card),
            Definition = definition,
            EffectiveCost = hq is not null ? null : Effective(typed!, CardStatFields.DeployCost) ?? definition.Cost,
            EffectiveAttack = attack,
            EffectiveDefense = defense,
            EffectiveOpCost = opCost,
            Health = hq?.Health ?? (unitized && state is not null ? state.Defense : definition.BaseDefense),
            Zone = zone,
            SlotIndex = slotIndex,
            OwnerSide = side,
            Visibility = Visibility.Full,
            IsHq = hq is not null,
            CanPlayCard = false, // 由 UiBattleActions 提供；本投影不判定合法性
            CanAttack = unitized && card.TryGetData<CommandData>(out var command) && command.CanAttack,
            CanMoveAndAttack = unitized && card.TryGetData<CommandData>(out var move) && move.CanMove,
            CanBeTargeted = side == "enemy",
            IsSuppressed = keywords.Has(KeywordIds.Suppressed),
            IsSilenced = keywords.Has(KeywordIds.Inhibited),
            IsVeteran = VeteranRules.IsVeteran(card),
            IsCovert = CovertRules.IsCovert(card),
            UnitTypes = unitized && state is not null
                ? state.UnitTypes.Select(t => t.ToLowerInvariant()).ToArray()
                : typed?.Definition.UnitTypes.Select(t => t.ToLowerInvariant()).ToArray() ?? [],
            Keywords = present.Select(UiKeyword).ToArray(),
            KeywordValues = present.Where(id => keywords.GetValue(id).HasValue)
                .ToDictionary(UiKeyword, id => keywords.GetValue(id)!.Value, StringComparer.Ordinal),
            IsCounterArmed = card.TryGetData<CounterActivationData>(out var armed) && armed.IsActive,
        };
    }

    private UiCardDefinition ReadDefinition(Match match, CardBase card)
    {
        var definition = card.Definition;
        var cardId = match.CardLibrary.TryGetRegisteredId(definition, out var registered)
            ? registered
            : definition.Name;
        // 定义投影与"静态卡定义投影"同源（单一实现，避免两处漂移）。
        return StaticCardProjection.Project(cardId, definition, _artLookup, _definitionLookup);
    }

    private static UiCardDefinition HqDefinition(Hq hq) => new()
    {
        CardId = "hq",
        Name = hq.Name,
        CardType = "hq",
        UnitType = "",
        Set = "",
        Rarity = "common",
        ArtPath = "",
        Cost = null,
        BaseAttack = null,
        BaseDefense = hq.Health,
        BaseOpCost = null,
        IsToken = false,
    };

    // ---------------------------------------------------------------- 盘面

    private UiCardView HqOf(Match match, Player owner, Player viewer, Dictionary<string, Card> index)
    {
        var hqLine = owner.Hq.Line;
        index[KeyOf(owner.Hq)] = owner.Hq;
        return Read(match, owner.Hq, viewer, "support", hqLine?.IndexOf(owner.Hq) ?? 0);
    }

    private IEnumerable<UiCardView> Row(
        Match match, Player owner, Player viewer, bool front, Dictionary<string, Card> index)
    {
        var battlefield = match.Battlefield;
        var line = front ? battlefield.FrontLine : battlefield.GetSupportLine(owner.Index);
        for (var i = 0; i < line.Size; i++)
        {
            if (line[i] is UnitCard unit && ReferenceEquals(unit.Owner, owner))
            {
                index[KeyOf(unit)] = unit;
                yield return Read(match, unit, viewer, front ? "frontline" : "support", i);
            }
        }
    }

    private static Player? OpponentOf(Match match, Player viewer)
        => match.Players.FirstOrDefault(p => p.Index != viewer.Index);

    private static string ActiveSideOf(Match match, Player viewer, string phase)
    {
        if (phase != "play")
        {
            return "self";
        }

        try
        {
            return ReferenceEquals(match.CurrentPlayer, viewer) ? "self" : "enemy";
        }
        catch (InvalidOperationException)
        {
            return "self";
        }
    }

    private static int TurnOf(Match match)
    {
        try
        {
            return match.TurnNumber;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private static string? ResultTitle(Match match, Player viewer)
    {
        var winner = match.Winner;
        return winner is null ? null : ReferenceEquals(winner, viewer) ? "胜利" : "失败";
    }

    /// <summary>结构化结果：未终局＝None；终局＝按 Winner 与视角比较。</summary>
    private static UiMatchOutcome OutcomeOf(Match match, Player viewer)
        => match.Winner is null
            ? UiMatchOutcome.None
            : ReferenceEquals(match.Winner, viewer) ? UiMatchOutcome.Win : UiMatchOutcome.Lose;

    /// <summary>认输方：仅当终局原因为认输时给出；非认输终局＝None。</summary>
    private static UiConcedingSide ConcedingSideOf(Match match, Player viewer)
    {
        if (match.Winner is null)
        {
            return UiConcedingSide.None;
        }

        MatchEndReason? reason;
        try
        {
            reason = match.EndReason;
        }
        catch (InvalidOperationException)
        {
            return UiConcedingSide.None;
        }

        if (reason != MatchEndReason.Concede)
        {
            return UiConcedingSide.None;
        }

        return ReferenceEquals(match.Winner, viewer) ? UiConcedingSide.Enemy : UiConcedingSide.Self;
    }

    private static string? ResultReasonOf(Match match)
    {
        try
        {
            return match.EndReason?.ToString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- 词条读面

    private static KeywordManager Keywords(Card card)
        => card is CardBase typed ? typed.Keywords : ((Hq)card).Keywords;

    private static string UiKeyword(string id) => id switch
    {
        KeywordIds.Blitz => "blitz",
        KeywordIds.Fury => "fury",
        KeywordIds.SmokeScreen => "smokescreen",
        KeywordIds.Ambush => "ambush",
        KeywordIds.Mobilize => "mobilize",
        KeywordIds.Pincer => "pincer",
        KeywordIds.Armor => "armor",
        KeywordIds.Guard => "guard",
        KeywordIds.Veteran => "veteran",
        KeywordIds.Covert => "covert",
        KeywordIds.Suppressed => "suppressed",
        KeywordIds.Inhibited => "inhibited",
        _ => id,
    };

    private static int? Effective(CardBase card, string field)
    {
        try
        {
            var value = card.Modifiers.GetEffectiveValue(field);
            // 行动费下限 0（与 OperateCosts.Effective 同口径）：减费修饰不得把面板显示成负数。
            return string.Equals(field, CardStatFields.OperateCost, StringComparison.Ordinal)
                ? Math.Max(0, value)
                : value;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>稳定 uid＝引擎实体 Id（N 格式）。</summary>
    internal static string KeyOf(Entity entity) => entity.Id.ToString("N");

    private static string KeyOf(Card card) => KeyOf((Entity)card);
}
