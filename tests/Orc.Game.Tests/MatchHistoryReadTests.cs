using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// S10 功能点③验收（被消灭历史——自写 handler〔实时〕＋事件流读取〔回溯〕；不建专门死亡记录）：
/// ③1 读取面（最小历史读取面——按类型筛取＋时序取最近；读出的卡引用可直接消费）；
/// ③2 场景（连续消灭〔友/敌混合〕→ 读取「上1个被消灭的友方单位」〔最近＋友方过滤〕；无符合＝空/无结果、不抛错）；
/// ③3 组合闭环（读取 → S9 转换组合形态重建到转换目标位置——旧实例离场/销毁、新实例按定义就位、位置正确）；
/// ③4 边界核验（信号契约 18 条不变；读取来源＝事件流——无专门死亡记录结构）。
/// 「自写 handler（实时）」半边沿用既有模式（G14HistoryCountTests 先例——不新增构件）。
/// </summary>
public class MatchHistoryReadTests
{
    // ---------- ③1 读取面（按类型筛取＋时序取用＋可消费性） ----------

    [Fact]
    public async Task History_Read_Filters_By_Type_And_Takes_Last_In_Write_Order()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();

        var history = match.HistoryService;

        // 读取窗口＝对局自创建以来、写入时序全程（初始化信号已可读）：deck.shuffled ×2、card.load ×20
        var shuffled = history.GetEntries(GameUpdates.DeckShuffled);
        Assert.Equal(2, shuffled.Count);
        Assert.Same(match.Players[0], shuffled[0].Data[GameUpdates.PayloadPlayer]); // 写入时序：玩家索引升序
        Assert.Same(match.Players[1], shuffled[1].Data[GameUpdates.PayloadPlayer]);

        var loads = history.GetEntries(GameUpdates.CardLoad);
        Assert.Equal(20, loads.Count); // 双方卡组 10＋10 逐张加载
        Assert.All(loads.Take(10), e => Assert.Same(match.Players[0], e.Data[GameUpdates.PayloadPlayer]));
        Assert.All(loads.Skip(10), e => Assert.Same(match.Players[1], e.Data[GameUpdates.PayloadPlayer]));

        // 时序取用：「上1个」＝写入时序的最后一条（最近）——turn.start.after 为初始化序列末条
        var lastTurnAfter = history.GetLastEntry(GameUpdates.TurnStartAfter);
        Assert.NotNull(lastTurnAfter);
        Assert.Same(lastTurnAfter, history.GetEntries(GameUpdates.TurnStartAfter).Last());

        // 条目可直接供消费：载荷含对象引用（turn 载荷＝{ Player, TurnNumber }）
        Assert.Same(match.Players[0], lastTurnAfter!.Data[GameUpdates.PayloadPlayer]);
        Assert.Equal(1, lastTurnAfter.Data[GameUpdates.PayloadTurnNumber]);

        // 快照语义：返回为新列表（其后新增条目不影响已返回结果）
        var before = history.GetEntries(GameUpdates.CardDrawn);
        await match.EndTurn(); // 回合 2（B）抽牌 → card.drawn 新增一条
        Assert.Empty(before);
        Assert.Single(history.GetEntries(GameUpdates.CardDrawn));
    }

    [Fact]
    public async Task History_No_Match_Returns_Empty_And_Null_Without_Throwing()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();

        // 无符合条件的死亡：明确空/无结果、不抛错
        Assert.Empty(match.HistoryService.GetEntries(GameUpdates.CardDied));
        Assert.Null(match.HistoryService.GetLastEntry(GameUpdates.CardDied));
        Assert.Null(match.HistoryService.GetLastEntry(GameUpdates.CardDied, _ => true));

        // 谓词无命中：同样 null（不抛错）
        Assert.Null(match.HistoryService.GetLastEntry(GameUpdates.DeckShuffled, _ => false));
    }

    // ---------- ③2 连续消灭（友/敌混合）→「上1个被消灭的友方单位」 ----------

    [Fact]
    public async Task Last_Died_Friendly_Unit_Read_Filters_Recent_And_Owned()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        // 友/敌混合的连续消灭：A 两个、B 一个（脆皮 防 2——经伤害门户致死＝防御归零统一死亡衔接）
        var aWeak1 = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.WeakId, 0);
        var aWeak2 = await CommandTestKit.PrepareOnFrontAsync(match, playerA, CommandTestKit.WeakId, 1);
        var bWeak = await CommandTestKit.PrepareOnFrontAsync(match, playerB, CommandTestKit.WeakId, 2);

        await aWeak1.ApplyDefenseDamageAsync(2); // ① A 死 1（友方——较早）
        await aWeak2.ApplyDefenseDamageAsync(2); // ② A 死 2（友方——最近友方）
        await bWeak.ApplyDefenseDamageAsync(2);  // ③ B 死 1（敌方——全序列最近）

        Assert.True(aWeak1.GetData<UnitStateData>().IsDestroyed);
        Assert.True(aWeak2.GetData<UnitStateData>().IsDestroyed);
        Assert.True(bWeak.GetData<UnitStateData>().IsDestroyed);

        var history = match.HistoryService;
        Assert.Equal(3, history.GetEntries(GameUpdates.CardDied).Count);

        // 「上1个被消灭的友方单位」＝最近（写入时序最后）＋友方过滤（死亡卡归属玩家==读取方——以死亡时归属为准）
        var lastFriendlyA = history.GetLastEntry(
            GameUpdates.CardDied,
            entry => entry.Data[GameUpdates.PayloadCard] is CardBase died && ReferenceEquals(died.Owner, playerA));
        Assert.NotNull(lastFriendlyA);
        Assert.Same(aWeak2, lastFriendlyA!.Data[GameUpdates.PayloadCard]); // 非 aWeak1（证明「最近」）、非 bWeak（证明「友方过滤」）

        // 敌方视角对照：B 读到己方那个死者（不误取 A 的）
        var lastFriendlyB = history.GetLastEntry(
            GameUpdates.CardDied,
            entry => entry.Data[GameUpdates.PayloadCard] is CardBase died && ReferenceEquals(died.Owner, playerB));
        Assert.Same(bWeak, lastFriendlyB!.Data[GameUpdates.PayloadCard]);

        // 死亡卡归属保持（以死亡时归属为准的判据来源）
        Assert.Same(playerA, aWeak2.Owner);
    }

    // ---------- ③3 组合闭环：读取 → S9 转换组合形态重建 ----------

    [Fact]
    public async Task Read_Then_Transform_Rebuild_End_To_End()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        // 消灭一个友方单位（防御归零统一死亡衔接）
        var weak = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.WeakId, 1);
        await weak.ApplyDefenseDamageAsync(2);
        Assert.True(weak.GetData<UnitStateData>().IsDestroyed);

        // ① 读取「上1个被消灭的友方单位」——读出的卡引用直接供后续消费
        var entry = match.HistoryService.GetLastEntry(
            GameUpdates.CardDied,
            e => e.Data[GameUpdates.PayloadCard] is CardBase died && ReferenceEquals(died.Owner, playerA));
        Assert.NotNull(entry);
        var diedCard = Assert.IsAssignableFrom<CardBase>(entry!.Data[GameUpdates.PayloadCard]);
        Assert.Same(weak, diedCard);

        // ② S9 转换组合形态：旧实例离场/销毁 → 按定义 Create → 放置到转换目标位置
        // （离场已由死亡链完成〔Position 置空、槽位释放〕；销毁经引擎销毁面补全——转换组合的「销毁」步）
        Assert.Null(diedCard.GetData<UnitStateData>().Line);
        await match.Engine.DestroyCard(diedCard);
        Assert.False(diedCard.Life.IsAlive);

        var service = match.CardService;
        Assert.True(service.TryResolveDefinitionId(diedCard, out var definitionId));

        var target = match.Battlefield.GetSupportLine(playerA).GetInsertionGaps().At(1); // 转换目标位置（原位置——已被死亡链释放为空槽）
        var result = await service.CreateAndPlaceToSupportLineAsync(definitionId!, playerA, target);

        // 组合断言：新实例按定义就位、位置正确
        Assert.Equal(CardPlaceStatus.Placed, result.Status);
        var rebuilt = result.Card!;
        Assert.Same(diedCard.Definition, rebuilt.Definition); // 按定义重建
        Assert.True(rebuilt.TryGetData<UnitStateData>(out var rebuiltState));
        Assert.Same(target, rebuiltState.Line);
        Assert.Same(rebuilt, target);
    }

    // ---------- ③4 边界核验（信号契约 18 条不变；读取来源＝事件流） ----------

    [Fact]
    public async Task Signal_Contract_Literals_Are_Frozen_And_History_Reads_From_Event_Stream()
    {
        // 18 条信号契约不变（字面值冻结——核对元素面与关键值）
        var signalLiterals = new[]
        {
            GameUpdates.TurnStartBefore, GameUpdates.TurnStart, GameUpdates.TurnStartAfter,
            GameUpdates.TurnEndBefore, GameUpdates.TurnEnd,
            GameUpdates.CardPlayed, GameUpdates.CardDrawn, GameUpdates.CardStatChanged,
            GameUpdates.CardLoad, GameUpdates.CardHandAdd, GameUpdates.CardDiscarded, GameUpdates.CardBurned,
            GameUpdates.CardDied, GameUpdates.UnitJoined, GameUpdates.UnitDeployed,
            GameUpdates.UnitPositionChanged, GameUpdates.DeckShuffled, GameUpdates.UnitTypesChanged,
        };
        Assert.Equal(18, signalLiterals.Length);
        Assert.Equal(18, signalLiterals.Distinct().Count());
        Assert.Equal("card.died", GameUpdates.CardDied);
        Assert.Equal("card.discarded", GameUpdates.CardDiscarded);
        Assert.Equal("card.burned", GameUpdates.CardBurned);
        Assert.Equal("unit.types.changed", GameUpdates.UnitTypesChanged);

        // 读取来源＝事件流（无专门死亡记录结构）：读取到的条目即事件流中的同一引用
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var player = match.Players[0];
        var weak = await CommandTestKit.PrepareOnFrontAsync(match, player, CommandTestKit.WeakId, 0);
        await weak.ApplyDefenseDamageAsync(2);

        var died = match.HistoryService.GetLastEntry(GameUpdates.CardDied);
        Assert.NotNull(died);
        Assert.Contains(died!, match.Engine.RootStream.Entries); // 同一引用——读取自事件流（非另行记录）
        Assert.Same(weak, died!.Data[GameUpdates.PayloadCard]);
    }
}
