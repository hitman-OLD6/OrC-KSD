using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Targeting;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// C2「开发/发现」完整链——引用集选取语义与边界失败验收测试：
/// ② 已有实例集载荷（选取语义）：候选＝己方手牌既有实例 → 选中 → 落位到另一去向（卡组顶回迁）；
///    断言＝同一实例（引用同一性、非新建、实例计数不变）＋位置/归属变化（手牌−1、卡组首位＝该实例）
///    ＋信号面（回顶静默）＋与定义级形成对照（新实例 vs 原实例——对照映射见《实现记录-C2》；驱动＝测试内，参照 C1 演示）；
/// ②a 第二去向链式场景：生成落位侧覆盖 ≥2 去向（①手牌＋本处卡组顶；去向为链的显式调用点/参数；效果链驱动）；
/// ③ 边界与失败：引用集空候选（失败结局＋链终止＋零副作用——与 M=0 同构口径）、
///    失败负例（相邻处无空槽＝拒绝可观察＋无未归属残留）、创建后回收（放置期失败防御兜底增强断言）。
/// </summary>
public class C2ReferenceAndFailureTests
{
    // ---------- ② 已有实例集载荷（选取语义）——手牌既有实例 → 卡组顶回迁 ----------

    [Fact]
    public async Task Scenario2_Reference_Set_Select_Existing_Hand_Instance_Then_Return_To_Deck_Top()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];

        // 候选＝己方手牌既有实例（起手装载；「己方」由构造方给定候选集界定——框架归属中立）
        var handSnapshot = playerA.Hand.Cast<CardBase>().ToArray();
        Assert.Equal(4, handSnapshot.Length);
        var refs = handSnapshot.Select(card => card.Ref).ToArray();

        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        // 发起（引用集形态——构造方给定快照；不经收集与筛选链）
        var context = new TargetingRequestContext().WithSlotReferences(C2Kit.SlotName, refs);
        var targeter = match.TargeterManager.CreateTargeter(
            slots: new TargetSlot[] { new CardPickerSlot(CardPickerForm.ReferenceSet, 1, 1, C2Kit.SlotName) },
            context: context);
        var task = targeter.Targeting();
        var (description, responder) = await bridge.WaitForNextBeginAsync();

        // 交互两端①：请求描述（呈现＝卡牌阵列；载荷一致性＝引用集与构造方给定一致；槽位形态）
        Assert.Empty(bridge.CollectCalls);
        var slot = Assert.Single(description.Slots);
        Assert.Equal(C2Kit.SlotName, slot.Name);
        Assert.Equal(TargetSlotKind.CardPicker, slot.Kind);
        Assert.Equal(TargetSlotPresentation.CardArray, slot.Presentation);
        Assert.Equal(refs, slot.AllowedReferences!.ToArray());
        Assert.Null(slot.CardListings);

        // 选择既定实例（中间一张——确定性、不消费随机）
        var target = handSnapshot[2];
        Assert.True(responder.Complete(
            description.RequestId, TargeterTestKit.ReferenceSelection(C2Kit.SlotName, target.Ref)));
        var result = await task;

        // 交互两端②：产出读取（按槽位名读出卡引用）
        Assert.Equal(TargeterStatus.Ok, result.Status);
        var selected = Assert.Single(result.Outcome!.GetSelection(C2Kit.SlotName));
        var selectedCard = Assert.IsAssignableFrom<CardBase>(selected.Value);

        // 同一实例（引用同一性——非新建）；对照维度：原实例＝构筑内（ID ≤ 水位线、非构筑外）
        Assert.Same(target, selectedCard);
        Assert.False(match.IsOutsideDeck(selectedCard));

        // 选中 → 落位到另一去向（手牌 → 卡组顶；G7 回迁原子面——受控面）
        var handBefore = playerA.Hand.Count;
        var deckBefore = playerA.Deck.Count;
        var totalBefore = handBefore + deckBefore;
        match.PlayerManager.ReturnToDeckTop(playerA, selectedCard);

        // 位置/归属变化：手牌 −1、卡组 +1、卡组首位（取件端）＝该实例；实例计数不变（无新建/无销毁）
        Assert.Equal(handBefore - 1, playerA.Hand.Count);
        Assert.Equal(deckBefore + 1, playerA.Deck.Count);
        Assert.Equal(totalBefore, playerA.Hand.Count + playerA.Deck.Count);
        Assert.DoesNotContain(selectedCard, playerA.Hand);
        Assert.True(playerA.Deck.ContainsInstance(selectedCard));
        Assert.Same(selectedCard, playerA.Deck.DrawInstance()); // 取件端即该实例（下次抽取取到）

        // 信号面：回顶＝静默（无 deck.shuffled、无手牌相关信号）
        Assert.Equal(0, recorder.TotalCount());
    }

    // ---------- ②a 第二去向链式场景（生成 → 卡组顶）——去向为链的显式调用点 ----------

    [Fact]
    public async Task Scenario2a_Second_Destination_Generate_To_Deck_Top_Via_Chain()
    {
        var bridge = new MockTargeterBridge();
        var registry = C2Kit.CreateRegistry(3, C2Destination.DeckTop);
        var match = C2Kit.CreateSceneMatch(bridge, registry, seed: 42);
        await match.Initialize();
        var playerA = match.Players[0];

        var (_, effect) = await C2Kit.PrepareEmitterAsync(match, playerA);
        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();
        var deckCountBefore = playerA.Deck.Count;

        var castTask = effect.CastAsync(match.Engine);
        var (description, responder) = await bridge.WaitForNextBeginAsync();
        var chosen = effect.LastDrawnIds[2];
        Assert.True(responder.Complete(
            description.RequestId, TargeterTestKit.IdentifierSelection(C2Kit.SlotName, chosen)));
        await castTask;

        Assert.Null(effect.LastError);
        var result = effect.LastPlaceResult!;

        // 第二生成去向（卡组顶——与①手牌独立场景、独立组装）：装入取件端（下次抽取取到）
        Assert.Equal(CardPlaceStatus.Placed, result.Status);
        var generated = result.Card!;
        Assert.Equal(chosen, effect.LastSelectedId);
        Assert.Equal(deckCountBefore + 1, playerA.Deck.Count);
        Assert.Same(generated, playerA.Deck.DrawInstance());
        Assert.True(match.IsOutsideDeck(generated)); // 新实例（真经创建面）

        // 去向语义差异（卡组顶＝静默）：无 deck.shuffled / hand.add / unit.joined；创建事实（card.load）照常
        Assert.Equal(0, recorder.CountOf(GameUpdates.DeckShuffled));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Equal(0, recorder.CountOf(GameUpdates.UnitJoined));
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardLoad));
    }

    // ---------- ③ 引用集空候选（必验）——失败结局＋链终止＋零副作用 ----------

    [Fact]
    public async Task Scenario3_Empty_Reference_Set_Fails_Without_Interaction_Zero_Side_Effects()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];

        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();
        var handBefore = playerA.Hand.Count;
        var deckBefore = playerA.Deck.Count;

        // 引用集形态＋空候选（构造方给定空集——C1 既有语义＝失败〔不进交互〕）
        var context = new TargetingRequestContext().WithSlotReferences(C2Kit.SlotName, Array.Empty<Ref<Entity>>());
        var targeter = match.TargeterManager.CreateTargeter(
            slots: new TargetSlot[] { new CardPickerSlot(CardPickerForm.ReferenceSet, 1, 1, C2Kit.SlotName) },
            context: context);
        var result = await targeter.Targeting(); // 不抛断链

        // 四件套（与定义级 M=0 同构口径）：不抛断链＋链终止＋零副作用＋结局可观察
        Assert.Equal(TargeterStatus.Failed, result.Status);
        Assert.Equal(TargeterFailureReason.NoAvailableCandidates, result.Reason);
        Assert.Empty(bridge.Begins); // 不进交互（targeting 发起、无 Begin）
        Assert.Equal(0, recorder.TotalCount());
        Assert.Equal(handBefore, playerA.Hand.Count);
        Assert.Equal(deckBefore, playerA.Deck.Count);
    }

    // ---------- ③ 失败负例（相邻处无空槽）——拒绝可观察＋无未归属残留 ----------

    [Fact]
    public async Task Scenario3_Adjacent_No_Empty_Slot_Rejected_Without_Residue()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        // 填占己方支援线全部可部署格（HQ 两侧；跳过 HQ 占位槽）→ 相邻空槽解析为空（稳定构造）
        await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);
        await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 0);
        Assert.Empty(match.CardService.GetAdjacentEmptySlots(playerA)); // 前置：解析为空

        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();
        var handBefore = playerA.Hand.Count;
        var deckBefore = playerA.Deck.Count;
        var occupantsBefore = CommandTestKit.AllSlotsOf(match).Select(s => s).ToArray();

        // 复合调用（解析与选取分离——解析空＝失败、先于创建：无实例可回收）
        var result = await match.CardService.CreateAndPlaceToAdjacentAsync(
            CommandTestKit.InfantryId, playerA, _ => null);

        // 拒绝结局可观察
        Assert.Equal(CardPlaceStatus.Rejected, result.Status);
        Assert.Equal(CardPlaceFailureReason.NoAdjacentSlot, result.FailureReason);
        Assert.Null(result.Card); // 策略性失败（未创建实例——零副作用）

        // 无未归属残留＋零信号＋区域/布局不变
        Assert.Equal(0, recorder.TotalCount());
        Assert.Equal(handBefore, playerA.Hand.Count);
        Assert.Equal(deckBefore, playerA.Deck.Count);
        Assert.Equal(occupantsBefore, CommandTestKit.AllSlotsOf(match).Select(s => s).ToArray());
    }

    // ---------- ③ 增强：创建后回收（放置期失败防御兜底——「无未归属残留」强证据） ----------

    [Fact]
    public async Task Scenario3_Created_Instance_Recycled_When_Target_Slot_Occupied()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        var occupied = match.Battlefield.GetSupportLine(playerA).GetInsertionGaps().At(1);
        await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1); // 占位

        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();
        var handBefore = playerA.Hand.Count;

        // 复合调用：创建成功 → 放置失败（槽被占）→ 服务侧自动回收（「不产生半放置态」服务侧内建）
        var result = await match.CardService.CreateAndPlaceToSupportLineAsync(
            CommandTestKit.InfantryId, playerA, occupied);

        Assert.Equal(CardPlaceStatus.Rejected, result.Status);
        Assert.Equal(CardPlaceFailureReason.TargetSlotOccupied, result.FailureReason);

        // 回收可观察：关联实例已销毁（Life.IsAlive＝false）；无未归属残留（不在任何容器）
        var recycled = result.Card;
        Assert.NotNull(recycled);
        Assert.False(recycled!.Life.IsAlive);
        Assert.DoesNotContain(recycled, playerA.Hand);
        Assert.False(playerA.Deck.ContainsInstance(recycled));

        // 回收静默（游戏层信号零发射）：无 unit.joined / hand.add / discarded；创建事实（card.load）恰一次；
        // 回收经销毁面（引擎级 card.destroyed 恰一次——S9 既有回收语义）
        Assert.Equal(0, recorder.CountOf(GameUpdates.UnitJoined));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardDiscarded));
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardLoad));
        Assert.Equal(1, recorder.CountOf(Updates.CardDestroyed));
        Assert.Equal(handBefore, playerA.Hand.Count);
    }
}
