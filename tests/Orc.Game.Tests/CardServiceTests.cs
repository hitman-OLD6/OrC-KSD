using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Commanding;
using Orc.Game.Judicators;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// S9（G6+G13 生成·复制·转换）验收测试：
/// ①生成→手牌（端到端＋衍生卡「构筑外」）②生成→阵线（机制级全维＋上下文可达性专项＋落点敌方）
/// ③生成→相邻处（端到端：解析口径/选取分离/空解析失败零副作用）④生成→卡组（卡组顶/洗入＋专项）
/// ⑤复制（端到端：定义级/新实例/源不受影响）⑥转换（端到端组合链：同槽位/静态/离场销毁/信号/计数/离场通知）
/// ⑦类型增补（受控入口/信号/去重/实例级/专项）；差异点（满手爆牌/槽占用失败回收静默/定义不存在/
/// 跨玩家/重复放置）；门禁（准备态/终局后）；独立构造（可注入可测试）；脱局降级。
/// 驱动形态：主链场景（①③⑤⑥）＝效果端到端（效果桩经上下文取用面）；②④⑦＝机制级全维＋「上下文可达性」专项断言
/// （经效果/上下文路径完成一次该场景操作并断言结果——Q&A-1·2(c)/Q&A-6·2(c)）。
/// </summary>
public class CardServiceTests
{
    // ---------- ① 生成 → 手牌（端到端） ----------

    [Fact]
    public async Task Scenario1_Generate_To_Hand_EndToEnd_Derived_Card_Outside_Deck()
    {
        var registry = new CardEffectRegistry();
        registry.Register(S9Kit.ProbeEffectId,
            _ => new CardServiceSceneEffect(CardServiceSceneEffect.SceneOp.ToHand, S9Kit.LightInfantryId));
        registry.Declare(S9Kit.EmitterId, new[] { S9Kit.ProbeEffectId });

        var match = S9Kit.CreateSceneMatch(effectRegistry: registry);
        await match.Initialize();
        var playerA = match.Players[0];

        var (_, effect) = await S9Kit.PrepareEmitterAsync(match, playerA);
        using var recorder = new UpdateRecorder(match.Engine);
        var handBefore = playerA.Hand.Count;

        await effect.CastAsync(match.Engine); // 效果端到端：效果内取用服务→生成并放置

        // 取用通路真实贯通（经上下文取用面取得服务并调用）
        Assert.True(effect.ServiceResolved);
        var result = effect.LastResult;
        Assert.NotNull(result);
        Assert.Equal(CardPlaceStatus.Placed, result!.Status);
        var generated = Assert.IsType<UnitCard>(result.Card);

        // 新实例入尾部（「加入」语义）＋ hand.add 恰一次（载荷可用：{ Player, Card }）
        Assert.Equal(handBefore + 1, playerA.Hand.Count);
        Assert.Same(generated, playerA.Hand[^1]);
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardHandAdd));
        var payload = recorder.PayloadOf(GameUpdates.CardHandAdd)!;
        Assert.Same(playerA, payload[GameUpdates.PayloadPlayer]);
        Assert.Same(generated, payload[GameUpdates.PayloadCard]);

        // 衍生卡（专用定义经统一创建生成）自动「构筑外」标记（ID ＞ 水位线——既有 IsOutsideDeck 判定）
        Assert.True(match.IsOutsideDeck(generated));

        // 统一创建就绪（加载链完备：归属＋元数据装配——词条/效果按定义声明）
        Assert.Same(playerA, generated.Owner);
        Assert.NotNull(generated.GetData<TagData>());
    }

    [Fact]
    public async Task Scenario1_Burn_On_Full_Hand_Destroyed_Then_Burned_No_HandAdd()
    {
        var match = S9Kit.CreateSceneMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        // 填满手牌（起手 4 → 上限 9）
        while (playerA.Hand.Count < Player.HandLimit)
        {
            await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);
        }

        using var recorder = new UpdateRecorder(match.Engine);
        var result = await match.CardService.CreateAndPlaceToHandAsync(S9Kit.LightInfantryId, playerA);

        // 满手＝爆牌（直爆：不经手牌——手牌全程保持上限；结果＝裁决完成、非失败）
        Assert.Equal(CardPlaceStatus.Burned, result.Status);
        Assert.True(result.IsSuccess);
        Assert.Equal(Player.HandLimit, playerA.Hand.Count);
        var burned = Assert.IsType<UnitCard>(result.Card);
        Assert.False(burned.Life.IsAlive); // 已销毁

        // 信号（生成路径爆牌口径）：销毁（含 card.destroyed）→ card.burned 恰一次；card.discarded 零次（反向：
        // 爆牌不走弃牌路线）；hand.add / drawn 零次
        Assert.Equal(1, recorder.CountOf(Updates.CardDestroyed));
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardBurned));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardDiscarded));
        var destroyedIndex = recorder.Types.ToList().IndexOf(Updates.CardDestroyed);
        var burnedIndex = recorder.Types.ToList().IndexOf(GameUpdates.CardBurned);
        Assert.True(destroyedIndex >= 0 && destroyedIndex < burnedIndex); // 顺序：销毁先、burned 后
        var burnedPayload = recorder.PayloadOf(GameUpdates.CardBurned)!;
        Assert.Same(burned, burnedPayload[GameUpdates.PayloadCard]);
        Assert.Same(playerA, burnedPayload[GameUpdates.PayloadPlayer]);
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardDrawn));
    }

    // ---------- ② 生成 → 阵线（机制级全维 ＋ 上下文可达性专项 ＋ 落点敌方） ----------

    [Fact]
    public async Task Scenario2_Generate_To_SupportLine_Join_Semantics_No_Fee_No_Deployed()
    {
        var match = S9Kit.CreateSceneMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var slot = match.Battlefield.GetSupportLine(playerA)[3]; // 目标槽＝槽 3（避开 HQ 占位槽）

        using var recorder = new UpdateRecorder(match.Engine);
        var pointsBefore = playerA.Points;
        var unitCountBefore = S9Kit.CountAliveUnitsOf(match, playerA);

        // 生成目标＝闪击兵（部署词条探针：加入路径不走部署链收尾——闪击不置位）——正常生成走同类通路
        var result = await match.CardService.CreateAndPlaceToSupportLineAsync(
            CommandTestKit.BlitzId, playerA, slot);

        Assert.Equal(CardPlaceStatus.Placed, result.Status);
        var unit = Assert.IsType<UnitCard>(result.Card);

        // 指定空槽加入＋单位化组件齐备（位置/类型从定义填充/指挥组件）
        Assert.Same(unit, slot.Occupant);
        var state = unit.GetData<UnitStateData>();
        Assert.Same(slot, state.Position);
        Assert.False(state.IsDestroyed);
        Assert.Equal(new[] { UnitType.Infantry }, state.UnitTypes);
        Assert.NotNull(unit.GetData<CommandData>());

        // unit.joined 恰一次（载荷 { Unit, Position }）；非部署（无 unit.deployed、不扣费）
        Assert.Equal(1, recorder.CountOf(GameUpdates.UnitJoined));
        var payload = recorder.PayloadOf(GameUpdates.UnitJoined)!;
        Assert.Same(unit, payload[GameUpdates.PayloadUnit]);
        Assert.Same(slot, payload[GameUpdates.PayloadPosition]);
        Assert.Equal(0, recorder.CountOf(GameUpdates.UnitDeployed));
        Assert.Equal(pointsBefore, playerA.Points);
        Assert.Equal(unitCountBefore + 1, S9Kit.CountAliveUnitsOf(match, playerA));

        // 无部署词条：闪击（部署链收尾落点）未置位——加入路径与部署链分离（既有 JoinUnitAsync 语义，单源复用）
        Assert.False(unit.GetData<CommandData>().CanAttack);
    }

    [Fact]
    public async Task Scenario2_Context_Reachability_Via_Effect_SupportLine()
    {
        var registry = new CardEffectRegistry();
        registry.Register(S9Kit.ProbeEffectId,
            _ => new CardServiceSceneEffect(CardServiceSceneEffect.SceneOp.ToSupportLine, S9Kit.LightInfantryId));
        registry.Declare(S9Kit.EmitterId, new[] { S9Kit.ProbeEffectId });

        var match = S9Kit.CreateSceneMatch(effectRegistry: registry);
        await match.Initialize();
        var playerA = match.Players[0];

        var (_, effect) = await S9Kit.PrepareEmitterAsync(match, playerA);
        effect.TargetSlot = match.Battlefield.GetSupportLine(playerA)[3];
        using var recorder = new UpdateRecorder(match.Engine);

        await effect.CastAsync(match.Engine);

        // 上下文可达性专项：经效果/上下文路径完成一次该场景操作（调用真实发生）＋断言其结果（新实例入阵线）
        Assert.True(effect.ServiceResolved);
        Assert.Equal(CardPlaceStatus.Placed, effect.LastResult!.Status);
        Assert.Same(effect.LastResult.Card, effect.TargetSlot!.Occupant);
        Assert.Equal(1, recorder.CountOf(GameUpdates.UnitJoined));
    }

    [Fact]
    public async Task Scenario2_Generate_For_Enemy_Hand_Lands_With_Enemy_Owner()
    {
        var match = S9Kit.CreateSceneMatch();
        await match.Initialize();
        var playerB = match.Players[1];

        using var recorder = new UpdateRecorder(match.Engine);
        var result = await match.CardService.CreateAndPlaceToHandAsync(S9Kit.LightInfantryId, playerB);

        // 落点敌方（任意玩家支持——「为谁的区域生成/放置，就归谁」）
        Assert.Equal(CardPlaceStatus.Placed, result.Status);
        var card = result.Card!;
        Assert.Same(playerB, card.Owner);
        Assert.Same(card, playerB.Hand[^1]);
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Same(playerB, recorder.PayloadOf(GameUpdates.CardHandAdd)![GameUpdates.PayloadPlayer]);
    }

    // ---------- ③ 生成 → 相邻处（端到端：解析口径＋选取分离；空解析失败零副作用） ----------

    [Fact]
    public async Task Scenario3_Generate_To_Adjacent_EndToEnd_Resolves_Candidates_And_Places()
    {
        var registry = new CardEffectRegistry();
        registry.Register(S9Kit.ProbeEffectId,
            _ => new CardServiceSceneEffect(CardServiceSceneEffect.SceneOp.ToAdjacent, S9Kit.LightInfantryId));
        registry.Declare(S9Kit.EmitterId, new[] { S9Kit.ProbeEffectId });

        var match = S9Kit.CreateSceneMatch(effectRegistry: registry);
        await match.Initialize();
        var playerA = match.Players[0];
        var supportLine = match.Battlefield.GetSupportLine(playerA);

        // 场上布局：HQ（槽 2）＋ 步兵（槽 4）→ 候选＝[1, 3]（被占槽位〔含 HQ〕左右空邻位、去重、索引升序）
        await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 3);

        var (_, effect) = await S9Kit.PrepareEmitterAsync(match, playerA);
        using var recorder = new UpdateRecorder(match.Engine);

        await effect.CastAsync(match.Engine);

        // 解析与选取分离：服务解析候选（口径同「邻位动态候选」单源）、调用方策略选取（缺省＝取首个）、选定后放置
        Assert.Equal(CardPlaceStatus.Placed, effect.LastResult!.Status);
        Assert.NotNull(effect.LastCandidates);
        Assert.Equal(new[] { supportLine[1], supportLine[3] }, effect.LastCandidates!);
        Assert.Same(effect.LastResult.Card, supportLine[1].Occupant);

        // unit.joined 恰一次；不经手牌
        Assert.Equal(1, recorder.CountOf(GameUpdates.UnitJoined));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardHandAdd));
    }

    [Fact]
    public async Task Scenario3_Adjacent_Empty_Resolution_Fails_Without_Side_Effects()
    {
        var registry = new CardEffectRegistry();
        registry.Register(S9Kit.ProbeEffectId,
            _ => new CardServiceSceneEffect(CardServiceSceneEffect.SceneOp.ToAdjacent, S9Kit.LightInfantryId));
        registry.Declare(S9Kit.EmitterId, new[] { S9Kit.ProbeEffectId });

        var match = S9Kit.CreateSceneMatch(effectRegistry: registry);
        await match.Initialize();
        var playerA = match.Players[0];
        var supportLine = match.Battlefield.GetSupportLine(playerA);

        // 占满支援线（全部可部署格——HQ 两侧；跳过 HQ 占位槽）——无相邻空槽
        await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 0);

        var (_, effect) = await S9Kit.PrepareEmitterAsync(match, playerA);
        using var recorder = new UpdateRecorder(match.Engine);
        var handBefore = playerA.Hand.Count;
        var deckBefore = playerA.Deck.Count;
        var occupantsBefore = CommandTestKit.AllSlotsOf(match).Select(slot => slot.Occupant).ToArray();

        await effect.CastAsync(match.Engine);

        // 空解析＝失败且零副作用：不创建实例（无未归属残留）、选取不发生、区域/信号零变化
        Assert.Equal(CardPlaceStatus.Rejected, effect.LastResult!.Status);
        Assert.Equal(CardPlaceFailureReason.NoAdjacentSlot, effect.LastResult.FailureReason);
        Assert.Null(effect.LastResult.Card);
        Assert.Null(effect.LastCandidates); // 选取环节未到达（解析空 → 不进入选取）
        Assert.Equal(handBefore, playerA.Hand.Count);
        Assert.Equal(deckBefore, playerA.Deck.Count);
        Assert.Equal(occupantsBefore, CommandTestKit.AllSlotsOf(match).Select(slot => slot.Occupant).ToArray());
        Assert.Equal(0, recorder.TotalCount());
    }

    // ---------- ④ 生成 → 卡组（卡组顶/洗入；机制级全维 ＋ 上下文可达性专项） ----------

    [Fact]
    public async Task Scenario4a_Generate_To_Deck_Top_Silent_And_Drawn_Next()
    {
        var match = S9Kit.CreateSceneMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        using var recorder = new UpdateRecorder(match.Engine);
        var deckBefore = playerA.Deck.Count;

        var result = await match.CardService.CreateAndPlaceToDeckTopAsync(S9Kit.LightInfantryId, playerA);

        Assert.Equal(CardPlaceStatus.Placed, result.Status);

        // 卡组顶＝装入取件端（下次抽取取到）
        Assert.Equal(deckBefore + 1, playerA.Deck.Count);
        Assert.Equal(S9Kit.LightInfantryId, playerA.Deck[0]);
        Assert.Same(result.Card, playerA.Deck.DrawInstance());

        // 静默：卡组顶装入无信号（除创建事实 card.load 外零信号——无 deck.shuffled）
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardLoad));
        Assert.Equal(0, recorder.CountOf(GameUpdates.DeckShuffled));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardDrawn));
        Assert.Equal(0, recorder.CountOf(GameUpdates.UnitJoined));
    }

    [Fact]
    public async Task Scenario4b_Generate_Into_Deck_Shuffled_Emits_Signal_And_Shuffles()
    {
        var match = S9Kit.CreateSceneMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        // 洗切前序列快照（初始化后确定）＋装入后预期序列（装入取件端（索引 0）＋原序列）
        var beforeIds = Enumerable.Range(0, playerA.Deck.Count).Select(i => playerA.Deck[i]).ToArray();
        var preShuffleIds = new[] { S9Kit.LightInfantryId }.Concat(beforeIds).ToArray();
        using var recorder = new UpdateRecorder(match.Engine);

        var result = await match.CardService.CreateAndPlaceIntoDeckShuffledAsync(S9Kit.LightInfantryId, playerA);

        Assert.Equal(CardPlaceStatus.Placed, result.Status);

        // 洗入＝装入卡组＋执行既有统一洗切动作面——发 deck.shuffled 恰一次（载荷 { Player, Deck }）
        Assert.Equal(1, recorder.CountOf(GameUpdates.DeckShuffled));
        var payload = recorder.PayloadOf(GameUpdates.DeckShuffled)!;
        Assert.Same(playerA, payload[GameUpdates.PayloadPlayer]);
        Assert.Same(playerA.Deck, payload[GameUpdates.PayloadDeck]);

        // 卡组含生成的卡（装入成功）＋数量 +1
        Assert.Equal(beforeIds.Length + 1, playerA.Deck.Count);
        Assert.True(playerA.Deck.ContainsInstance(result.Card!));

        // 洗切随机性（确定性种子可断言）：洗切后序列 ≠ 装入后未洗切序列——洗切确实发生（插入后洗切复合语义）
        var afterIds = Enumerable.Range(0, playerA.Deck.Count).Select(i => playerA.Deck[i]).ToArray();
        Assert.NotEqual(preShuffleIds, afterIds);
    }

    [Fact]
    public async Task Scenario4_Context_Reachability_Via_Effect_Deck_Top()
    {
        var registry = new CardEffectRegistry();
        registry.Register(S9Kit.ProbeEffectId,
            _ => new CardServiceSceneEffect(CardServiceSceneEffect.SceneOp.ToDeckTop, S9Kit.LightInfantryId));
        registry.Declare(S9Kit.EmitterId, new[] { S9Kit.ProbeEffectId });

        var match = S9Kit.CreateSceneMatch(effectRegistry: registry);
        await match.Initialize();
        var playerA = match.Players[0];

        var (_, effect) = await S9Kit.PrepareEmitterAsync(match, playerA);

        await effect.CastAsync(match.Engine);

        // 上下文可达性专项：经效果/上下文路径完成一次该场景操作＋断言其结果（卡组顶＝取件端）
        Assert.True(effect.ServiceResolved);
        Assert.Equal(CardPlaceStatus.Placed, effect.LastResult!.Status);
        Assert.Same(effect.LastResult.Card, playerA.Deck.DrawInstance());
    }

    // ---------- ⑤ 复制（端到端：定义级/新实例/源不受影响） ----------

    [Fact]
    public async Task Scenario5_Copy_Is_Definition_Level_New_Instance_Source_Unaffected()
    {
        var registry = new CardEffectRegistry();
        registry.Register(S9Kit.ProbeEffectId,
            _ => new CardServiceSceneEffect(CardServiceSceneEffect.SceneOp.CopyToHand, definitionId: ""));
        registry.Declare(S9Kit.EmitterId, new[] { S9Kit.ProbeEffectId });

        var match = S9Kit.CreateSceneMatch(effectRegistry: registry);
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        // 源＝敌方「可见」卡实例（本批无可见性系统——以可见卡替代「明牌」，边界披露）：敌方场上单位，
        // 且带实例状态（损伤/修饰/类型增补）以验证「复制不携带实例状态」。
        var source = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        await source.ApplyDefenseDamageAsync(1);
        await source.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 5, "copy-source"));
        await source.AddUnitTypeAsync(UnitType.Bomber);

        var (_, effect) = await S9Kit.PrepareEmitterAsync(match, playerA);
        effect.Source = source;
        using var recorder = new UpdateRecorder(match.Engine);
        var handBefore = playerA.Hand.Count;

        await effect.CastAsync(match.Engine);

        // 结果：经放置面进入己方手牌（来源敌方、落点己方——组合成立）
        var result = effect.LastResult;
        Assert.NotNull(result);
        Assert.Equal(CardPlaceStatus.Placed, result!.Status);
        var copy = Assert.IsType<UnitCard>(result.Card);
        Assert.Same(copy, playerA.Hand[^1]);
        Assert.Equal(handBefore + 1, playerA.Hand.Count);

        // 「复制须新实例」：两实例不同一、归属独立（源卡仅提供定义，不传递归属）
        Assert.NotSame(source, copy);
        Assert.Same(playerA, copy.Owner);
        Assert.Same(playerB, source.Owner);

        // 复制＝静态定义级（不携带实例状态）：新实例未放置（未单位化）、无修饰、无原实例状态
        Assert.False(copy.TryGetData<UnitStateData>(out _));
        Assert.Empty(copy.Modifiers.All);
        Assert.True(match.IsOutsideDeck(copy)); // 运行时创建＝构筑外（ID ＞ 水位线）

        // 源实例不受影响（状态与归属）：损伤/修饰/类型增补/在场原样保持
        var sourceState = source.GetData<UnitStateData>();
        Assert.Equal(1, sourceState.DefenseLoss);
        Assert.Contains(UnitType.Bomber, sourceState.UnitTypes);
        Assert.NotEmpty(source.Modifiers.All);
        Assert.Same(source, match.Battlefield.GetSupportLine(playerB)[1].Occupant);

        // （放入手牌时）card.hand.add 恰一次
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Same(copy, recorder.PayloadOf(GameUpdates.CardHandAdd)![GameUpdates.PayloadCard]);
    }

    // ---------- ⑥ 转换（端到端组合链：同槽位/静态/离场销毁/信号/计数） ----------

    [Fact]
    public async Task Scenario6_Transform_Rebuilds_Instance_At_Same_Slot()
    {
        var registry = new CardEffectRegistry();
        registry.Register(S9Kit.ProbeEffectId,
            _ => new CardServiceSceneEffect(CardServiceSceneEffect.SceneOp.Transform, S9Kit.EliteId));
        registry.Declare(S9Kit.EmitterId, new[] { S9Kit.ProbeEffectId });

        var match = S9Kit.CreateSceneMatch(effectRegistry: registry);
        await match.Initialize();
        var playerA = match.Players[0];
        var slot = match.Battlefield.GetSupportLine(playerA)[1];

        // 原实例（步兵——槽 1；带实例状态：损伤 1 ＋ 类型增补〔Artillery〕＋ 修饰 +2 攻）
        var oldUnit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        await oldUnit.ApplyDefenseDamageAsync(1);
        await oldUnit.AddUnitTypeAsync(UnitType.Artillery);
        await oldUnit.Modifiers.AddModifierAsync(new AddModifier(CardStatFields.Attack, 2, "transform-test"));
        var oldState = oldUnit.GetData<UnitStateData>();
        var unitCountBefore = S9Kit.CountAliveUnitsOf(match, playerA);

        var (_, effect) = await S9Kit.PrepareEmitterAsync(match, playerA);
        effect.Source = oldUnit;
        using var recorder = new UpdateRecorder(match.Engine);

        await effect.CastAsync(match.Engine);

        // 组合链完成（原实例离场/销毁 → 按目标定义创建静态新实例 → 放回原位置）
        var result = effect.LastResult;
        Assert.NotNull(result);
        Assert.Equal(CardPlaceStatus.Placed, result!.Status);
        var newUnit = Assert.IsType<UnitCard>(result.Card);

        // ① 新实例位于原槽位
        Assert.Same(newUnit, slot.Occupant);

        // ② 静态（满状态：无损伤、无修饰；无原状态携带：类型＝目标定义〔不含原实例增补〕）
        var newState = newUnit.GetData<UnitStateData>();
        Assert.Equal(0, newState.DefenseLoss);
        Assert.Equal(4, S9Kit.DefenseOf(newUnit)); // 精锐兵定义防御（满状态）
        Assert.Empty(newUnit.Modifiers.All);
        Assert.Equal(new[] { UnitType.Tank }, newState.UnitTypes);
        Assert.DoesNotContain(UnitType.Artillery, newState.UnitTypes); // 原实例增补不携带（静态性交叉）

        // ③ 原实例已离场且已销毁（非死亡路径：不冒充「被消灭」）
        Assert.False(oldUnit.Life.IsAlive);
        Assert.Null(oldState.Position);
        Assert.False(oldState.IsDestroyed);

        // ④ 信号：card.destroyed 恰一次 ＋ unit.joined 恰一次；无 card.died / card.discarded / unit.deployed
        Assert.Equal(1, recorder.CountOf(Updates.CardDestroyed));
        Assert.Equal(1, recorder.CountOf(GameUpdates.UnitJoined));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardDied));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardDiscarded));
        Assert.Equal(0, recorder.CountOf(GameUpdates.UnitDeployed));

        // ⑤ 场上计数一致（替换前后 1 → 1）
        Assert.Equal(unitCountBefore, S9Kit.CountAliveUnitsOf(match, playerA));
        Assert.Equal(1, S9Kit.CountAliveUnitsOf(match, playerA));

        // 备注：「转换为上 1 个被消灭的友方单位」＝事件流读取（S10）后组合验证——本批不验（边界固化）。
    }

    [Fact]
    public async Task Scenario6_Transform_Triggers_Leave_Notification_Breaks_Pincer()
    {
        var registry = new CardEffectRegistry();
        registry.Register(S9Kit.ProbeEffectId,
            _ => new CardServiceSceneEffect(CardServiceSceneEffect.SceneOp.Transform, S9Kit.LightInfantryId));
        registry.Declare(S9Kit.EmitterId, new[] { S9Kit.ProbeEffectId });

        var bridge = new MockTargeterBridge();
        var match = S9Kit.CreateSceneMatch(bridge, registry);
        await match.Initialize();
        var playerA = match.Players[0];
        var supportLine = match.Battlefield.GetSupportLine(playerA);

        // 形成钳击配对（部署链收尾形成——先例同构）：friend ＋ 钳击兵 → 双方 +2 攻击
        var friend = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 3);
        var pincer = await CommandTestKit.InstantiateLoadedAsync(match, playerA, S9Kit.PincerUnitId, toHand: true);
        bridge.CollectScript = CommandTestKit.AllRefsScript(match);
        bridge.InteractionScript = TargeterTestKit.AutoCompleteWithFirstAllowed();
        var deploy = await match.PlayManager.PlayUnitAsync(pincer, supportLine[1]);
        Assert.Equal(PlayResultStatus.Success, deploy.Status);
        Assert.Equal(4, S9Kit.AttackOf(friend)); // 2 + 2（配对加成）

        var (_, effect) = await S9Kit.PrepareEmitterAsync(match, playerA);
        effect.Source = pincer;

        await effect.CastAsync(match.Engine);

        // 第六维（离场通知接入验证——代表性关系场景）：转换（离场）触发离场失效通知——另一方即时失去
        Assert.Equal(CardPlaceStatus.Placed, effect.LastResult!.Status);
        Assert.Equal(2, S9Kit.AttackOf(friend)); // +2 撤销（即时失去）
        Assert.False(match.CommandManager.Pincers.IsPaired(pincer));
        Assert.False(match.CommandManager.Pincers.IsPaired(friend));

        // 转换落位原位（新实例（轻步兵）占原槽）
        Assert.Same(effect.LastResult.Card, supportLine[1].Occupant);
    }

    [Fact]
    public async Task Scenario6_Transform_Failure_Keeps_Source_Intact()
    {
        var registry = new CardEffectRegistry();
        registry.Register(S9Kit.ProbeEffectId,
            _ => new CardServiceSceneEffect(CardServiceSceneEffect.SceneOp.Transform, "u_s9_missing"));
        registry.Declare(S9Kit.EmitterId, new[] { S9Kit.ProbeEffectId });

        var match = S9Kit.CreateSceneMatch(effectRegistry: registry);
        await match.Initialize();
        var playerA = match.Players[0];
        var slot = match.Battlefield.GetSupportLine(playerA)[1];

        var oldUnit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var oldState = oldUnit.GetData<UnitStateData>();

        var (_, effect) = await S9Kit.PrepareEmitterAsync(match, playerA);
        effect.Source = oldUnit;

        await effect.CastAsync(match.Engine);

        // 「先判定/先就绪后替换」：目标定义不存在＝创建失败先于任何替换——原实例状态完好（「旧已毁、新未成」禁止）
        Assert.IsType<KeyNotFoundException>(effect.LastError);
        Assert.Same(oldUnit, slot.Occupant);
        Assert.True(oldUnit.Life.IsAlive);
        Assert.False(oldState.IsDestroyed);
        Assert.Same(slot, oldState.Position);
        Assert.Equal(1, S9Kit.CountAliveUnitsOf(match, playerA));
    }

    // ---------- ⑦ 类型增补（受控入口＋信号；判定读点受益；专项） ----------

    [Fact]
    public async Task Scenario7_Type_Augmentation_Controlled_Entry_Signal_And_Benefits()
    {
        var match = S9Kit.CreateSceneMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        using var recorder = new UpdateRecorder(match.Engine);
        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var other = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        var attacker = other;

        // 装配期填充静默：入场（单位化从定义填充）不发射 unit.types.changed（初始化设定 ≠ 变更）
        Assert.Equal(0, recorder.CountOf(GameUpdates.UnitTypesChanged));

        // 判定读点受益（增补前基线）：目标非轰炸机 → 可反击
        // （K2：经 combat.counter.eligibility 判定器条目读值——单源口径，断言强度保持）
        Assert.Equal(new object[] { true },
            match.Judicators.Resolve(JudicatorNames.CombatCounterEligibility).Invoke(new object[] { attacker, unit }));

        await unit.AddUnitTypeAsync(UnitType.Bomber);

        // ① 既有判定读点即时可见（无需修改读点代码——反击豁免判定：目标＝轰炸机 → 永不反击；
        // K2：经判定器条目读值——单源口径，断言强度保持）
        Assert.Equal(new object[] { false },
            match.Judicators.Resolve(JudicatorNames.CombatCounterEligibility).Invoke(new object[] { attacker, unit }));
        Assert.Contains(UnitType.Bomber, unit.GetData<UnitStateData>().UnitTypes);

        // ② 信号恰一次（载荷＝{ Unit, AddedType }——增量）
        Assert.Equal(1, recorder.CountOf(GameUpdates.UnitTypesChanged));
        var payload = recorder.PayloadOf(GameUpdates.UnitTypesChanged)!;
        Assert.Same(unit, payload[GameUpdates.PayloadUnit]);
        Assert.Equal(UnitType.Bomber, payload[GameUpdates.PayloadAddedType]);

        // ③ 去重（幂等：不重复登记、不重复发信号）
        await unit.AddUnitTypeAsync(UnitType.Bomber);
        Assert.Equal(1, recorder.CountOf(GameUpdates.UnitTypesChanged));
        Assert.Equal(1, unit.GetData<UnitStateData>().UnitTypes.Count(t => t == UnitType.Bomber));

        // ④ 实例级（仅该实例——不写回定义、不迁移同定义其他实例）
        Assert.DoesNotContain(UnitType.Bomber, other.GetData<UnitStateData>().UnitTypes);
        Assert.DoesNotContain(UnitType.Bomber, unit.Definition.UnitTypes);

        // 校验分层：未定义枚举值＝拒绝；未单位化（无单位数据）＝明确异常
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => unit.AddUnitTypeAsync("未定义兵种"));
        var loose = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => loose.AddUnitTypeAsync(UnitType.Tank));
    }

    [Fact]
    public async Task Scenario7_Context_Reachability_Via_Effect_Augments_Type()
    {
        var registry = new CardEffectRegistry();
        registry.Register(S9Kit.ProbeEffectId,
            _ => new CardServiceSceneEffect(CardServiceSceneEffect.SceneOp.AddUnitType, definitionId: ""));
        registry.Declare(S9Kit.EmitterId, new[] { S9Kit.ProbeEffectId });

        var match = S9Kit.CreateSceneMatch(effectRegistry: registry);
        await match.Initialize();
        var playerA = match.Players[0];

        var target = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var (_, effect) = await S9Kit.PrepareEmitterAsync(match, playerA);
        effect.Source = target;
        effect.AddedType = UnitType.Artillery;
        using var recorder = new UpdateRecorder(match.Engine);

        await effect.CastAsync(match.Engine);

        // 上下文可达性专项：经效果执行链完成一次该场景操作＋断言其结果（类型增补生效——信号真实发出）
        Assert.True(effect.ServiceResolved);
        Assert.Contains(UnitType.Artillery, target.GetData<UnitStateData>().UnitTypes);
        Assert.Equal(1, recorder.CountOf(GameUpdates.UnitTypesChanged));
    }

    // ---------- 差异点：定义不存在 / 槽占用失败回收静默 / 跨玩家 / 重复放置 ----------

    [Fact]
    public async Task Failure_Missing_Definition_Throws_Without_Residue()
    {
        var match = S9Kit.CreateSceneMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        using var recorder = new UpdateRecorder(match.Engine);
        var handBefore = playerA.Hand.Count;

        // 定义不存在/未注册＝输入契约无效（异常——对齐 CardLibrary 既有「未注册 → 抛错」先例）；零残留
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => match.CardService.CreateAndPlaceToHandAsync("u_s9_missing", playerA));

        Assert.Equal(handBefore, playerA.Hand.Count);
        Assert.Equal(0, recorder.TotalCount());
    }

    [Fact]
    public async Task Failure_Slot_Occupied_Rejected_And_Instance_Recycled_Silently()
    {
        var match = S9Kit.CreateSceneMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var slot = match.Battlefield.GetSupportLine(playerA)[1];
        await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1); // 占用槽 1

        using var recorder = new UpdateRecorder(match.Engine);
        var result = await match.CardService.CreateAndPlaceToSupportLineAsync(S9Kit.LightInfantryId, playerA, slot);

        // 业务失败（结果对象、不抛）：槽位非空
        Assert.Equal(CardPlaceStatus.Rejected, result.Status);
        Assert.Equal(CardPlaceFailureReason.TargetSlotOccupied, result.FailureReason);

        // 服务侧回收（失败自动回收——「不产生半放置态」服务侧内建保证、不依赖调用方）：已创建实例被销毁
        var recycled = Assert.IsType<UnitCard>(result.Card);
        Assert.False(recycled.Life.IsAlive);
        Assert.False(recycled.TryGetData<UnitStateData>(out _)); // 未单位化（未放置）——无半放置态

        // 回收静默（游戏层信号零发射）：无 unit.joined / hand.add / discarded；
        // 创建事实（card.load）恰一次；回收经销毁面（引擎级 card.destroyed 恰一次——「回收场景可接受」口径，实现选取并记录）
        Assert.Equal(0, recorder.CountOf(GameUpdates.UnitJoined));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardDiscarded));
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardLoad));
        Assert.Equal(1, recorder.CountOf(Updates.CardDestroyed));

        // 无未归属残留：实例不在任何容器
        Assert.DoesNotContain(recycled, playerA.Hand);
        Assert.False(playerA.Deck.ContainsInstance(recycled));
    }

    [Fact]
    public async Task Failure_Cross_Player_Placement_Rejected()
    {
        var match = S9Kit.CreateSceneMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        var card = await match.CardService.CreateAsync(S9Kit.LightInfantryId, playerA);

        // 跨玩家放置＝调用方组合错误（创建即按落点预归属）——明确拒绝（异常）、零副作用
        await Assert.ThrowsAsync<InvalidOperationException>(() => match.CardService.PlaceToHandAsync(card, playerB));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => match.CardService.PlaceToDeckTopAsync(card, playerB));

        Assert.True(card.Life.IsAlive); // 实例未被破坏
        Assert.DoesNotContain(card, playerA.Hand);
        Assert.DoesNotContain(card, playerB.Hand);
    }

    [Fact]
    public async Task Failure_Duplicate_Placement_Rejected()
    {
        var match = S9Kit.CreateSceneMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        var result = await match.CardService.CreateAndPlaceToHandAsync(S9Kit.LightInfantryId, playerA);
        var card = result.Card!;

        // 重复放置既有归属实例＝拒绝（实例唯一归属不变量——手牌/卡组都拒绝）
        await Assert.ThrowsAsync<InvalidOperationException>(() => match.CardService.PlaceToHandAsync(card, playerA));
        await Assert.ThrowsAsync<InvalidOperationException>(() => match.CardService.PlaceToDeckTopAsync(card, playerA));
        Assert.Same(card, playerA.Hand[^1]); // 原归属保持
    }

    [Fact]
    public async Task Failure_Category_Mismatch_Rejected_Preventively()
    {
        var match = S9Kit.CreateSceneMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        using var recorder = new UpdateRecorder(match.Engine);

        // 放置类别约束不匹配（非单位定义放置到支援线）＝业务失败（结果对象）——预防式：先判定不创建实例（零副作用）
        var result = await match.CardService.CreateAndPlaceToSupportLineAsync(
            CommandTestKit.OrderCardId, playerA, match.Battlefield.GetSupportLine(playerA)[1]);

        Assert.Equal(CardPlaceStatus.Rejected, result.Status);
        Assert.Equal(CardPlaceFailureReason.CategoryMismatch, result.FailureReason);
        Assert.Null(result.Card);
        Assert.Equal(0, recorder.TotalCount());
    }

    [Fact]
    public async Task Failure_Dead_Unit_Placement_Rejected()
    {
        var match = S9Kit.CreateSceneMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        var unit = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        await unit.ApplyDefenseDamageAsync(999); // 防御归零 → 统一死亡衔接（置毁＋清位）
        Assert.True(unit.GetData<UnitStateData>().IsDestroyed);

        // 尸体不可放置（前置契约——明确拒绝、零副作用）
        await Assert.ThrowsAsync<InvalidOperationException>(() => match.CardService.PlaceToHandAsync(unit, playerA));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => match.CardService.PlaceToDeckTopAsync(unit, playerA));
        Assert.DoesNotContain(unit, playerA.Hand);
    }

    // ---------- 读辅助（复制/转换组合的「读源卡定义」读面） ----------

    [Fact]
    public async Task Definition_Id_Read_Helper_Resolves_Registered_Only()
    {
        var match = S9Kit.CreateSceneMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        var registered = await match.CardService.CreateAsync(S9Kit.LightInfantryId, playerA);
        Assert.True(match.CardService.TryResolveDefinitionId(registered, out var id));
        Assert.Equal(S9Kit.LightInfantryId, id);

        // 未注册（独立构造的定义——不经对局卡库）：不可读＝false（不抛错）
        var unregistered = new UnitCard(match.Engine, new CardDefinition(
            "外域兵", 1, 1, 1, 1, unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard));
        Assert.False(match.CardService.TryResolveDefinitionId(unregistered, out _));

        // 已销毁实例仍可按其定义读取（「按定义创建」不依赖源实例存活——支持事件流回溯类场景）
        var doomed = await match.CardService.CreateAsync(S9Kit.LightInfantryId, playerA);
        await match.Engine.DestroyCard(doomed);
        Assert.True(match.CardService.TryResolveDefinitionId(doomed, out var doomedId));
        Assert.Equal(S9Kit.LightInfantryId, doomedId);
    }

    // ---------- 门禁（准备态 / 终局后） ----------

    [Fact]
    public async Task Gate_Preparing_And_Ended_States_Rejected()
    {
        var match = S9Kit.CreateSceneMatch();

        // 准备态：服务不可取用（随对局装配——门禁与随机服务取用门禁同构）
        Assert.Throws<InvalidOperationException>(() => match.CardService);

        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var service = match.CardService; // 终局前保留引用（验证「服务操作自身门禁」）
        Assert.NotNull(service);

        // 终局：HQ 归零 → 结束态
        await playerB.Hq.ApplyDamageAsync(999);
        Assert.Equal(MatchState.Ended, match.State);

        // 终局后：属性取用拒绝＋服务操作拒绝（明确异常——门禁归入「调用方错误」层）
        Assert.Throws<InvalidOperationException>(() => match.CardService);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAndPlaceToHandAsync(S9Kit.LightInfantryId, playerA));
    }

    // ---------- 独立构造（服务构造不绑死对局；可注入可测试） ----------

    [Fact]
    public async Task Independent_Construction_Service_Can_Be_Driven_Directly()
    {
        var match = S9Kit.CreateSceneMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        // 独立构造（不经对局装配路径——测试自行构造；依赖以可替换形态注入：核心面＝engine＋library；
        // 阵线/洗切/状态读取器缺省＝相关面明确不可用）。说明：驱动素材（Player 对象）取自对局——
        // Player 构造为 internal，「不建完整对局」在现有可见性下的可达形态＝不经对局装配的独立实例
        // （沿用「不要求零依赖」口径，逐条记录）。
        var standalone = new MatchCardService(match.Engine, match.CardLibrary);

        // 核心面可独立驱动（创建＋手牌放置——不经对局面动作）
        var result = await standalone.CreateAndPlaceToHandAsync(S9Kit.LightInfantryId, playerA);
        Assert.Equal(CardPlaceStatus.Placed, result.Status);
        Assert.Same(result.Card, playerA.Hand[^1]);

        // 对局面动作缺省＝明确拒绝（不静默）：阵线放置 / 洗入
        var loose = (UnitCard)await standalone.CreateAsync(S9Kit.LightInfantryId, playerA);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => standalone.PlaceToSupportLineAsync(loose, playerA, match.Battlefield.GetSupportLine(playerA)[1]));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => standalone.CreateAndPlaceIntoDeckShuffledAsync(S9Kit.LightInfantryId, playerA));

        // 装配路径实例（Match.CardService）与独立实例共享同一实现：对照驱动成功（不双实现）
        var viaMatch = await match.CardService.CreateAndPlaceToHandAsync(S9Kit.LightInfantryId, playerA);
        Assert.Equal(CardPlaceStatus.Placed, viaMatch.Status);
    }

    // ---------- 脱局降级（效果桩：服务不可解析＝功能不可用、不抛错、不失败） ----------

    [Fact]
    public async Task Detached_Effect_Execution_Degrades_Without_Error()
    {
        var engine = new LogicEngine();
        var standalone = new UnitCard(engine, new CardDefinition(
            "独立演示兵", 1, 1, 2, 3, unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard));
        var effect = new CardServiceSceneEffect(CardServiceSceneEffect.SceneOp.ToHand, S9Kit.LightInfantryId);
        standalone.AddEffect(effect);

        await effect.CastAsync(engine); // 不抛（服务解析＝null → 效果跳过）

        Assert.False(effect.ServiceResolved);
        Assert.Null(effect.LastResult);
    }

    // ---------- 相邻解析读面（服务面查询——口径同源与空线形态） ----------

    [Fact]
    public async Task Adjacent_Resolution_Query_Face_Matches_BattleLine_Rules()
    {
        var match = S9Kit.CreateSceneMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var supportLine = match.Battlefield.GetSupportLine(playerA);

        // 空线（仅 HQ 占位）：HQ 两侧邻位空槽 [1, 3] → 候选 [1, 3]
        Assert.Equal(new[] { supportLine[1], supportLine[3] }, match.CardService.GetAdjacentEmptySlots(playerA));

        // 单位落位后：HQ（槽 2）＋单位（槽 4）→ 候选 [1, 3]（去重、索引升序）
        await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 3);
        Assert.Equal(new[] { supportLine[1], supportLine[3] }, match.CardService.GetAdjacentEmptySlots(playerA));

        // 与既有「邻位动态候选」单源一致（同一实现，不另建规则）
        Assert.Equal(supportLine.GetAdjacentEmptySlots().ToArray(), match.CardService.GetAdjacentEmptySlots(playerA));
    }
}
