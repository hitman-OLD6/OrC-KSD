using System.Reflection;
using System.Text.Json;
using Orc.Core;
using Orc.Game.Board;
using Orc.Game.Cards;
using Orc.Game.Judicators;
using Orc.Game.Managers;
using Orc.Game.Output;
using Orc.Game.Triggers;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 2A 验收锚点⑤（hooks 六项）：常量与载荷键（冻结字面值）、发射入口就绪（零调用点四项经测试发射）、
/// drawn → hand.add 并存（粒度不同；硬性位于 turn.start 与 turn.start.after 之间）、
/// card.load 真实调用（初始化逐张；排序＝玩家索引升序；逐位稳定）、起手静默。
/// </summary>
public class GameHooksTests
{
    [Fact]
    public void Hook_Constants_Have_Exact_Frozen_Literals()
    {
        Assert.Equal("card.load", GameUpdates.CardLoad);
        Assert.Equal("card.hand.add", GameUpdates.CardHandAdd);
        Assert.Equal("card.died", GameUpdates.CardDied);
        Assert.Equal("unit.joined", GameUpdates.UnitJoined);
        Assert.Equal("unit.deployed", GameUpdates.UnitDeployed);
        Assert.Equal("unit.position.changed", GameUpdates.UnitPositionChanged);
    }

    [Fact]
    public void Hook_Payload_Keys_Have_Exact_Frozen_Literals()
    {
        Assert.Equal("Unit", GameUpdates.PayloadUnit);
        Assert.Equal("Position", GameUpdates.PayloadPosition);
        Assert.Equal("OldPosition", GameUpdates.PayloadOldPosition);
        Assert.Equal("NewPosition", GameUpdates.PayloadNewPosition);
        // card.load / card.hand.add 复用既有载荷键（Card / Player）
        Assert.Equal("Card", GameUpdates.PayloadCard);
        Assert.Equal("Player", GameUpdates.PayloadPlayer);
    }

    [Fact]
    public void Hook_Constants_Do_Not_Collide_With_Existing_Or_Builtin_Constant_Sets()
    {
        var hooks = new[]
        {
            GameUpdates.CardLoad, GameUpdates.CardHandAdd, GameUpdates.CardDied,
            GameUpdates.UnitJoined, GameUpdates.UnitDeployed, GameUpdates.UnitPositionChanged,
        };
        var firstBatch = new[]
        {
            GameUpdates.TurnStartBefore, GameUpdates.TurnStart, GameUpdates.TurnStartAfter,
            GameUpdates.TurnEndBefore, GameUpdates.TurnEnd,
            GameUpdates.CardPlayed, GameUpdates.CardDrawn, GameUpdates.CardStatChanged,
        };
        var builtin = new[] { Updates.CardPlaced, Updates.CardDestroyed, Updates.CardData, Updates.EffectRemoved };

        Assert.Equal(6, hooks.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(hooks, firstBatch.Contains);
        Assert.DoesNotContain(hooks, builtin.Contains);
        // card.died 与引擎内置 card.destroyed 并存（近似名不同义）
        Assert.NotEqual(Updates.CardDestroyed, GameUpdates.CardDied);
    }

    [Fact]
    public async Task Zero_Callpoint_Hooks_Are_Emittable_With_Frozen_Payloads()
    {
        var engine = new LogicEngine();
        using var recorder = new UpdateRecorder(engine);
        var unit = new UnitCard(engine, new CardDefinition("单位", 1, 2, 3, 4, faction: Faction.Germany, rarity: Rarity.Standard));
        var line = new BattleLine(4);
        var oldPosition = line.GetInsertionGaps().At(0);
        var newPosition = line.GetInsertionGaps().At(1);

        await GameUpdates.EmitCardDied(engine, unit);
        await GameUpdates.EmitUnitJoined(engine, unit, new BoardPosition(oldPosition.Line, oldPosition.Ordinal));
        await GameUpdates.EmitUnitDeployed(engine, unit, new BoardPosition(newPosition.Line, newPosition.Ordinal));
        await GameUpdates.EmitUnitPositionChanged(engine, unit, new BoardPosition(oldPosition.Line, oldPosition.Ordinal), new BoardPosition(newPosition.Line, newPosition.Ordinal));

        Assert.Equal(
            new[] { GameUpdates.CardDied, GameUpdates.UnitJoined, GameUpdates.UnitDeployed, GameUpdates.UnitPositionChanged },
            recorder.Types);
        // card.died 载荷 {Card}
        Assert.Same(unit, recorder.Updates[0].Payload![GameUpdates.PayloadCard]);
        // unit.joined / unit.deployed 载荷 {Unit, Position}
        Assert.Same(unit, recorder.Updates[1].Payload![GameUpdates.PayloadUnit]);
        Assert.Same(oldPosition, recorder.Updates[1].Payload![GameUpdates.PayloadPosition]);
        Assert.Same(unit, recorder.Updates[2].Payload![GameUpdates.PayloadUnit]);
        Assert.Same(newPosition, recorder.Updates[2].Payload![GameUpdates.PayloadPosition]);
        // unit.position.changed 载荷 {Unit, OldPosition, NewPosition}
        Assert.Same(unit, recorder.Updates[3].Payload![GameUpdates.PayloadUnit]);
        Assert.Same(oldPosition, recorder.Updates[3].Payload![GameUpdates.PayloadOldPosition]);
        Assert.Same(newPosition, recorder.Updates[3].Payload![GameUpdates.PayloadNewPosition]);
    }

    [Fact]
    public async Task Draw_Chain_Emits_Drawn_Then_HandAdd_Between_TurnStart_And_After()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        using var recorder = new UpdateRecorder(match.Engine);
        await match.Initialize();
        recorder.Clear();

        await match.EndTurn(); // 回合 2：后手抽牌

        var types = recorder.Types.ToList();
        var drawnIndex = types.IndexOf(GameUpdates.CardDrawn);
        Assert.True(drawnIndex > types.IndexOf(GameUpdates.TurnStart)); // 硬性位置：turn.start 之后
        Assert.Equal(GameUpdates.CardHandAdd, types[drawnIndex + 1]); // 顺序：drawn 先、hand.add 后
        Assert.True(types.IndexOf(GameUpdates.CardHandAdd) < types.IndexOf(GameUpdates.TurnStartAfter)); // 且先于 after

        // 并存语义、粒度不同：drawn（抽牌动作）与 hand.add（进手牌归属）载荷同卡同玩家
        var drawn = recorder.Updates[drawnIndex];
        var handAdd = recorder.Updates[drawnIndex + 1];
        Assert.Same(drawn.Payload![GameUpdates.PayloadCard], handAdd.Payload![GameUpdates.PayloadCard]);
        Assert.Same(match.Players[1], drawn.Payload[GameUpdates.PayloadPlayer]);
        Assert.Same(match.Players[1], handAdd.Payload[GameUpdates.PayloadPlayer]);
    }

    [Fact]
    public async Task Card_Load_Is_Emitted_Per_Card_Grouped_By_Player_Index()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        using var recorder = new UpdateRecorder(match.Engine);
        await match.Initialize();

        var loads = recorder.Updates.Where(u => u.Type == GameUpdates.CardLoad).ToList();
        Assert.Equal(GameTestData.StandardDeckSize * 2, loads.Count); // 数量＝双方卡组总数
        Assert.All(loads.Take(GameTestData.StandardDeckSize),
            u => Assert.Same(match.Players[0], u.Payload![GameUpdates.PayloadPlayer])); // A 组在前
        Assert.All(loads.Skip(GameTestData.StandardDeckSize),
            u => Assert.Same(match.Players[1], u.Payload![GameUpdates.PayloadPlayer])); // B 组在后
        // 起手静默：装载不发 drawn / hand.add
        Assert.DoesNotContain(GameUpdates.CardDrawn, recorder.Types);
        Assert.DoesNotContain(GameUpdates.CardHandAdd, recorder.Types);
    }

    [Fact]
    public async Task Card_Load_Order_Is_Bitwise_Stable_For_Same_Seed()
    {
        var m1 = GameTestData.CreateStandardMatch(seed: 777);
        var m2 = GameTestData.CreateStandardMatch(seed: 777);
        using var r1 = new UpdateRecorder(m1.Engine);
        using var r2 = new UpdateRecorder(m2.Engine);

        await m1.Initialize();
        await m2.Initialize();

        // 同种子＋同参 → 加载顺序与内容逐位一致（可复现）
        Assert.Equal(LoadNamesOf(r1), LoadNamesOf(r2));

        static string[] LoadNamesOf(UpdateRecorder recorder) => recorder.Updates
            .Where(u => u.Type == GameUpdates.CardLoad)
            .Select(u => ((Orc.Cards.Card)u.Payload![GameUpdates.PayloadCard]!).Name)
            .ToArray();
    }

    [Fact]
    public async Task Hook_Emission_Helpers_Validate_Arguments()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();
        var engine = match.Engine;
        var player = match.Players[0];
        var unit = new UnitCard(engine, new CardDefinition("单位", 1, 2, 3, 4, faction: Faction.Germany, rarity: Rarity.Standard));
        var line = new BattleLine(4);

        await Assert.ThrowsAsync<ArgumentNullException>(() => GameUpdates.EmitCardLoad(null!, player, unit));
        await Assert.ThrowsAsync<ArgumentNullException>(() => GameUpdates.EmitCardLoad(engine, null!, unit));
        await Assert.ThrowsAsync<ArgumentNullException>(() => GameUpdates.EmitCardLoad(engine, player, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => GameUpdates.EmitCardDied(engine, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => GameUpdates.EmitUnitJoined(engine, unit, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => GameUpdates.EmitUnitPositionChanged(engine, unit, null!, new BoardPosition(line, 0)));
    }

    // ---------- 03-hook定义 S6：清单层 ↔ 代码一致性核对 ----------

    [Fact]
    public void GameHooks_Reexports_All_GameUpdates_Constants_Without_Copying_Literals()
    {
        var constants = typeof(GameUpdates)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (field.Name, Value: (string)field.GetRawConstantValue()!))
            .ToList();

        var signals = constants.Where(c => !c.Name.StartsWith("Payload", StringComparison.Ordinal))
            .Select(c => c.Value).ToList();
        var payloadKeys = constants.Where(c => c.Name.StartsWith("Payload", StringComparison.Ordinal))
            .Select(c => c.Value).ToList();

        // 35 = 原 31 + card.dying（卸载前宿主结算）+ card.leaving（离场前）+ keyword.attack.finished（临时增益到期）
        // + counter.disarmed（反制武装窗口到期自动解除——与 counter.triggered 成对，R1.3）。
        Assert.Equal(35, signals.Count);
        // 18 = 原 17 + Source（R9：事件引发者，恒写、可空）。
        Assert.Equal(18, payloadKeys.Count);
        Assert.Equal(signals.OrderBy(v => v, StringComparer.Ordinal),
            GameHooks.Signals.OrderBy(v => v, StringComparer.Ordinal));
        Assert.Equal(payloadKeys.OrderBy(v => v, StringComparer.Ordinal),
            GameHooks.PayloadKeys.OrderBy(v => v, StringComparer.Ordinal));
    }

    [Fact]
    public void GameHooks_Reexports_All_JudicatorNames_And_TriggerLayers()
    {
        var names = typeof(JudicatorNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(27, names.Count);
        Assert.Equal(names.OrderBy(v => v, StringComparer.Ordinal),
            GameHooks.JudicatorNameList.OrderBy(v => v, StringComparer.Ordinal));

        // 装配面：25 内置固定注册段（4 验证类＋6 交战类＋4 动作资格类〔含抑制资格〕＋1 效果选靶类＋5 资源类
        // ＋1 目标候选合法性〔S2 提入〕＋1 回合归属〔R4〕＋3 通用关系类〔相邻/兵种/归属〕） + 1 外部装配段 = 全量 26
        Assert.Equal(26, GameHooks.BuiltInJudicatorNames.Count);
        Assert.Single(GameHooks.ExternalJudicatorNames);
        Assert.Equal(GameHooks.JudicatorNameList.OrderBy(v => v, StringComparer.Ordinal),
            GameHooks.BuiltInJudicatorNames.Concat(GameHooks.ExternalJudicatorNames)
                .OrderBy(v => v, StringComparer.Ordinal));

        Assert.Equal(Enum.GetValues<TriggerLayer>(), GameHooks.TriggerLayers);
    }

    [Fact]
    public void Every_Signal_Has_A_Static_Emit_Callpoint()
    {
        // 25 条经 GameUpdates.Emit* 助手；5 条 turn.* 经 TurnManager.EmitTurnAsync 直发（无助手）
        var helpers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GameHooks.CardPlayed] = "EmitCardPlayed",
            [GameHooks.CardDrawn] = "EmitCardDrawn",
            [GameHooks.CardStatChanged] = "EmitCardStatChanged",
            [GameHooks.CardLoad] = "EmitCardLoad",
            [GameHooks.CardHandAdd] = "EmitCardHandAdd",
            [GameHooks.CardDiscarded] = "EmitCardDiscarded",
            [GameHooks.CardBurned] = "EmitCardBurned",
            [GameHooks.CardDied] = "EmitCardDied",
            [GameHooks.CardDying] = "EmitCardDying",
            [GameHooks.CardLeaving] = "EmitCardLeaving",
            [GameHooks.KeywordAttackFinished] = "EmitKeywordAttackFinished",
            [GameHooks.UnitJoined] = "EmitUnitJoined",
            [GameHooks.UnitDeployed] = "EmitUnitDeployed",
            [GameHooks.UnitPositionChanged] = "EmitUnitPositionChanged",
            [GameHooks.UnitUpgraded] = "EmitUnitUpgraded",
            [GameHooks.UnitRevealed] = "EmitUnitRevealed",
            [GameHooks.DeckShuffled] = "EmitDeckShuffled",
            [GameHooks.UnitTypesChanged] = "EmitUnitTypesChanged",
            [GameHooks.SlotGained] = "EmitSlotGained",
            [GameHooks.SlotLost] = "EmitSlotLost",
            [GameHooks.SlotChanged] = "EmitSlotChanged",
            [GameHooks.PointGained] = "EmitPointGained",
            [GameHooks.PointLost] = "EmitPointLost",
            [GameHooks.PointChanged] = "EmitPointChanged",
            [GameHooks.CardDamaged] = "EmitCardDamaged",
            [GameHooks.UnitActed] = "EmitUnitActed",
            [GameHooks.UnitCombatSurvived] = "EmitUnitCombatSurvived",
            [GameHooks.UnitDamageDealt] = "EmitUnitDamageDealt",
            [GameHooks.CounterTriggered] = "EmitCounterTriggered",
            [GameHooks.CounterDisarmed] = "EmitCounterDisarmed",
        };
        var turnSignals = new[]
        {
            GameHooks.TurnStartBefore, GameHooks.TurnStart, GameHooks.TurnStartAfter,
            GameHooks.TurnEndBefore, GameHooks.TurnEnd,
        };

        // 30 = 原 27 + card.leaving + keyword.attack.finished + counter.disarmed。
        Assert.Equal(30, helpers.Count);
        foreach (var (signal, method) in helpers)
        {
            Assert.Contains(signal, GameHooks.Signals);
            Assert.NotNull(typeof(GameUpdates).GetMethod(method, BindingFlags.Public | BindingFlags.Static));
        }

        foreach (var signal in turnSignals)
        {
            Assert.Contains(signal, GameHooks.Signals);
        }
        Assert.NotNull(typeof(TurnManager).GetMethod(
            "EmitTurnAsync", BindingFlags.NonPublic | BindingFlags.Instance));

        // 26 + 5 = 31，无遗漏、无重复（E1-33 追加 card.damaged / unit.acted；E1-39 追加 unit.combat.survived；
        // S1 追加 unit.upgraded；S2 追加 unit.revealed）
        Assert.Equal(GameHooks.Signals.Count,
            helpers.Keys.Concat(turnSignals).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Judicator_Assembly_Matches_Initialize_Fixed_Section_Plus_External_Segment()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();

        // 固定注册段（Match.Initialize 内置段）：验证类/交战类/资格类/效果选靶类/资源类/目标候选合法性类恒可达
        foreach (var name in GameHooks.BuiltInJudicatorNames)
        {
            Assert.NotNull(match.Judicators.Resolve(name));
        }

        // 外部装配段（judicatorAssembly）：未注入 ⇒ 1 条示范类（DeckTopTag）不可达（fail-fast）
        // 〔S2 受控适配（2 → 1）：目标候选合法性判定提入内置段——原经外部装配段〕
        foreach (var name in GameHooks.ExternalJudicatorNames)
        {
            Assert.Throws<KeyNotFoundException>(() => match.Judicators.Resolve(name));
        }
    }

    [Fact]
    public async Task Trigger_Layers_Are_Wired_To_LowLevel_BuiltIn_Triggers()
    {
        var match = GameTestData.CreateStandardMatch(seed: 42);
        await match.Initialize();

        Assert.Equal(Enum.GetValues<TriggerLayer>(), GameHooks.TriggerLayers);

        Assert.All(match.TriggerRegistry.Entries, entry => Assert.True(Enum.IsDefined(entry.Layer)));
        // 指挥流程 + 单位移动 + 单位攻击 + 造成攻击伤害（Match.Initialize 注册的 4 个内置流程触发器）
        Assert.True(match.TriggerRegistry.GetByLayer(TriggerLayer.LowLevel).Count >= 4);
    }

    [Fact]
    public void Pending_Layer_Covers_All_27_Kards_Triggers_With_Status()
    {
        Assert.Equal(27, GameHooks.PendingTriggers.Count);
        Assert.Equal(27, GameHooks.PendingTriggers
            .Select(p => p.KardsTrigger).Distinct(StringComparer.Ordinal).Count());

        Assert.Equal(6, GameHooks.PendingTriggers.Count(p => p.Status == GameHookPendingStatus.Available));
        Assert.Equal(13, GameHooks.PendingTriggers.Count(p => p.Status == GameHookPendingStatus.Deferred));
        Assert.Equal(8, GameHooks.PendingTriggers.Count(p => p.Status == GameHookPendingStatus.NotPlanned));

        var flowAllowList = new[] { GameHooks.FlowUnitAttack };
        foreach (var pending in GameHooks.PendingTriggers)
        {
            // 任何对位都必须是「18 信号 或 流程位白名单」之一（防伪对位）
            Assert.All(pending.OrcCounterparts, counterpart => Assert.True(
                GameHooks.Signals.Contains(counterpart) || flowAllowList.Contains(counterpart),
                $"待补项 '{pending.KardsTrigger}' 的对位 '{counterpart}' 不在信号集/流程位白名单内。"));

            if (pending.Status == GameHookPendingStatus.Available)
            {
                Assert.NotEmpty(pending.OrcCounterparts);
            }
            if (pending.Status == GameHookPendingStatus.NotPlanned)
            {
                Assert.Empty(pending.OrcCounterparts);
            }
        }

        // 权威清单（docs/kards-diy-可参考语料报告.md §2.1）抽样锚点
        Assert.Contains(GameHooks.PendingTriggers, p => p.KardsTrigger == "unitDeployed");
        Assert.Contains(GameHooks.PendingTriggers, p => p.KardsTrigger == "unitMobilized");
        Assert.Contains(GameHooks.PendingTriggers, p => p.KardsTrigger == "chargeNow");
    }

    [Fact]
    public void Exporter_Emits_Parseable_Json_Covering_Signals_Judicators_Layers_And_Pending()
    {
        using var document = JsonDocument.Parse(GameHooksJson.Serialize(indented: true));
        var root = document.RootElement;

        // S1 受控适配（29 → 30）：unit.upgraded；S2（30 → 31）：unit.revealed；R1.3（31 → 32）：counter.disarmed。
        Assert.Equal(32, root.GetProperty("signals").GetArrayLength());
        Assert.Equal(23, root.GetProperty("judicators").GetArrayLength());
        Assert.Equal(GameHooks.TriggerLayers.Count, root.GetProperty("triggerLayers").GetArrayLength());
        Assert.Equal(27, root.GetProperty("pending").GetArrayLength());

        foreach (var signal in root.GetProperty("signals").EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(signal.GetProperty("name").GetString()));
            Assert.True(signal.GetProperty("payloadKeys").GetArrayLength() > 0);
            Assert.True(signal.GetProperty("emitters").GetArrayLength() > 0);
        }

        foreach (var pending in root.GetProperty("pending").EnumerateArray())
        {
            Assert.Contains(pending.GetProperty("orcStatus").GetString(),
                new[] { "已具备", "后置", "不做" });
        }
    }
}
