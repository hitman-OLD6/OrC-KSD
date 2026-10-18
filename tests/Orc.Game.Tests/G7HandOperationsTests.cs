using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// G7 手牌操作与弃置——原语级验收：
/// ⑤ 手牌顺序原子面——CardSet 的 MoveTo / MoveToLeft / MoveToRight（结果位置语义、自移＝无操作成功、
///    不在手牌/越界＝明确拒绝、空牌/单牌边界）、RemoveAt（返回卡引用、越界拒绝）、Peek（只读、越界拒绝）；
/// 回迁基础件——CardList 的 InsertInstanceAt（「新增条目＋装配实例」原子面、重复归属拒绝）与 ContainsInstance 读面；
/// 回迁动作——位置语义（卡组顶＝下次抽取取件端、指定位置 0..N）、三类拒绝（不在手牌/重复归属/越界）、静默（不发信号）；
/// 弃置动作——移除＋销毁（引擎既有机制：生命周期终止＋登记清除＋效果清理）＋card.discarded（恰一次、载荷正确）、
/// 归属口径＝卡当前所在手牌（他方手牌可达）、拒绝语义（不在手牌/重复弃置＝明确拒绝、非幂等）。
/// </summary>
public class G7HandOperationsTests
{
    private static UnitCard CreateUnit(LogicEngine engine, string name = "回迁单位")
        => new(engine, new CardDefinition(
            name, deployCost: 1, operateCost: 1, attack: 2, defense: 3,
            unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard));

    // ==================== ⑤ 手牌顺序原子面（CardSet；纯容器操作） ====================

    [Fact]
    public void CardSet_MoveTo_Operations_Reorder_With_Result_Position_Semantics()
    {
        var engine = new LogicEngine();
        var a = new Card(engine, "甲");
        var b = new Card(engine, "乙");
        var c = new Card(engine, "丙");
        var set = new CardSet();
        set.Add(a);
        set.Add(b);
        set.Add(c);

        set.MoveToLeft(c); // [甲,乙,丙] → [丙,甲,乙]
        Assert.Equal(new[] { c, a, b }, set.ToArray());

        set.MoveToRight(c); // → [甲,乙,丙]
        Assert.Equal(new[] { a, b, c }, set.ToArray());

        set.MoveTo(1, c); // 指定位置＝结果位置：→ [甲,丙,乙]
        Assert.Equal(new[] { a, c, b }, set.ToArray());

        set.MoveTo(0, b); // → [乙,甲,丙]
        Assert.Equal(new[] { b, a, c }, set.ToArray());
    }

    [Fact]
    public void CardSet_Move_To_Current_Position_Is_NoOp_Success()
    {
        var engine = new LogicEngine();
        var a = new Card(engine, "甲");
        var b = new Card(engine, "乙");
        var set = new CardSet();
        set.Add(a);
        set.Add(b);

        // 自移（目标位置＝当前位置）＝无操作、视为成功（结果＝集合不变）
        set.MoveTo(0, a);
        set.MoveToLeft(a);
        set.MoveToRight(b);
        set.MoveTo(1, b);
        Assert.Equal(new[] { a, b }, set.ToArray());

        // 单牌边界：全部移动＝无操作成功
        var single = new CardSet();
        var only = new Card(engine, "独");
        single.Add(only);
        single.MoveToLeft(only);
        single.MoveToRight(only);
        single.MoveTo(0, only);
        Assert.Same(only, Assert.Single(single));
    }

    [Fact]
    public void CardSet_Move_Target_Not_In_Set_Is_Rejected_Atomically()
    {
        var engine = new LogicEngine();
        var a = new Card(engine, "甲");
        var stranger = new Card(engine, "外");
        var set = new CardSet();
        set.Add(a);

        Assert.Throws<InvalidOperationException>(() => set.MoveTo(0, stranger));
        Assert.Throws<InvalidOperationException>(() => set.MoveToLeft(stranger));
        Assert.Throws<InvalidOperationException>(() => set.MoveToRight(stranger));

        // 空手牌边界：任何目标卡均「不在手牌中」＝拒绝
        var empty = new CardSet();
        Assert.Throws<InvalidOperationException>(() => empty.MoveToLeft(a));
        Assert.Throws<InvalidOperationException>(() => empty.MoveToRight(a));
        Assert.Empty(empty);

        // 拒绝＝集合不变（原子）
        Assert.Same(a, Assert.Single(set));
    }

    [Fact]
    public void CardSet_Move_Out_Of_Range_Is_Rejected()
    {
        var engine = new LogicEngine();
        var a = new Card(engine, "甲");
        var b = new Card(engine, "乙");
        var set = new CardSet();
        set.Add(a);
        set.Add(b);

        Assert.Throws<ArgumentOutOfRangeException>(() => set.MoveTo(-1, a));
        Assert.Throws<ArgumentOutOfRangeException>(() => set.MoveTo(2, a)); // ≥Count
        Assert.Equal(new[] { a, b }, set.ToArray()); // 集合不变
    }

    [Fact]
    public void CardSet_RemoveAt_Returns_Card_And_Rejects_Out_Of_Range()
    {
        var engine = new LogicEngine();
        var a = new Card(engine, "甲");
        var b = new Card(engine, "乙");
        var c = new Card(engine, "丙");
        var set = new CardSet();
        set.Add(a);
        set.Add(b);
        set.Add(c);

        var removed = set.RemoveAt(1);
        Assert.Same(b, removed); // 返回被移除的卡引用
        Assert.Equal(new[] { a, c }, set.ToArray());

        Assert.Throws<ArgumentOutOfRangeException>(() => set.RemoveAt(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => set.RemoveAt(2));
        var empty = new CardSet();
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.RemoveAt(0)); // 空集合：越界拒绝
    }

    [Fact]
    public void CardSet_Peek_Reads_By_Index_Without_Change_And_Rejects_Out_Of_Range()
    {
        var engine = new LogicEngine();
        var a = new Card(engine, "甲");
        var b = new Card(engine, "乙");
        var c = new Card(engine, "丙");
        var set = new CardSet();
        set.Add(a);
        set.Add(b);
        set.Add(c);

        Assert.Same(a, set.Peek(0)); // 最左
        Assert.Same(b, set.Peek(1));
        Assert.Same(c, set.Peek(set.Count - 1)); // 最右
        Assert.Equal(new[] { a, b, c }, set.ToArray()); // 集合不变（成员＋顺序）

        Assert.Throws<ArgumentOutOfRangeException>(() => set.Peek(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => set.Peek(3)); // 越界可区分
        var empty = new CardSet();
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.Peek(0));
    }

    // ==================== 回迁基础件（CardList：新增条目＋装配实例） ====================

    [Fact]
    public void CardList_InsertInstanceAt_Inserts_Entry_And_Attaches_Instance()
    {
        var engine = new LogicEngine();
        var list = new CardList(new[] { "c01", "c03" });
        var instance = CreateUnit(engine, "单位一");

        list.InsertInstanceAt(1, "c02", instance);

        Assert.Equal(new[] { "c01", "c02", "c03" }, list.ToArray()); // id 读面（条目化）
        Assert.Equal(3, list.Count);
        Assert.True(list.ContainsInstance(instance));

        Assert.Equal("c01", list.Draw()); // 名单通道：原条目无实例
        var drawn = list.DrawInstance(); // 新条目已装配实例：实例通道可取件
        Assert.Same(instance, drawn);

        // 尾部插入（p＝N 特例）
        var tail = CreateUnit(engine, "单位二");
        list.InsertInstanceAt(list.Count, "c04", tail);
        Assert.Equal(new[] { "c03", "c04" }, list.ToArray());
        Assert.Equal("c03", list.Draw()); // 先经名单通道取走未装配的原条目
        Assert.Same(tail, list.DrawInstance()); // 尾部插入条目已装配、可取件
    }

    [Fact]
    public void CardList_InsertInstanceAt_Is_Atomic_On_Rejection()
    {
        var engine = new LogicEngine();
        var list = new CardList(new[] { "c01" });
        var instance = CreateUnit(engine);

        Assert.Throws<ArgumentNullException>(() => list.InsertInstanceAt(0, "cX", null!)); // instance 为 null
        Assert.Throws<ArgumentOutOfRangeException>(() => list.InsertInstanceAt(-1, "cX", instance)); // 负索引
        Assert.Throws<ArgumentOutOfRangeException>(() => list.InsertInstanceAt(2, "cX", instance)); // ＞Count

        // 拒绝＝集合不变（原子）
        Assert.Equal(new[] { "c01" }, list.ToArray());
        Assert.False(list.ContainsInstance(instance));
    }

    [Fact]
    public void CardList_InsertInstanceAt_Rejects_Duplicate_Instance_Ownership()
    {
        var engine = new LogicEngine();
        var list = new CardList(new[] { "c01" });
        var instance = CreateUnit(engine, "单位一");

        list.InsertInstanceAt(0, "c01", instance);

        // 卡组侧已含同一实例＝明确拒绝（对齐既有「重复装配拒绝」——实例唯一归属不变量）
        var ex = Assert.Throws<InvalidOperationException>(() => list.InsertInstanceAt(1, "c01", instance));
        Assert.Contains("重复归属", ex.Message);
        Assert.Equal(2, list.Count); // 拒绝＝集合不变

        // ContainsInstance 读面：含 / 不含 / null 拒绝
        var other = CreateUnit(engine, "单位二");
        Assert.True(list.ContainsInstance(instance));
        Assert.False(list.ContainsInstance(other));
        Assert.Throws<ArgumentNullException>(() => list.ContainsInstance(null!));
    }

    // ==================== 回迁动作（手牌 → 卡组；跨集合受控动作） ====================

    [Fact]
    public async Task ReturnToDeck_Moves_Card_To_Position_Silently()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var card = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);
        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        var handBefore = playerA.Hand.Count;
        var deckBefore = playerA.Deck.Count;

        match.PlayerManager.ReturnToDeckTop(playerA, card);

        Assert.Equal(handBefore - 1, playerA.Hand.Count); // 手牌移出
        Assert.DoesNotContain(card, playerA.Hand);
        Assert.Equal(deckBefore + 1, playerA.Deck.Count); // 装入卡组
        Assert.True(playerA.Deck.ContainsInstance(card));
        Assert.Same(card, playerA.Deck.DrawInstance()); // 卡组顶＝下次抽取取件端
        Assert.Empty(recorder.Types); // 回迁静默（不发信号）
    }

    [Fact]
    public async Task ReturnToDeck_At_Index_One_Preserves_Position()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var card = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);

        match.PlayerManager.ReturnToDeck(playerA, card, 1); // 非顶位置回迁

        var firstOut = playerA.Deck.DrawInstance(); // 原顶（非本卡）
        Assert.NotSame(card, firstOut);
        var secondOut = playerA.Deck.DrawInstance(); // 索引 1＝本卡
        Assert.Same(card, secondOut);
    }

    [Fact]
    public async Task ReturnToDeck_Rejections_Are_Explicit_And_Atomic()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        var inHand = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);
        var notInHand = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId); // 未入手

        // 卡不在所声明玩家手牌中＝拒绝
        var ex1 = Assert.Throws<InvalidOperationException>(() => match.PlayerManager.ReturnToDeckTop(playerA, notInHand));
        Assert.Contains("不在", ex1.Message);

        // 目标位置越界＝拒绝（负 / ＞N）
        Assert.Throws<ArgumentOutOfRangeException>(() => match.PlayerManager.ReturnToDeck(playerA, inHand, -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => match.PlayerManager.ReturnToDeck(playerA, inHand, playerA.Deck.Count + 1));

        // 重复归属＝拒绝（构造重叠态：同一实例同时在手牌与卡组——正常流程不可达的防御语义）
        var deckCountBefore = playerA.Deck.Count;
        playerA.Deck.InsertInstanceAt(0, CommandTestKit.InfantryId, inHand);
        var ex2 = Assert.Throws<InvalidOperationException>(() => match.PlayerManager.ReturnToDeckTop(playerA, inHand));
        Assert.Contains("重复归属", ex2.Message);
        Assert.Equal(deckCountBefore + 1, playerA.Deck.Count); // 拒绝＝不变

        // 参数校验
        Assert.Throws<ArgumentNullException>(() => match.PlayerManager.ReturnToDeckTop(null!, inHand));
        Assert.Throws<ArgumentNullException>(() => match.PlayerManager.ReturnToDeckTop(playerA, null!));
    }

    // ==================== 弃置动作（移除＋销毁＋信号） ====================

    [Fact]
    public async Task Discard_Removes_Destroys_And_Emits_Signal()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var card = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);
        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        var handBefore = playerA.Hand.Count;
        Assert.Contains(card, match.Engine.Cards); // 销毁前：引擎登记在读

        await match.PlayerManager.DiscardCardAsync(playerA, card);

        // ① 移除（不可再使用——卡离手）
        Assert.Equal(handBefore - 1, playerA.Hand.Count);
        Assert.DoesNotContain(card, playerA.Hand);
        // ② 销毁（引擎既有机制：生命周期终止＋登记清除）
        Assert.False(card.Life.IsAlive);
        Assert.False(card.Ref.IsAlive);
        Assert.DoesNotContain(card, match.Engine.Cards);
        // ③ 信号（恰一次、载荷 { Card, Player }；销毁先于信号）
        Assert.Equal(new[] { Updates.CardDestroyed, GameUpdates.CardDiscarded }, recorder.Types);
        var discarded = recorder.Updates.Single(u => u.Type == GameUpdates.CardDiscarded);
        Assert.Same(card, discarded.Payload![GameUpdates.PayloadCard]);
        Assert.Same(playerA, discarded.Payload[GameUpdates.PayloadPlayer]);
    }

    [Fact]
    public async Task Discard_Cleans_Up_Loaded_Effects_On_Destroy()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        var card = (UnitCard)match.CardLibrary.Instantiate(CommandTestKit.InfantryId);
        var effect = new LifecycleProbeEffect("弃置清理探针");
        card.AddEffect(effect);
        await card.LoadAsync(playerA);
        playerA.Hand.Add(card);

        Assert.True(effect.IsMounted); // 加载时点装载完成
        Assert.Contains($"{effect.Name}/dispose", match.Engine.Bus.GetSubscribers(Updates.CardDestroyed));

        await match.PlayerManager.DiscardCardAsync(playerA, card);

        // 销毁接线佐证：引擎既有销毁机制被调用/生效——效果清理（卸载＋总线撤销）可观测
        Assert.Equal(1, effect.UnmountCalls);
        Assert.False(effect.IsMounted);
        Assert.DoesNotContain(effect.Name, match.Engine.Bus.GetSubscribers(Updates.EffectRemoved));
        Assert.False(card.Life.IsAlive);
        Assert.DoesNotContain(card, match.Engine.Cards);
    }

    [Fact]
    public async Task Discard_Rejections_Are_Explicit_And_NonIdempotent()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var card = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);
        var neverInHand = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId);

        await match.PlayerManager.DiscardCardAsync(playerA, card);

        // 同一卡重复弃置＝第二次因前提失败而拒绝（不幂等放行）
        var repeat = await Assert.ThrowsAsync<InvalidOperationException>(
            () => match.PlayerManager.DiscardCardAsync(playerA, card));
        Assert.Contains("不在", repeat.Message);

        // 卡不在任何手牌中（从未入手）＝拒绝
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => match.PlayerManager.DiscardCardAsync(playerA, neverInHand));

        // 参数校验
        await Assert.ThrowsAsync<ArgumentNullException>(() => match.PlayerManager.DiscardCardAsync(null!, card));
        await Assert.ThrowsAsync<ArgumentNullException>(() => match.PlayerManager.DiscardCardAsync(playerA, null!));
    }

    [Fact]
    public async Task Discard_Can_Target_Opponent_Hand_Via_Composed_Call()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerB = match.Players[1];
        var card = await CommandTestKit.InstantiateLoadedAsync(match, playerB, CommandTestKit.InfantryId, toHand: true);

        // 归属口径＝卡当前所在手牌（不设「仅己方」限制——他方手牌的弃置经调用方组合合法可达）
        await match.PlayerManager.DiscardCardAsync(playerB, card);

        Assert.DoesNotContain(card, playerB.Hand);
        Assert.False(card.Life.IsAlive);
    }
}
