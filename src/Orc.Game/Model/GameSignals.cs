using Orc.Core.Model;

namespace Orc.Game.Model;

/// <summary>
/// 游戏层信号声明（S3 第 7 条）：把 <see cref="GameUpdates"/> 的 36 个更新字符串声明为**引擎实体的成员触发器**，
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

    /// <summary>游戏层 36 个信号名（声明序＝<see cref="GameUpdates"/> 定义序；与预制体成员集合同源，守护测试锁漂移）。</summary>
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
        GameUpdates.UnitAttacked,
    };

    /// <summary>游戏层 36 个信号成员（声明序，来源层＝游戏）。</summary>
    public static IReadOnlyList<MemberDefinition> Members { get; } = Build();

    /// <summary>
    /// 游戏层 36 条信号的**中文名**（R35／D25：文案真源在信号定义处）。
    /// <para>
    /// 覆盖率守护逐层各一条：<c>GameSignals.Names ≡ GameSignals.Labels.Keys</c>（与内核那条**互不合并**，D26）。
    /// 模板库的 <c>label</c> 由生成器从「信号中文名 ＋ guard 中文名」派生（如「部署时（本单位）」），人不手写。
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> Labels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [GameUpdates.TurnStartBefore] = "回合开始前",
        [GameUpdates.TurnStart] = "回合开始时",
        [GameUpdates.TurnStartAfter] = "回合开始后",
        [GameUpdates.TurnEndBefore] = "回合结束前",
        [GameUpdates.TurnEnd] = "回合结束时",
        [GameUpdates.CardPlayed] = "打出时",
        [GameUpdates.CardDrawn] = "抽到时",
        [GameUpdates.CardStatChanged] = "数值变更时",
        [GameUpdates.CardLoad] = "加载时",
        [GameUpdates.CardHandAdd] = "加入手牌时",
        [GameUpdates.CardDiscarded] = "弃置时",
        [GameUpdates.CardBurned] = "爆牌时",
        [GameUpdates.CardDied] = "死亡时",
        [GameUpdates.CardDying] = "死亡确认时",
        [GameUpdates.CardLeaving] = "非死亡离场时",
        [GameUpdates.KeywordAttackFinished] = "词条攻击收尾时",
        [GameUpdates.UnitJoined] = "单位加入时",
        [GameUpdates.UnitDeployed] = "部署时",
        [GameUpdates.UnitPositionChanged] = "位置变化时",
        [GameUpdates.UnitUpgraded] = "升为老兵时",
        [GameUpdates.UnitRevealed] = "揭示时",
        [GameUpdates.CardDamaged] = "受到伤害时",
        [GameUpdates.UnitActed] = "行动后",
        [GameUpdates.UnitDamageDealt] = "造成伤害时",
        [GameUpdates.CounterTriggered] = "反制触发时",
        [GameUpdates.CounterDisarmed] = "反制解除时",
        [GameUpdates.UnitCombatSurvived] = "战斗存活时",
        [GameUpdates.DeckShuffled] = "洗牌时",
        [GameUpdates.UnitTypesChanged] = "类型变更时",
        [GameUpdates.SlotGained] = "获得槽位时",
        [GameUpdates.SlotLost] = "失去槽位时",
        [GameUpdates.SlotChanged] = "槽位变化时",
        [GameUpdates.PointGained] = "获得点数时",
        [GameUpdates.PointLost] = "失去点数时",
        [GameUpdates.PointChanged] = "点数变化时",
        [GameUpdates.UnitAttacked] = "攻击后",
    };

    /// <summary>
    /// 语义边界说明（R35／D25；可选）——模板的 <c>description</c> **直接取本表**，生成器不另写一份。
    /// <para>
    /// <c>card.leaving</c> 的「非死亡」必须写在这里（R29 第 3 条）：作者看到模板文案就知道
    /// 「单位被打死时不触发」，不必去读引擎实现。
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> Notes { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [GameUpdates.CardLeaving] =
            "非死亡离场：单位被移除、回手或撤退回手时触发；**死亡不触发**——死亡走 card.dying／card.died，三者互斥不重叠。",
        [GameUpdates.CardDied] = "死亡完成（战斗数值链判定后）。要覆盖「被打死」的场景请用本档，不要用非死亡离场档。",
        [GameUpdates.CardDying] = "死亡已确认、效果卸载前的宿主结算阶段；载荷同 card.died。",
        [GameUpdates.UnitAttacked] =
            "攻击结算完成（指挥攻击与效果攻击都发）；被拦截/未结算不触发。攻击者当场阵亡、伏击改写（零伤害）同样发——本档是「发生过一次攻击」，不是「造成了伤害」。",
    };

    /// <summary>
    /// **主体卡字段白名单**（R32／D27）：信号 → 载荷里表示「宿主卡自身」的字段名，未登记⇒不出 <c>self</c> 档。
    /// <para>
    /// 22 条登记（26 条含内核 4 条）：回合五连／<c>deck.shuffled</c>／<c>slot.*</c>／<c>point.*</c> 的实体字段是
    /// <c>Player</c>／<c>Deck</c>／<c>Source</c>，拿宿主卡去比**恒不命中**，故不登记（方向偏向漏登记）。
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> Subjects { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [GameUpdates.CardPlayed] = GameUpdates.PayloadCard,
        [GameUpdates.CardDrawn] = GameUpdates.PayloadCard,
        [GameUpdates.CardStatChanged] = GameUpdates.PayloadCard,
        [GameUpdates.CardHandAdd] = GameUpdates.PayloadCard,
        [GameUpdates.CardDiscarded] = GameUpdates.PayloadCard,
        [GameUpdates.CardBurned] = GameUpdates.PayloadCard,
        [GameUpdates.CardDied] = GameUpdates.PayloadCard,
        [GameUpdates.CardDying] = GameUpdates.PayloadCard,
        [GameUpdates.CardLeaving] = GameUpdates.PayloadCard,
        [GameUpdates.KeywordAttackFinished] = GameUpdates.PayloadCard,
        [GameUpdates.UnitJoined] = GameUpdates.PayloadUnit,
        [GameUpdates.UnitDeployed] = GameUpdates.PayloadUnit,
        [GameUpdates.UnitPositionChanged] = GameUpdates.PayloadUnit,
        [GameUpdates.UnitUpgraded] = GameUpdates.PayloadUnit,
        [GameUpdates.UnitRevealed] = GameUpdates.PayloadUnit,
        [GameUpdates.UnitActed] = GameUpdates.PayloadUnit,
        [GameUpdates.UnitCombatSurvived] = GameUpdates.PayloadUnit,
        [GameUpdates.UnitTypesChanged] = GameUpdates.PayloadUnit,
        [GameUpdates.UnitDamageDealt] = GameUpdates.PayloadUnit,
        [GameUpdates.UnitAttacked] = GameUpdates.PayloadUnit,
        [GameUpdates.CardDamaged] = GameUpdates.PayloadCard,
        [GameUpdates.CounterTriggered] = GameUpdates.PayloadCard,
        [GameUpdates.CounterDisarmed] = GameUpdates.PayloadCard,
    };

    /// <summary>
    /// **玩家字段白名单**（R32／D27）：信号 → 载荷里的玩家字段名，未登记⇒不出 <c>owner-*</c> 档。
    /// <para>19 条登记（回合五连 ＋ 卡动作三条 ＋ 反制两条 ＋ 卡组/资源六条 ＋ 抽到/手牌两条）。</para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> Players { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [GameUpdates.TurnStartBefore] = GameUpdates.PayloadPlayer,
        [GameUpdates.TurnStart] = GameUpdates.PayloadPlayer,
        [GameUpdates.TurnStartAfter] = GameUpdates.PayloadPlayer,
        [GameUpdates.TurnEndBefore] = GameUpdates.PayloadPlayer,
        [GameUpdates.TurnEnd] = GameUpdates.PayloadPlayer,
        [GameUpdates.CardPlayed] = GameUpdates.PayloadPlayer,
        [GameUpdates.CardDrawn] = GameUpdates.PayloadPlayer,
        [GameUpdates.CardHandAdd] = GameUpdates.PayloadPlayer,
        [GameUpdates.CardDiscarded] = GameUpdates.PayloadPlayer,
        [GameUpdates.CardBurned] = GameUpdates.PayloadPlayer,
        [GameUpdates.CounterTriggered] = GameUpdates.PayloadPlayer,
        [GameUpdates.CounterDisarmed] = GameUpdates.PayloadPlayer,
        [GameUpdates.DeckShuffled] = GameUpdates.PayloadPlayer,
        [GameUpdates.SlotGained] = GameUpdates.PayloadPlayer,
        [GameUpdates.SlotLost] = GameUpdates.PayloadPlayer,
        [GameUpdates.SlotChanged] = GameUpdates.PayloadPlayer,
        [GameUpdates.PointGained] = GameUpdates.PayloadPlayer,
        [GameUpdates.PointLost] = GameUpdates.PayloadPlayer,
        [GameUpdates.PointChanged] = GameUpdates.PayloadPlayer,
    };

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
            Game(GameUpdates.UnitAttacked,
                unit,
                (GameUpdates.PayloadTarget, ViewFieldType.Entity),
                (GameUpdates.PayloadByEffect, ViewFieldType.Boolean),
                source),

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
