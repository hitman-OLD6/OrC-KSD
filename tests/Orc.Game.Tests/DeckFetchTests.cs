using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Game.Effects;
using Orc.Game.Managers;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 卡组定向取卡（批 6·Q7）验收——四层锚点：
/// ①基础件（<see cref="CardList.RemoveInstance"/>）：引用相等匹配、单条目稳定删除（其余顺序保持、计数-1）、
///   原子（拒绝＝集合不变）、明确失败（null／不存在／重复移除抛；未装载条目不可匹配）、移除即释放卡组侧归属（可再装入）。
/// ②受控动作（<see cref="PlayerManager.FetchFromDeckAsync"/>）：完成路径（drawn→hand.add、恰一次、载荷、尾部追加）；
///   爆牌路径（满手＝drawn→销毁→burned、hand.add 零次、卡自卡组移除、被爆卡生命周期可读）；
///   失败分层（四类类别＋判定优先级＋零副作用＋失败幂等）；跨玩家拒绝（InvalidTarget）与他方卡组合法路径（落点＝归属玩家）；
///   「取出→回迁→再取出」往返一致性（实例唯一归属不变量）。
/// ③csx 受控面（<see cref="EffectRuntime.FetchFromDeckAsync"/>）：真实 csx 全链（csx 脚本装载〔DeclarePrefab＋引擎预制体库〕
///   → 随加载链装载 → 事件触发〔unit.joined〕→ handler 经受控面调用 → 游戏层可观察结果〔卡组-1／手牌+1／信号〕）；
///   多次触发幂等（第二次起 NotInDeck、零副作用）；未加载/非本局＝InvalidTarget 结果化；参数层＝异常。
/// ④门禁（Unavailable）：Mulligan（非可操作相位——等价门禁面）与终局后＝零副作用拒绝。
/// 门禁登记说明：MatchState.Preparing（准备态）与「初始化加载未完成」在结构上**不可稳定构造**（管理器群与效果运行时
/// 门面均在 Initialize 内创建、其间无切入面；组件不可达——有测试固化
/// <see cref="FetchFromDeckAsync_Preparing_State_Is_Structurally_Unreachable"/>）——按需求「允许以等价门禁面覆盖
/// 并登记说明」，以 Mulligan（统一经 IsActionAllowed 判定的非可操作相位）与终局后覆盖门禁专项；
/// PlayerManager 层的「初始化加载未完成」检查（卡牌 ID 水位线未快照）保留为实现内防御。
/// </summary>
public class DeckFetchTests
{
    private const string FetchHostId = "u_fetch_host";
    private const string FetchHostName = "侦察兵";
    private const string FetchHostPrefabId = "effect.csx.deckfetch.self";

    /// <summary>csx 制品（主场景）：「（触发条件）后从卡组抽取此牌」——宿主自我取件（经受控面）。</summary>
    private const string FetchSelfCsx = """
        Func<Orc.Cards.CardEventView, Context, CancellationToken, Task> HandleAsync = async (view, ctx, ct) =>
        {
            var self = view.Host as Orc.Cards.Card;
            if (self is null) { return; }
            var runtime = Orc.Game.Effects.EffectRuntime.ResolveFor(self);
            if (runtime is null) { return; }
            await runtime.FetchFromDeckAsync(self, ct);
        };
        """;

    // ---------- 工具 ----------

    private static UnitCard CreateUnit(LogicEngine engine, string name = "测试单位")
        => new(engine, new CardDefinition(
            name, deployCost: 1, operateCost: 1, attack: 2, defense: 2,
            unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard));

    /// <summary>侦察兵（csx 全链宿主定义；卡组名单中恰一张——从卡组装载、不被其它来源实例化）。</summary>
    private static CardDefinitionEntry FetchHostDefinition => new(
        FetchHostId,
        new CardDefinition(FetchHostName, deployCost: 1, operateCost: 1, attack: 1, defense: 2,
            unitTypes: new[] { UnitType.Infantry }, faction: Faction.Germany, rarity: Rarity.Standard));

    private static async Task FillHandToAsync(Match match, Player player, int count)
    {
        while (player.Hand.Count < count)
        {
            await CommandTestKit.InstantiateLoadedAsync(match, player, CommandTestKit.InfantryId, toHand: true);
        }
    }

    /// <summary>在玩家容器（手牌/卡组）中定位侦察兵实例（起手装载可能将其抽入——效果随卡存续，回迁后即可取件）。</summary>
    private static UnitCard FindHostCard(Player player)
    {
        foreach (var card in player.Hand)
        {
            if (card is UnitCard unit && string.Equals(unit.Definition.Name, FetchHostName, StringComparison.Ordinal))
            {
                return unit;
            }
        }

        for (var i = 0; i < player.Deck.Count; i++)
        {
            if (string.Equals(player.Deck[i], FetchHostId, StringComparison.Ordinal))
            {
                return (UnitCard)player.Deck.PeekInstance(i);
            }
        }

        throw new InvalidOperationException("未找到侦察兵宿主卡（测试装配错误）。");
    }

    /// <summary>csx 全链场景：csx 制品注册 → 初始化（随加载链装载）→ 宿主就位（起手抽入则回迁卡组顶）。</summary>
    private static async Task<(Match Match, Player PlayerA, UnitCard Host)> CreateCsxFetchSceneAsync()
    {
        var registry = new CardEffectRegistry();
        registry.DeclarePrefab(FetchHostId, new[] { FetchHostPrefabId });

        var match = new Match(
            new CardList(new[] { FetchHostId }.Concat(Enumerable.Repeat(CommandTestKit.InfantryId, 9))),
            new CardList(Enumerable.Repeat(CommandTestKit.InfantryId, 10)),
            CommandTestKit.CreateDefinitions().Concat(new[] { FetchHostDefinition }),
            seed: 42,
            options: new MatchOptions { SkipMulligan = true },
            effectRegistry: registry,
            packageActivator: TestHost.Activator);

        // 效果快照注册（initialize 前——随卡牌加载链装载；csx 求值器由 Initialize 默认装配）。
        match.Engine.Prefabs.RegisterPrefab(new EffectSnapshot(new EffectPrefab(
            FetchHostPrefabId,
            new TriggerPrefab(
                "t.fetch.self", "test.csx.deckfetch.self", true,
                hooks: new[] { GameUpdates.UnitJoined },
                events: new[] { new EventPrefab("e1", csxSource: FetchSelfCsx) }))));

        await match.Initialize();

        var playerA = match.Players[0];
        var host = FindHostCard(playerA);
        if (!playerA.Deck.ContainsInstance(host))
        {
            match.PlayerManager.ReturnToDeckTop(playerA, host); // 起手装载抽入的场合：放回卡组顶（回迁静默）
        }

        Assert.True(playerA.Deck.ContainsInstance(host));
        return (match, playerA, host);
    }

    // ==================== ① 基础件（CardList：按实例移除条目） ====================

    [Fact]
    public void CardList_RemoveInstance_Removes_Matching_Entry_Preserving_Order()
    {
        var engine = new LogicEngine();
        var list = new CardList(new[] { "c01", "c02", "c03" });
        var first = CreateUnit(engine, "甲");
        var third = CreateUnit(engine, "丙");
        list.AttachInstanceAt(0, first);
        list.AttachInstanceAt(2, third);

        list.RemoveInstance(first);

        // 单条目稳定删除：其余条目相对顺序保持、计数-1（id 读面）
        Assert.Equal(new[] { "c02", "c03" }, list.ToArray());
        Assert.Equal(2, list.Count);
        Assert.False(list.ContainsInstance(first));
        Assert.True(list.ContainsInstance(third));
        Assert.Same(third, list.PeekInstance(1)); // 实例读面一致

        Assert.Equal("c02", list.Draw()); // 名单通道：原 c02（未装载）
        Assert.Same(third, list.DrawInstance()); // 实例通道：原 c03 顺延

        // 移除即释放卡组侧归属：实例此后可再次被装入（如回迁/取卡路径）
        list.InsertInstanceAt(0, "c01", first);
        Assert.True(list.ContainsInstance(first));
        Assert.Same(first, list.PeekInstance(0));
    }

    [Fact]
    public void CardList_RemoveInstance_Rejects_Missing_And_Is_Atomic()
    {
        var engine = new LogicEngine();
        var list = new CardList(new[] { "c01" });
        var stranger = CreateUnit(engine, "外");

        Assert.Throws<ArgumentNullException>(() => list.RemoveInstance(null!));

        // 实例不存在（含：条目未装载实例——未装载条目不可匹配）＝明确失败（异常）
        Assert.Throws<InvalidOperationException>(() => list.RemoveInstance(stranger));

        // 拒绝＝集合不变（原子）——重复失败调用同断言
        Assert.Throws<InvalidOperationException>(() => list.RemoveInstance(stranger));
        Assert.Equal(new[] { "c01" }, list.ToArray());

        // 装载后移除成功；重复移除（已不在）＝明确失败、集合不变
        var loaded = CreateUnit(engine, "装载");
        list.AttachInstanceAt(0, loaded);
        list.RemoveInstance(loaded);
        Assert.Empty(list);
        Assert.Throws<InvalidOperationException>(() => list.RemoveInstance(loaded));
        Assert.Empty(list);
    }

    // ==================== ② 受控动作（PlayerManager.FetchFromDeckAsync） ====================

    [Fact]
    public async Task FetchFromDeckAsync_Moves_Target_From_Deck_To_Hand_With_Draw_Signals()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var card = playerA.Deck.PeekInstance(0); // 卡组顶实例（读取不得移除条目）
        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        var handBefore = playerA.Hand.Count;
        var deckBefore = playerA.Deck.Count;

        var result = await match.PlayerManager.FetchFromDeckAsync(playerA, card);

        // 结果对象：完成态、关联卡引用
        Assert.Equal(DeckFetchStatus.Fetched, result.Status);
        Assert.True(result.IsSuccess);
        Assert.Null(result.FailureReason);
        Assert.Same(card, result.Card);

        // 行为：卡组-1、手牌+1（尾部追加）
        Assert.Equal(handBefore + 1, playerA.Hand.Count);
        Assert.Same(card, playerA.Hand[playerA.Hand.Count - 1]);
        Assert.Equal(deckBefore - 1, playerA.Deck.Count);
        Assert.False(playerA.Deck.ContainsInstance(card));

        // 信号：drawn → hand.add（恰好一次、顺序、载荷 { 玩家, 卡牌实例 }）
        Assert.Equal(new[] { GameUpdates.CardDrawn, GameUpdates.CardHandAdd }, recorder.Types);
        var drawn = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardDrawn);
        Assert.Same(card, drawn.Payload![GameUpdates.PayloadCard]);
        Assert.Same(playerA, drawn.Payload![GameUpdates.PayloadPlayer]);
        var handAdd = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardHandAdd);
        Assert.Same(card, handAdd.Payload![GameUpdates.PayloadCard]);
        Assert.Same(playerA, handAdd.Payload![GameUpdates.PayloadPlayer]);
    }

    [Fact]
    public async Task FetchFromDeckAsync_At_Hand_Limit_Burns_With_Drawn_Then_Burned()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        await FillHandToAsync(match, playerA, Player.HandLimit);
        Assert.Equal(Player.HandLimit, playerA.Hand.Count);

        var card = playerA.Deck.PeekInstance(0);
        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        var deckBefore = playerA.Deck.Count;
        var result = await match.PlayerManager.FetchFromDeckAsync(playerA, card);

        // 结果对象：爆牌＝完成语义（对齐 DrawCard 路径「算被抽到」）
        Assert.Equal(DeckFetchStatus.Burned, result.Status);
        Assert.True(result.IsSuccess);
        Assert.Same(card, result.Card);

        // 行为：手牌全程保持上限、卡自卡组移除、被爆卡已销毁（生命周期可读）
        Assert.Equal(Player.HandLimit, playerA.Hand.Count);
        Assert.Equal(deckBefore - 1, playerA.Deck.Count);
        Assert.False(playerA.Deck.ContainsInstance(card));
        Assert.False(card.Life.IsAlive);
        Assert.DoesNotContain(card, match.Engine.Cards);

        // 信号：drawn 恰一次 → 销毁 → burned 恰一次；hand.add/discarded 零次
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardDrawn));
        Assert.Equal(1, recorder.CountOf(Updates.CardDestroyed));
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardBurned));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardDiscarded));
        Assert.Equal(
            new[] { GameUpdates.CardDrawn, Updates.CardDestroyed, GameUpdates.CardBurned },
            recorder.Types
                .Where(t => t is GameUpdates.CardDrawn or Updates.CardDestroyed or GameUpdates.CardBurned)
                .ToList());
        var burned = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardBurned);
        Assert.Same(card, burned.Payload![GameUpdates.PayloadCard]);
        Assert.Same(playerA, burned.Payload![GameUpdates.PayloadPlayer]);
    }

    [Fact]
    public async Task FetchFromDeckAsync_Rejections_Are_Structured_And_Zero_Side_Effects()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        using var recorder = new UpdateRecorder(match.Engine);

        // d1：成功取出后重复调用（已取出）→ NotInDeck；失败路径幂等零副作用、零信号
        var taken = playerA.Deck.PeekInstance(0);
        var first = await match.PlayerManager.FetchFromDeckAsync(playerA, taken);
        Assert.Equal(DeckFetchStatus.Fetched, first.Status);
        recorder.Clear();
        var handAfterFirst = playerA.Hand.Count;
        var deckAfterFirst = playerA.Deck.Count;

        var repeat = await match.PlayerManager.FetchFromDeckAsync(playerA, taken);
        var repeatAgain = await match.PlayerManager.FetchFromDeckAsync(playerA, taken);
        Assert.Equal(DeckFetchStatus.Rejected, repeat.Status);
        Assert.Equal(DeckFetchFailureReason.NotInDeck, repeat.FailureReason);
        Assert.Same(taken, repeat.Card);
        Assert.Equal(DeckFetchStatus.Rejected, repeatAgain.Status);
        Assert.Equal(DeckFetchFailureReason.NotInDeck, repeatAgain.FailureReason);
        Assert.Equal(handAfterFirst, playerA.Hand.Count);
        Assert.Equal(deckAfterFirst, playerA.Deck.Count);
        Assert.Empty(recorder.Types); // 零发射

        // d2：从未在过（已加载、卡组中无条目）→ NotInDeck
        var never = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId);
        var r2 = await match.PlayerManager.FetchFromDeckAsync(playerA, never);
        Assert.Equal(DeckFetchStatus.Rejected, r2.Status);
        Assert.Equal(DeckFetchFailureReason.NotInDeck, r2.FailureReason);

        // d3：未加载（无归属）→ InvalidTarget（结果化——不抛）
        var unloaded = match.CardLibrary.Instantiate(CommandTestKit.InfantryId);
        var r3 = await match.PlayerManager.FetchFromDeckAsync(playerA, unloaded);
        Assert.Equal(DeckFetchStatus.Rejected, r3.Status);
        Assert.Equal(DeckFetchFailureReason.InvalidTarget, r3.FailureReason);

        // d4：跨玩家（声明玩家≠卡归属玩家——参数自洽）→ InvalidTarget；零副作用
        var cardOfA = playerA.Deck.PeekInstance(0);
        var r4 = await match.PlayerManager.FetchFromDeckAsync(playerB, cardOfA);
        Assert.Equal(DeckFetchStatus.Rejected, r4.Status);
        Assert.Equal(DeckFetchFailureReason.InvalidTarget, r4.FailureReason);
        Assert.True(playerA.Deck.ContainsInstance(cardOfA));

        // d5：定义未注册于本对局（独立构造定义＋加载）→ InvalidTarget（非本局）
        var custom = CreateUnit(match.Engine, "野生单位");
        await custom.LoadAsync(playerA);
        var r5 = await match.PlayerManager.FetchFromDeckAsync(playerA, custom);
        Assert.Equal(DeckFetchStatus.Rejected, r5.Status);
        Assert.Equal(DeckFetchFailureReason.InvalidTarget, r5.FailureReason);

        // d6：已销毁（仍在卡组条目中）→ CardDestroyed
        var destroyed = playerA.Deck.PeekInstance(0);
        await match.Engine.DestroyCard(destroyed);
        var r6 = await match.PlayerManager.FetchFromDeckAsync(playerA, destroyed);
        Assert.Equal(DeckFetchStatus.Rejected, r6.Status);
        Assert.Equal(DeckFetchFailureReason.CardDestroyed, r6.FailureReason);

        // d7：已取出＋已销毁 → CardDestroyed（先生命周期、后容器归属——判定优先级「已销毁＋不在卡组」）
        var gone = playerA.Deck.PeekInstance(1); // 索引 0 为 d6 的被销毁卡（仍在条目中）——避开
        var taken2 = await match.PlayerManager.FetchFromDeckAsync(playerA, gone);
        Assert.Equal(DeckFetchStatus.Fetched, taken2.Status);
        await match.Engine.DestroyCard(gone);
        var r7 = await match.PlayerManager.FetchFromDeckAsync(playerA, gone);
        Assert.Equal(DeckFetchStatus.Rejected, r7.Status);
        Assert.Equal(DeckFetchFailureReason.CardDestroyed, r7.FailureReason);

        // 参数层＝异常（编程契约错误 fail-fast）
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => match.PlayerManager.FetchFromDeckAsync(null!, cardOfA));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => match.PlayerManager.FetchFromDeckAsync(playerA, null!));
    }

    [Fact]
    public async Task FetchFromDeckAsync_Can_Target_Opponent_Deck_Via_Owner_Declaration()
    {
        // 「操作他方卡组」合法路径：声明玩家＝卡归属玩家（≠调用者视角）→ 卡自其卡组取出 → 进**其**手牌；
        // 信号载荷玩家＝归属玩家（锁定「不设仅己方限制」＋「落点＝归属玩家」两口径——对称弃置先例）。
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerB = match.Players[1];
        var card = playerB.Deck.PeekInstance(0);
        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        var result = await match.PlayerManager.FetchFromDeckAsync(playerB, card);

        Assert.Equal(DeckFetchStatus.Fetched, result.Status);
        Assert.Contains(card, playerB.Hand); // 落点＝归属玩家（B 手牌）
        Assert.False(playerB.Deck.ContainsInstance(card));
        var drawn = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardDrawn);
        Assert.Same(playerB, drawn.Payload![GameUpdates.PayloadPlayer]); // 载荷玩家＝归属玩家
    }

    [Fact]
    public async Task Fetch_RoundTrip_Fetch_Return_Fetch_Preserves_Unique_Ownership()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var card = playerA.Deck.PeekInstance(0);

        // ① 取出（卡组 → 手牌）
        var first = await match.PlayerManager.FetchFromDeckAsync(playerA, card);
        Assert.Equal(DeckFetchStatus.Fetched, first.Status);
        Assert.Contains(card, playerA.Hand);
        Assert.False(playerA.Deck.ContainsInstance(card));

        // ② 回迁（手牌 → 卡组顶）
        match.PlayerManager.ReturnToDeckTop(playerA, card);
        Assert.DoesNotContain(card, playerA.Hand);
        Assert.True(playerA.Deck.ContainsInstance(card));

        // ③ 再取出（可重复取用——实例唯一归属不变量：任何时刻至多归属一处）
        var second = await match.PlayerManager.FetchFromDeckAsync(playerA, card);
        Assert.Equal(DeckFetchStatus.Fetched, second.Status);
        Assert.Same(card, second.Card);
        Assert.Contains(card, playerA.Hand);
        Assert.False(playerA.Deck.ContainsInstance(card));
        Assert.Single(playerA.Hand, c => ReferenceEquals(c, card)); // 至多一份（不重复、不丢失）
    }

    // ==================== ③ csx 受控面（EffectRuntime.FetchFromDeckAsync） ====================

    [Fact]
    public async Task csx效果_从卡组抽取此牌_端到端全链()
    {
        var (match, playerA, host) = await CreateCsxFetchSceneAsync();
        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        var handBefore = playerA.Hand.Count;
        var deckBefore = playerA.Deck.Count;

        // 触发：一个单位加入（unit.joined）→ 装载于宿主的 csx handler 执行 → 经受控面从卡组抽取自己。
        await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);

        // 行为断言（csx 真实执行的可观察结果）：卡组-1、手牌+1（宿主实例回手）
        Assert.Equal(deckBefore - 1, playerA.Deck.Count);
        Assert.Equal(handBefore + 1, playerA.Hand.Count);
        Assert.Contains(host, playerA.Hand);
        Assert.False(playerA.Deck.ContainsInstance(host));

        // 信号断言：drawn → hand.add（恰一次、顺序、载荷）
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardDrawn));
        Assert.Equal(1, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Equal(
            new[] { GameUpdates.CardDrawn, GameUpdates.CardHandAdd },
            recorder.Types
                .Where(t => t is GameUpdates.CardDrawn or GameUpdates.CardHandAdd)
                .ToList());
        var drawn = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardDrawn);
        Assert.Same(host, drawn.Payload![GameUpdates.PayloadCard]);
        Assert.Same(playerA, drawn.Payload![GameUpdates.PayloadPlayer]);
    }

    [Fact]
    public async Task csx效果_二次触发_第二次起NotInDeck零副作用()
    {
        var (match, playerA, host) = await CreateCsxFetchSceneAsync();

        // 第一次触发＝完成（宿主已回手）
        await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        Assert.Contains(host, playerA.Hand);

        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();
        var handCount = playerA.Hand.Count;
        var deckCount = playerA.Deck.Count;

        // 第二次触发：宿主已不在卡组 → NotInDeck（失败零信号、零副作用）
        await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 2);

        Assert.Equal(handCount, playerA.Hand.Count);
        Assert.Equal(deckCount, playerA.Deck.Count);
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardDrawn));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardBurned));
    }

    [Fact]
    public async Task FetchFromDeckAsync_Via_Runtime_Rejects_Unloaded_And_Foreign_Targets()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var anyCard = match.Players[0].Deck.PeekInstance(0);
        var runtime = EffectRuntime.ResolveFor(anyCard);
        Assert.NotNull(runtime);

        // 未加载（无归属）＝InvalidTarget（结果化——不抛）
        var unloaded = match.CardLibrary.Instantiate(CommandTestKit.InfantryId);
        var r1 = await runtime!.FetchFromDeckAsync(unloaded);
        Assert.Equal(DeckFetchStatus.Rejected, r1.Status);
        Assert.Equal(DeckFetchFailureReason.InvalidTarget, r1.FailureReason);
        Assert.Same(unloaded, r1.Card);

        // 非本局（另一对局的卡）＝InvalidTarget；零副作用（两局均不变）
        var match2 = CommandTestKit.CreateCommandMatch();
        await match2.Initialize();
        var foreign = match2.Players[0].Deck.PeekInstance(0);
        var r2 = await runtime.FetchFromDeckAsync(foreign);
        Assert.Equal(DeckFetchStatus.Rejected, r2.Status);
        Assert.Equal(DeckFetchFailureReason.InvalidTarget, r2.FailureReason);
        Assert.True(match2.Players[0].Deck.ContainsInstance(foreign));

        // 参数层＝异常
        await Assert.ThrowsAsync<ArgumentNullException>(() => runtime.FetchFromDeckAsync(null!));
    }

    [Fact]
    public void FetchFromDeckAsync_Preparing_State_Is_Structurally_Unreachable()
    {
        // 门禁登记（文件头说明的测试化证据）：准备态（MatchState.Preparing）下管理器群未创建——
        // 调用面结构不可达（访问即抛错）；准备态/初始化加载未完成的等价门禁由下方 Mulligan 与终局用例覆盖。
        var match = CommandTestKit.CreateCommandMatch();
        Assert.Equal(MatchState.Preparing, match.State);
        Assert.Throws<InvalidOperationException>(() => match.PlayerManager);
    }

    [Fact]
    public async Task FetchFromDeckAsync_Is_Rejected_By_Mulligan_Gate()
    {
        // 非可操作相位门禁（等价门禁面——见文件头登记说明）：换牌相位统一经 IsActionAllowed 判定 → Unavailable。
        var match = CommandTestKit.CreateCommandMatch(skipMulligan: false);
        await match.Initialize();
        Assert.Equal(MatchState.Mulligan, match.State);

        var playerA = match.Players[0];
        var card = playerA.Deck.PeekInstance(0);
        var runtime = EffectRuntime.ResolveFor(card);
        Assert.NotNull(runtime);

        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        var result = await runtime!.FetchFromDeckAsync(card);

        Assert.Equal(DeckFetchStatus.Rejected, result.Status);
        Assert.Equal(DeckFetchFailureReason.Unavailable, result.FailureReason);
        Assert.Same(card, result.Card);
        Assert.True(playerA.Deck.ContainsInstance(card)); // 零副作用：卡留卡组
        Assert.DoesNotContain(card, playerA.Hand);
        Assert.Empty(recorder.Types); // 零发射
    }

    [Fact]
    public async Task FetchFromDeckAsync_Is_Rejected_After_Match_End()
    {
        // 终局后门禁：动作面拒绝（Unavailable）、零副作用、状态不推进。
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var playerB = match.Players[1];
        var card = playerA.Deck.PeekInstance(0);
        var runtime = EffectRuntime.ResolveFor(card);
        Assert.NotNull(runtime);

        var concede = match.Concede(playerB); // B 认输 → 终局（胜者＝A）
        Assert.True(concede.IsAccepted);
        Assert.Equal(MatchState.Ended, match.State);

        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        var result = await runtime!.FetchFromDeckAsync(card);

        Assert.Equal(DeckFetchStatus.Rejected, result.Status);
        Assert.Equal(DeckFetchFailureReason.Unavailable, result.FailureReason);
        Assert.True(playerA.Deck.ContainsInstance(card)); // 零副作用
        Assert.DoesNotContain(card, playerA.Hand);
        Assert.Empty(recorder.Types); // 零发射
    }
}
