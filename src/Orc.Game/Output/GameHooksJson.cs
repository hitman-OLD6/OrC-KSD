using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Orc.Game.Output;

/// <summary>
/// 游戏层接入点／信号面导出器（03-hook定义 S5；<c>invariants.json</c> 式映射）。
/// 风格对齐 <c>src/Orc/Output/*</c>（静态类 + <c>Serialize(...)</c> → string、不写文件），
/// 但**不复用** <c>Orc.Output.JsonValueWriter</c>——其为 <c>Orc</c> 程序集 <c>internal</c> 且内核本批不触动；
/// 本导出器自持 <see cref="Utf8JsonWriter"/> 手写写出（定深结构、无递归）。
/// </summary>
public static class GameHooksJson
{
    /// <summary>序列化为 JSON 文本（确定性；覆盖全部信号、判定器、触发器分层、待补层）。</summary>
    public static string Serialize(bool indented = false)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented }))
        {
            writer.WriteStartObject();

            writer.WriteStartArray("signals");
            foreach (var signal in SignalMetadata)
            {
                writer.WriteStartObject();
                writer.WriteString("name", signal.Name);
                WriteStringArray(writer, "payloadKeys", signal.PayloadKeys);
                WriteStringArray(writer, "emitters", signal.Emitters);
                WriteStringArray(writer, "subscribersHint", signal.SubscribersHint);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("judicators");
            foreach (var judicator in JudicatorMetadata)
            {
                writer.WriteStartObject();
                writer.WriteString("name", judicator.Name);
                WriteStringArray(writer, "implementations", judicator.Implementations);
                writer.WriteBoolean("builtIn", judicator.BuiltIn);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("pending");
            foreach (var pending in GameHooks.PendingTriggers)
            {
                writer.WriteStartObject();
                writer.WriteString("kardsTrigger", pending.KardsTrigger);
                writer.WriteString("orcStatus", pending.StatusText);
                WriteStringArray(writer, "orcCounterparts", pending.OrcCounterparts);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }
        writer.WriteEndArray();
    }

    /// <summary>信号描述（名称／载荷键／发射点〔文件:行·成员〕／订阅者提示）。</summary>
    private sealed record SignalEntry(
        string Name,
        IReadOnlyList<string> PayloadKeys,
        IReadOnlyList<string> Emitters,
        IReadOnlyList<string> SubscribersHint);

    /// <summary>判定器描述（名称／实现类／是否内置固定注册段）。</summary>
    private sealed record JudicatorEntry(
        string Name,
        IReadOnlyList<string> Implementations,
        bool BuiltIn);

    private static readonly IReadOnlyList<string> TurnPayload =
        [GameHooks.PayloadPlayer, GameHooks.PayloadTurnNumber, GameHooks.PayloadSource];

    private static readonly IReadOnlyList<string> CardPlayerPayload =
        [GameHooks.PayloadCard, GameHooks.PayloadPlayer, GameHooks.PayloadSource];

    /// <summary>31 条信号的权威描述表（与 03-hook定义 S2 对齐；E1-25 追加资源六项；E1-33 追加伤害/行动两项；S1 追加 unit.upgraded；S2 追加 unit.revealed）。</summary>
    private static IReadOnlyList<SignalEntry> SignalMetadata { get; } = new[]
    {
        new SignalEntry(GameHooks.TurnStartBefore, TurnPayload,
            ["Managers/TurnManager.cs:100·BeginTurnAsync"], []),
        new SignalEntry(GameHooks.TurnStart, TurnPayload,
            ["Managers/TurnManager.cs:101·BeginTurnAsync"],
            ["Commanding/CommandManager.cs·在场回合数计数"]),
        new SignalEntry(GameHooks.TurnStartAfter, TurnPayload,
            ["Managers/TurnManager.cs:111·BeginTurnAsync"], []),
        new SignalEntry(GameHooks.TurnEndBefore, TurnPayload,
            ["Managers/TurnManager.cs:86·EndTurn"], []),
        new SignalEntry(GameHooks.TurnEnd, TurnPayload,
            ["Managers/TurnManager.cs:87·EndTurn"],
            ["Cards/KeywordComponents2.cs·回合末词条"]),
        new SignalEntry(GameHooks.CardPlayed,
            [GameHooks.PayloadCard, GameHooks.PayloadPlayer, GameHooks.PayloadSource],
            ["Cards/UnitCard.cs:97·HandlePlayChainAsync", "Managers/PlayManager.cs·HandleOrderAnnouncedAsync（OrderFlow/Resolve priority 0）"],
            ["Cards/KeywordComponents2.cs·打出时词条"]),
        new SignalEntry(GameHooks.CardDrawn,
            [GameHooks.PayloadPlayer, GameHooks.PayloadCard, GameHooks.PayloadSource],
            ["HandLimitBurn.cs:66·BurnAsync（爆牌路径）", "Managers/PlayerManager.cs:163·DrawCard（常规）"],
            ["Match/MatchCardService.cs·进手牌链路"]),
        new SignalEntry(GameHooks.CardStatChanged,
            [GameHooks.PayloadCard, GameHooks.PayloadChangedFields, GameHooks.PayloadSource],
            ["Cards/CardModifierComponent.cs:632·RequestRerunCoreAsync"],
            ["Commanding/CommandManager.cs·防御归零检查触发器", "Commanding/CommandManager.cs·HQ 归零检查触发器"]),
        new SignalEntry(GameHooks.CardLoad, CardPlayerPayload,
            ["Cards/CardBase.cs:177·LoadAsync"], []),
        new SignalEntry(GameHooks.CardHandAdd, CardPlayerPayload,
            ["Match/MatchCardService.cs:220·PlaceToHandAsync", "Managers/PlayerManager.cs:163·DrawCard"], []),
        new SignalEntry(GameHooks.CardDiscarded, CardPlayerPayload,
            ["HandLimitBurn.cs:47·DestroyAndEmitDiscardedAsync（弃置处置链）"], []),
        new SignalEntry(GameHooks.CardBurned, CardPlayerPayload,
            ["HandLimitBurn.cs:70·BurnAsync（爆牌共享单元）"], []),
        new SignalEntry(GameHooks.CounterTriggered,
            [GameHooks.PayloadCard, GameHooks.PayloadPlayer, GameHooks.PayloadSource],
            ["Cards/CounterCard.cs·HandleUseCounterAsync（激活分支）"], []),
        new SignalEntry(GameHooks.CounterDisarmed,
            [GameHooks.PayloadCard, GameHooks.PayloadPlayer, "Reason", GameHooks.PayloadSource],
            ["Cards/CounterCard.cs·HandleTurnStartAsync（己方回合开始——解除落定后发射）"], []),
        new SignalEntry(GameHooks.UnitDamageDealt,
            [GameHooks.PayloadUnit, GameHooks.PayloadCard, GameHooks.PayloadAmount, GameHooks.PayloadSource],
            ["Commanding/CommandManager.cs·HandleDefaultAttackDamageAsync", "Commanding/CommandManager.cs·HandleUnitAttackAsync"], []),
        new SignalEntry(GameHooks.CardDied,
            [GameHooks.PayloadCard, GameHooks.PayloadKiller, GameHooks.PayloadSource],
            ["Commanding/CommandManager.cs·ProcessDeathAsync"],
            ["Commanding/CommandManager.cs·守护维护"]),
        new SignalEntry(GameHooks.UnitJoined,
            [GameHooks.PayloadUnit, GameHooks.PayloadPosition, GameHooks.PayloadSource],
            ["Cards/UnitCard.cs:159·HandleJoinChainAsync"],
            ["Commanding/CommandManager.cs·守护维护"]),
        new SignalEntry(GameHooks.UnitDeployed,
            [GameHooks.PayloadUnit, GameHooks.PayloadPosition, GameHooks.PayloadSource],
            ["Cards/UnitCard.cs:138·HandleDeployChainAsync"],
            ["Commanding/CommandManager.cs·守护维护"]),
        new SignalEntry(GameHooks.UnitPositionChanged,
            [GameHooks.PayloadUnit, GameHooks.PayloadOldPosition, GameHooks.PayloadNewPosition, GameHooks.PayloadSource],
            ["Commanding/CommandManager.cs:1177·HandleUnitMoveAsync"],
            ["Commanding/CommandManager.cs·守护维护"]),
        new SignalEntry(GameHooks.UnitUpgraded,
            [GameHooks.PayloadUnit, GameHooks.PayloadSource],
            ["Cards/UnitCard.cs·PromoteToVeteranAsync（升级收尾——先落定后发射、恰一次）"], ["Commanding/CommandManager.cs·守护维护"]),
        new SignalEntry(GameHooks.UnitRevealed,
            [GameHooks.PayloadUnit, GameHooks.PayloadSource],
            ["Cards/CovertSystem.cs·RevealAsync（揭示收尾——先落定后发射、恰一次）"], []),
        new SignalEntry(GameHooks.DeckShuffled,
            [GameHooks.PayloadPlayer, GameHooks.PayloadDeck, GameHooks.PayloadSource],
            ["Match/Match.cs:348·ShuffleDeckAsync"], []),
        new SignalEntry(GameHooks.UnitTypesChanged,
            [GameHooks.PayloadUnit, GameHooks.PayloadAddedType, GameHooks.PayloadSource],
            ["Cards/UnitCard.cs:266·AddUnitTypeAsync"], []),
        new SignalEntry(GameHooks.CardDamaged,
            [GameHooks.PayloadCard, GameHooks.PayloadAmount, GameHooks.PayloadSource],
            ["Cards/UnitCard.cs·ApplyDefenseDamageAsync", "Players/Hq.cs·ApplyDamageAsync"], []),
        new SignalEntry(GameHooks.UnitActed,
            [GameHooks.PayloadUnit, GameHooks.PayloadSource],
            ["Commanding/CommandManager.cs·DispatchAttackAsync", "Commanding/CommandManager.cs·DispatchMoveAsync"], []),
        new SignalEntry(GameHooks.UnitCombatSurvived,
            [GameHooks.PayloadUnit, GameHooks.PayloadSource],
            ["Commanding/CommandManager.cs·HandleDefaultAttackDamageAsync（结算收尾）"], []),
        new SignalEntry(GameHooks.SlotGained,
            [GameHooks.PayloadPlayer, GameHooks.PayloadAmount, GameHooks.PayloadSource],
            ["Managers/ResourceManager.cs·GainSlotsAsync"], []),
        new SignalEntry(GameHooks.SlotLost,
            [GameHooks.PayloadPlayer, GameHooks.PayloadAmount, GameHooks.PayloadSource],
            ["Managers/ResourceManager.cs·LoseSlotsAsync"], []),
        new SignalEntry(GameHooks.SlotChanged,
            [GameHooks.PayloadPlayer, GameHooks.PayloadOldSlots, GameHooks.PayloadNewSlots, GameHooks.PayloadSource],
            ["Managers/ResourceManager.cs·SettleAsync / GainSlotsAsync / LoseSlotsAsync"], []),
        new SignalEntry(GameHooks.PointGained,
            [GameHooks.PayloadPlayer, GameHooks.PayloadAmount, GameHooks.PayloadSource],
            ["Managers/ResourceManager.cs·GainPointsAsync"], []),
        new SignalEntry(GameHooks.PointLost,
            [GameHooks.PayloadPlayer, GameHooks.PayloadAmount, GameHooks.PayloadSource],
            ["Managers/ResourceManager.cs·LosePointsAsync"], []),
        new SignalEntry(GameHooks.PointChanged,
            [GameHooks.PayloadPlayer, GameHooks.PayloadOldPoints, GameHooks.PayloadNewPoints, GameHooks.PayloadSource],
            ["Managers/ResourceManager.cs·ChangePointsAsync / GainPointsAsync / LosePointsAsync"], []),
    };

    /// <summary>23 条判定器的权威描述表（与 03-hook定义 S3-3 对齐；E1-25 补齐 effect.target.resolve 缺口并追加资源五条；S2：目标候选合法性提入生产内置段；R4：回合归属准入）。</summary>
    private static IReadOnlyList<JudicatorEntry> JudicatorMetadata { get; } = new[]
    {
        new JudicatorEntry(GameHooks.JudicatorActionInput,
            ["Judicators/ActionInputJudicator.cs"], true),
        new JudicatorEntry(GameHooks.JudicatorActionTurnOwner,
            ["Judicators/ActionTurnOwnerJudicator.cs"], true),
        new JudicatorEntry(GameHooks.JudicatorCostCheck,
            ["Judicators/CostCheckJudicator.cs"], true),
        new JudicatorEntry(GameHooks.JudicatorCounterUse,
            ["Judicators/CounterUseJudicator.cs"], true),
        new JudicatorEntry(GameHooks.JudicatorMoveRecheck,
            ["Judicators/MoveRevalidationJudicator.cs"], true),
        new JudicatorEntry(GameHooks.JudicatorAttackRecheck,
            ["Judicators/AttackRevalidationJudicator.cs"], true),
        new JudicatorEntry(GameHooks.JudicatorCombatTargetLegal,
            ["Judicators/CombatTargetLegalJudicator.cs"], true),
        new JudicatorEntry(GameHooks.JudicatorCombatRange,
            ["Judicators/CombatRangeJudicator.cs"], true),
        new JudicatorEntry(GameHooks.JudicatorCombatGuardEligibility,
            ["Judicators/CombatGuardEligibilityJudicator.cs"], true),
        new JudicatorEntry(GameHooks.JudicatorCombatInterception,
            ["Judicators/CombatInterceptionJudicator.cs"], true),
        new JudicatorEntry(GameHooks.JudicatorCombatCounterEligibility,
            ["Judicators/CombatCounterEligibilityJudicator.cs"], true),
        new JudicatorEntry(GameHooks.JudicatorCombatAmbushCondition,
            ["Judicators/CombatAmbushConditionJudicator.cs"], true),
        new JudicatorEntry(GameHooks.JudicatorMoveLegEligibility,
            ["Judicators/LegEligibilityJudicator.cs"], true),
        new JudicatorEntry(GameHooks.JudicatorAttackLegEligibility,
            ["Judicators/LegEligibilityJudicator.cs"], true),
        new JudicatorEntry(GameHooks.JudicatorMoveFrontlineEnemy,
            ["Judicators/MoveFrontlineEnemyJudicator.cs"], true),
        new JudicatorEntry(GameHooks.JudicatorEffectTargetResolve,
            ["Judicators/EffectTargetResolveJudicator.cs"], true),
        new JudicatorEntry(GameHooks.JudicatorPointSlotIncrement,
            ["Judicators/PointSlotIncrementJudicator.cs"], true),
        new JudicatorEntry(GameHooks.JudicatorPointSlotGain,
            ["Judicators/PointSlotGainAmountJudicator.cs"], true),
        new JudicatorEntry(GameHooks.JudicatorPointSlotLose,
            ["Judicators/PointSlotLoseAmountJudicator.cs"], true),
        new JudicatorEntry(GameHooks.JudicatorPointGain,
            ["Judicators/PointGainAmountJudicator.cs"], true),
        new JudicatorEntry(GameHooks.JudicatorPointLose,
            ["Judicators/PointLoseAmountJudicator.cs"], true),
        new JudicatorEntry(GameHooks.JudicatorDeckTopTag,
            ["Judicators/DeckTopTagJudicator.cs"], false),
        new JudicatorEntry(GameHooks.JudicatorTargetCandidateEligibility,
            ["Judicators/TargetEligibilityJudicator.cs"], true),
    };
}
