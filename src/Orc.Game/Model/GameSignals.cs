using Orc.Core.Model;

namespace Orc.Game.Model;

/// <summary>
/// 游戏层信号声明（S3 第 7 条）：把 <see cref="GameUpdates"/> 的 35 个更新字符串声明为**引擎实体的成员触发器**，
/// 由游戏层预制体 <c>engine.game</c>（<c>extends: engine</c>）追加到同一个 <c>engine</c> 根下。
/// <para>
/// 成员名＝更新字符串原样（零转换）：<c>hooks: ["unit.position.changed"]</c> → <c>engine#unit.position.changed</c>。
/// </para>
/// <para>
/// **载荷视图**按 <see cref="GameUpdates"/> 的载荷契约声明，字段一律**可选**（同一种信号在不同路径上允许缺省某些键，
/// 如 <c>card.died</c> 的 <c>Killer</c> 可为 null／缺失）。视图字段名即"订阅者视图可用的字段名"（R1c 包含校验的依据）。
/// </para>
/// </summary>
public static class GameSignals
{
    /// <summary>载荷键：原因（<c>card.leaving</c> / <c>counter.disarmed</c>；值＝string）。</summary>
    public const string PayloadReason = "Reason";

    /// <summary>游戏层 35 个信号名（声明序＝<see cref="GameUpdates"/> 定义序；与预制体成员集合同源，守护测试锁漂移）。</summary>
    public static IReadOnlyList<string> Names { get; } = new[]
    {
        GameUpdates.TurnStartBefore,
        GameUpdates.TurnStart,
        GameUpdates.TurnStartAfter,
        GameUpdates.TurnEndBefore,
        GameUpdates.TurnEnd,
        GameUpdates.CardPlayed,
        GameUpdates.CardDrawn,
        GameUpdates.CardStatChanged,
        GameUpdates.CardLoad,
        GameUpdates.CardHandAdd,
        GameUpdates.CardDiscarded,
        GameUpdates.CardBurned,
        GameUpdates.CardDied,
        GameUpdates.CardDying,
        GameUpdates.CardLeaving,
        GameUpdates.KeywordAttackFinished,
        GameUpdates.UnitJoined,
        GameUpdates.UnitDeployed,
        GameUpdates.UnitPositionChanged,
        GameUpdates.UnitUpgraded,
        GameUpdates.UnitRevealed,
        GameUpdates.CardDamaged,
        GameUpdates.UnitActed,
        GameUpdates.UnitDamageDealt,
        GameUpdates.CounterTriggered,
        GameUpdates.CounterDisarmed,
        GameUpdates.UnitCombatSurvived,
        GameUpdates.DeckShuffled,
        GameUpdates.UnitTypesChanged,
        GameUpdates.SlotGained,
        GameUpdates.SlotLost,
        GameUpdates.SlotChanged,
        GameUpdates.PointGained,
        GameUpdates.PointLost,
        GameUpdates.PointChanged,
    };

    /// <summary>游戏层 35 个信号成员（声明序，来源层＝游戏）。</summary>
    public static IReadOnlyList<MemberDefinition> Members { get; } = Build();

    private static MemberDefinition[] Build()
    {
        var card = (GameUpdates.PayloadCard, ViewFieldType.Entity);
        var player = (GameUpdates.PayloadPlayer, ViewFieldType.Entity);
        var source = (GameUpdates.PayloadSource, ViewFieldType.Entity);
        var unit = (GameUpdates.PayloadUnit, ViewFieldType.Entity);
        var amount = (GameUpdates.PayloadAmount, ViewFieldType.Number);

        return new[]
        {
            // ---------- 回合五连 ----------
            Game(GameUpdates.TurnStartBefore, (GameUpdates.PayloadTurnNumber, ViewFieldType.Number), player, source),
            Game(GameUpdates.TurnStart, (GameUpdates.PayloadTurnNumber, ViewFieldType.Number), player, source),
            Game(GameUpdates.TurnStartAfter, (GameUpdates.PayloadTurnNumber, ViewFieldType.Number), player, source),
            Game(GameUpdates.TurnEndBefore, (GameUpdates.PayloadTurnNumber, ViewFieldType.Number), player, source),
            Game(GameUpdates.TurnEnd, (GameUpdates.PayloadTurnNumber, ViewFieldType.Number), player, source),

            // ---------- 通用三项＋加载/手牌 ----------
            Game(GameUpdates.CardPlayed, card, player, source),
            Game(GameUpdates.CardDrawn, card, player, source),
            Game(GameUpdates.CardStatChanged, card, (GameUpdates.PayloadChangedFields, ViewFieldType.StringArray), source),
            // card.load＝加载期信号：active-when: placed 的组件挂上来＝装载期拒绝（S3 第 4c 条）。
            SignalMember.Create(
                GameUpdates.CardLoad,
                SignalTiming.Load,
                SignalMember.Payload(card, player, source),
                PrefabLayer.Game),
            Game(GameUpdates.CardHandAdd, card, player, source),
            Game(GameUpdates.CardDiscarded, card, player, source),
            Game(GameUpdates.CardBurned, card, player, source),

            // ---------- 死亡/离场 ----------
            Game(GameUpdates.CardDied, card, (GameUpdates.PayloadKiller, ViewFieldType.Entity), source),
            Game(GameUpdates.CardDying, card, (GameUpdates.PayloadKiller, ViewFieldType.Entity), source),
            Game(GameUpdates.CardLeaving, card, (PayloadReason, ViewFieldType.String), source),
            Game(GameUpdates.KeywordAttackFinished, card, source),

            // ---------- 单位系列 ----------
            Game(GameUpdates.UnitJoined, unit, (GameUpdates.PayloadPosition, ViewFieldType.Entity), source),
            Game(GameUpdates.UnitDeployed, unit, (GameUpdates.PayloadPosition, ViewFieldType.Entity), source),
            Game(GameUpdates.UnitPositionChanged,
                unit,
                (GameUpdates.PayloadOldPosition, ViewFieldType.Entity),
                (GameUpdates.PayloadNewPosition, ViewFieldType.Entity),
                source),
            Game(GameUpdates.UnitUpgraded, unit, source),
            Game(GameUpdates.UnitRevealed, unit, source),
            Game(GameUpdates.UnitActed, unit, source),
            Game(GameUpdates.UnitCombatSurvived, unit, source),
            Game(GameUpdates.UnitTypesChanged, unit, (GameUpdates.PayloadAddedType, ViewFieldType.String), source),
            Game(GameUpdates.UnitDamageDealt, unit, card, amount, source),

            // ---------- 伤害/反制 ----------
            Game(GameUpdates.CardDamaged, card, amount, source),
            Game(GameUpdates.CounterTriggered, card, player, source),
            Game(GameUpdates.CounterDisarmed, card, player, (PayloadReason, ViewFieldType.String), source),

            // ---------- 卡组/资源 ----------
            Game(GameUpdates.DeckShuffled, player, (GameUpdates.PayloadDeck, ViewFieldType.Entity), source),
            Game(GameUpdates.SlotGained, player, amount, source),
            Game(GameUpdates.SlotLost, player, amount, source),
            Game(GameUpdates.SlotChanged,
                player,
                (GameUpdates.PayloadOldSlots, ViewFieldType.Number),
                (GameUpdates.PayloadNewSlots, ViewFieldType.Number),
                source),
            Game(GameUpdates.PointGained, player, amount, source),
            Game(GameUpdates.PointLost, player, amount, source),
            Game(GameUpdates.PointChanged,
                player,
                (GameUpdates.PayloadOldPoints, ViewFieldType.Number),
                (GameUpdates.PayloadNewPoints, ViewFieldType.Number),
                source),
        };
    }

    private static MemberDefinition Game(string name, params (string Name, ViewFieldType Type)[] fields)
        => SignalMember.Create(name, SignalTiming.Any, SignalMember.Payload(fields), PrefabLayer.Game);
}
