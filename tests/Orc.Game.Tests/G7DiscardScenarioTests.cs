using Orc.Cards;
using Orc.Core;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Players;
using Orc.Game.Targeting;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// G7 手牌操作与弃置——组合场景级验收（v2 场景集）：
/// ①弃置三路径（选择弃〔经手牌选择槽位驱动至终局——含「选择终局产出＝被弃卡」衔接断言〕/
///   随机弃〔经随机服务取样、种子可复现〕/指定弃〔Peek 最左＋弃置动作〕；移除＋销毁＋信号）；
/// ②选择＋放回卡组顶（两步组合——不含洗切；附「指定位置回迁」最小覆盖）；
/// ③选择＋手牌内移动（组合消费级——移至最左）；
/// ④洗入类（全体＝逐张回迁→洗切恰一次；随机单张＝随机服务取样→回迁→洗切恰一次；空集＝无操作）；
/// ⑥HandLimit 爆牌（直爆口径：不经手牌、手牌全程 9；drawn → burned、hand.add 零次；销毁先于信号；不发 discarded）；
/// ⑦弃牌信号与监听消费（信号层订阅记录断言＋消费层自写监听 handler 演示）。
/// 演示定位：效果侧逻辑由测试充当消费方（不新增生产文件、不绕过受控面）；选择驱动＝桥接脚本化。
/// </summary>
public class G7DiscardScenarioTests
{
    // ==================== ① 弃置三路径 ====================

    [Fact]
    public async Task DiscardPath_Select_Via_HandSelectSlot_Then_Discard()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var target = (CardBase)playerA.Hand[1]; // 选择非最左卡（防「选 A 弃 B」类缺陷）
        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        // 两步组合：手牌选择交互（经手牌选择槽位）终局产出卡引用 → 弃置动作
        var produced = await DriveHandSelectionAsync(match, bridge, playerA, target);
        Assert.Same(target, produced); // 衔接一致性：选择终局产出＝被弃卡

        await match.PlayerManager.DiscardCardAsync(playerA, produced);

        // ① 移除（不可再使用）；② 销毁；
        Assert.DoesNotContain(target, playerA.Hand);
        Assert.False(target.Life.IsAlive);
        Assert.DoesNotContain(target, match.Engine.Cards);
        // ③ card.discarded 恰一次＋载荷正确（销毁先于信号）
        Assert.Equal(new[] { Updates.CardDestroyed, GameUpdates.CardDiscarded }, recorder.Types);
        var discarded = recorder.Updates.Single(u => u.Type == GameUpdates.CardDiscarded);
        Assert.Same(target, discarded.Payload![GameUpdates.PayloadCard]);
        Assert.Same(playerA, discarded.Payload[GameUpdates.PayloadPlayer]);
    }

    [Fact]
    public async Task DiscardPath_Random_Via_RandomService_Deterministic_And_Removes()
    {
        // 随机弃＝随机服务取样 → 弃置动作（组合式）；确定性种子下可复现
        var first = await RunRandomDiscardAsync(seed: 4242);
        var second = await RunRandomDiscardAsync(seed: 4242);
        Assert.Equal(first.Index, second.Index);
        Assert.Equal(first.Name, second.Name);
    }

    [Fact]
    public async Task DiscardPath_Designated_Leftmost_Via_Peek_Then_Discard()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var snapshot = playerA.Hand.ToArray(); // 起手 4
        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        // 指定弃（「弃掉最左」）＝顺序原子面定位（Peek 读最左）＋弃置动作
        var leftmost = (CardBase)playerA.Hand.Peek(0);
        await match.PlayerManager.DiscardCardAsync(playerA, leftmost);

        Assert.Same(snapshot[0], leftmost);
        Assert.Equal(snapshot.Skip(1), playerA.Hand); // 其余顺序保持
        Assert.False(leftmost.Life.IsAlive);
        var discarded = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardDiscarded);
        Assert.Same(leftmost, discarded.Payload![GameUpdates.PayloadCard]);
        Assert.Same(playerA, discarded.Payload[GameUpdates.PayloadPlayer]);
    }

    private static async Task<(int Index, string Name)> RunRandomDiscardAsync(int seed)
    {
        var match = CommandTestKit.CreateCommandMatch(seed: seed);
        await match.Initialize();
        var playerA = match.Players[0];
        var hand = playerA.Hand.Cast<CardBase>().ToList();
        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        var chosen = match.RandomService.PickOne(hand); // 随机服务取样（对局级确定性单流）
        var index = hand.IndexOf(chosen);

        await match.PlayerManager.DiscardCardAsync(playerA, chosen);

        // 移除＋销毁＋信号（每次运行均断言）
        Assert.DoesNotContain(chosen, playerA.Hand);
        Assert.False(chosen.Life.IsAlive);
        var discarded = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardDiscarded);
        Assert.Same(chosen, discarded.Payload![GameUpdates.PayloadCard]);
        Assert.Same(playerA, discarded.Payload[GameUpdates.PayloadPlayer]);
        return (index, chosen.Name);
    }

    // ==================== ② 选择＋放回卡组顶 ====================

    [Fact]
    public async Task Select_Then_ReturnToDeckTop_Without_Shuffle()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var target = (CardBase)playerA.Hand[2];
        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        var produced = await DriveHandSelectionAsync(match, bridge, playerA, target);
        match.PlayerManager.ReturnToDeckTop(playerA, produced); // 回迁动作（位置＝卡组顶）

        // 断言补充：卡出现在卡组顶（下次抽取取件端）＋ 无 deck.shuffled（未洗切）
        Assert.DoesNotContain(target, playerA.Hand);
        Assert.Same(target, playerA.Deck.DrawInstance());
        Assert.DoesNotContain(GameUpdates.DeckShuffled, recorder.Types);

        // 附：回迁「指定位置」最小覆盖——以索引 1 等非顶位置回迁成功、位置正确
        var second = (CardBase)playerA.Hand[0];
        match.PlayerManager.ReturnToDeck(playerA, second, 1);
        var topOut = playerA.Deck.DrawInstance(); // 原顶（非 second）
        Assert.NotSame(second, topOut);
        var nextOut = playerA.Deck.DrawInstance(); // 索引 1＝second
        Assert.Same(second, nextOut);
    }

    // ==================== ③ 选择＋手牌内移动 ====================

    [Fact]
    public async Task Select_Then_Move_Within_Hand()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge);
        await match.Initialize();
        var playerA = match.Players[0];
        var snapshot = playerA.Hand.ToArray();
        var target = (CardBase)playerA.Hand[3]; // 最右

        var produced = await DriveHandSelectionAsync(match, bridge, playerA, target);
        Assert.Same(target, produced);
        playerA.Hand.MoveToLeft(produced); // 顺序原子面「移至最左」（手牌内移动）

        Assert.Same(target, playerA.Hand[0]);
        Assert.Equal(snapshot.Take(3), playerA.Hand.Skip(1)); // 其余顺序保持
    }

    // ==================== ④ 洗入类 ====================

    [Fact]
    public async Task ShuffleIn_All_Hand_Then_Shuffle_Exactly_Once()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var handSnapshot = playerA.Hand.Cast<CardBase>().ToArray();
        Assert.Equal(4, handSnapshot.Length);
        var deckCountBefore = playerA.Deck.Count;
        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        // 「将所有手牌洗入卡组」＝逐张回迁（位置缺省）→ 洗切恰一次（调用方组合）
        await ShuffleAllHandIntoDeckAsync(match, playerA);

        Assert.Empty(playerA.Hand);
        Assert.Equal(deckCountBefore + handSnapshot.Length, playerA.Deck.Count);
        foreach (var card in handSnapshot)
        {
            Assert.True(playerA.Deck.ContainsInstance(card));
        }

        Assert.Equal(1, recorder.Types.Count(t => t == GameUpdates.DeckShuffled)); // 洗切恰一次（一条信号）
        Assert.DoesNotContain(GameUpdates.CardDiscarded, recorder.Types); // 无其它信号
    }

    [Fact]
    public async Task ShuffleIn_Random_Single_Card_Deterministic()
    {
        // 「随机把一张牌洗入」＝随机服务选一张手牌 → 回迁（缺省位置）→ 洗切恰一次；种子可复现
        var first = await RunRandomShuffleInAsync(seed: 909);
        var second = await RunRandomShuffleInAsync(seed: 909);
        Assert.Equal(first.Index, second.Index);
        Assert.Equal(first.Name, second.Name);
    }

    [Fact]
    public async Task ShuffleIn_Empty_Hand_Is_NoOp()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        // 构造空手牌（先行使逐张回迁走完——组合前置）
        foreach (var card in playerA.Hand.Cast<CardBase>().ToArray())
        {
            match.PlayerManager.ReturnToDeckTop(playerA, card);
        }

        Assert.Empty(playerA.Hand);
        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();
        var deckCountBefore = playerA.Deck.Count;

        await ShuffleAllHandIntoDeckAsync(match, playerA); // 空集分支：无操作

        Assert.Equal(deckCountBefore, playerA.Deck.Count); // 卡组计数不变
        Assert.Empty(recorder.Types); // 无 deck.shuffled、无其它信号
    }

    private static async Task<(int Index, string Name)> RunRandomShuffleInAsync(int seed)
    {
        var match = CommandTestKit.CreateCommandMatch(seed: seed);
        await match.Initialize();
        var playerA = match.Players[0];
        var hand = playerA.Hand.Cast<CardBase>().ToList();
        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        var chosen = match.RandomService.PickOne(hand);
        var index = hand.IndexOf(chosen);

        match.PlayerManager.ReturnToDeckTop(playerA, chosen);
        await match.ShuffleDeckAsync(playerA);

        Assert.DoesNotContain(chosen, playerA.Hand);
        Assert.True(playerA.Deck.ContainsInstance(chosen));
        Assert.Equal(1, recorder.Types.Count(t => t == GameUpdates.DeckShuffled));
        return (index, chosen.Name);
    }

    /// <summary>「将所有手牌洗入卡组」组合（消费方逻辑）：逐张回迁（位置缺省）→ 洗切恰一次；空集＝无操作（不洗切、不发信号）。</summary>
    private static async Task ShuffleAllHandIntoDeckAsync(Match match, Player player)
    {
        var handSnapshot = player.Hand.Cast<CardBase>().ToArray();
        if (handSnapshot.Length == 0)
        {
            return; // 空集边界
        }

        foreach (var card in handSnapshot)
        {
            match.PlayerManager.ReturnToDeckTop(player, card);
        }

        await match.ShuffleDeckAsync(player); // 装入全部完成后、洗切恰一次（既有洗切动作面——发射 deck.shuffled）
    }

    // ==================== ⑥ HandLimit 爆牌 ====================

    [Fact]
    public async Task HandLimit_Burn_Draw_At_Full_Hand_Emits_Burned_Not_Discarded()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];

        // 前置：凑满手牌 9（起手 4 ＋ 经既有加载/手牌装载面补充 5）
        while (playerA.Hand.Count < Player.HandLimit)
        {
            await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);
        }

        Assert.Equal(9, playerA.Hand.Count);

        // 全程手牌观测：drawn 信号观测点读取（不出现第 10 张）
        var handCountsAtDraw = new List<int>();
        using var probe = match.Engine.Subscribe((type, payload, _) =>
        {
            if (type == GameUpdates.CardDrawn && payload is not null
                && ReferenceEquals(payload[GameUpdates.PayloadPlayer], playerA))
            {
                handCountsAtDraw.Add(playerA.Hand.Count);
            }

            return Task.CompletedTask;
        });
        using var recorder = new UpdateRecorder(match.Engine);

        var deckBefore = playerA.Deck.Count;
        await match.EndTurn(); // 回合 2（B）：B 照抽
        recorder.Clear(); // 只保留 A 回合的更新（分段断言）
        await match.EndTurn(); // 回合 3（A）：满手 → 触发一次回合抽牌 → 爆牌

        // 手牌：终态＝9（不变）、全程＝9
        Assert.Equal(9, playerA.Hand.Count);
        Assert.Equal(9, Assert.Single(handCountsAtDraw)); // A 的 drawn 观测点：恰一次、值＝9
        // 卡组：计数＝N−1（抽取发生）
        Assert.Equal(deckBefore - 1, playerA.Deck.Count);

        // 被爆卡：不可再抽到/使用；销毁接线佐证
        var burnedUpdate = Assert.Single(recorder.Updates, u => u.Type == GameUpdates.CardBurned);
        var burned = Assert.IsAssignableFrom<CardBase>(burnedUpdate.Payload![GameUpdates.PayloadCard]);
        Assert.False(burned.Life.IsAlive);
        Assert.DoesNotContain(burned, playerA.Hand);
        Assert.False(playerA.Deck.ContainsInstance(burned));
        Assert.DoesNotContain(burned, match.Engine.Cards);

        // 信号序列（分段断言）：card.drawn 恰一次 → card.burned 恰一次（drawn 先于 burned）；
        // card.discarded 零次（反向：爆牌不走弃牌路线）；card.hand.add 零次
        var types = recorder.Types.ToList();
        Assert.Equal(1, types.Count(t => t == GameUpdates.CardDrawn));
        Assert.Equal(1, types.Count(t => t == GameUpdates.CardBurned));
        Assert.Equal(0, types.Count(t => t == GameUpdates.CardDiscarded));
        Assert.Equal(0, types.Count(t => t == GameUpdates.CardHandAdd));
        Assert.True(types.IndexOf(GameUpdates.CardDrawn) < types.IndexOf(GameUpdates.CardBurned));
        // 销毁先于 burned（引擎销毁更新先于爆牌信号）
        Assert.True(types.IndexOf(Updates.CardDestroyed) < types.IndexOf(GameUpdates.CardBurned));
        // 载荷：被爆卡＋归属玩家
        Assert.Same(playerA, burnedUpdate.Payload![GameUpdates.PayloadPlayer]);
    }

    // ==================== ⑦ 弃牌信号与监听消费 ====================

    [Fact]
    public async Task DiscardSignal_Listen_And_Consume_Via_Stateful_Handler()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        var playerA = match.Players[0];
        var card1 = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);
        var card2 = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);

        // 消费层：自写有状态监听者（挂接既有总线；私有状态闭环）
        var handler = new DiscardWatchHandler(match.Engine, playerA);
        handler.Attach();
        using var recorder = new UpdateRecorder(match.Engine);
        recorder.Clear();

        await match.PlayerManager.DiscardCardAsync(playerA, card1);
        await match.PlayerManager.DiscardCardAsync(playerA, card2);

        // 信号层：订阅记录断言——card.discarded 恰两次、载荷 { Card, Player } 正确、序（销毁先于信号）
        Assert.Equal(
            new[]
            {
                Updates.CardDestroyed, GameUpdates.CardDiscarded,
                Updates.CardDestroyed, GameUpdates.CardDiscarded,
            },
            recorder.Types);
        var discarded = recorder.Updates.Where(u => u.Type == GameUpdates.CardDiscarded).ToList();
        Assert.Same(card1, discarded[0].Payload![GameUpdates.PayloadCard]);
        Assert.Same(playerA, discarded[0].Payload![GameUpdates.PayloadPlayer]);
        Assert.Same(card2, discarded[1].Payload![GameUpdates.PayloadCard]);
        Assert.Same(playerA, discarded[1].Payload![GameUpdates.PayloadPlayer]);

        // 消费层：私有状态累计与记录、条件达成触发可观测动作（「当有牌被弃置时」类表达可承载）
        Assert.Equal(2, handler.SeenEvents);
        Assert.Equal(2, handler.DiscardCount);
        Assert.Equal(new[] { card1, card2 }, handler.Discarded.ToArray());
        Assert.Equal(1, handler.ThresholdFires); // 第 2 张被弃时条件达成恰一次
    }

    // ==================== 选择驱动辅助（桥接脚本化——既有交互设施） ====================

    /// <summary>驱动一次手牌选择交互至终局（请求构造 → 桥接应答 → 产出卡引用）；返回终局产出的卡引用。</summary>
    private static async Task<CardBase> DriveHandSelectionAsync(
        Match match, MockTargeterBridge bridge, Player player, CardBase cardToSelect)
    {
        var handSnapshot = player.Hand.Select(card => card.Ref).ToList();
        var context = new TargetingRequestContext()
            .WithSlotReferences("hand", handSnapshot)
            .WithSlotDomainValidator("hand", r => r.IsAlive && r.Value is Card card && player.Hand.Contains(card));
        var targeter = match.TargeterManager.CreateTargeter(
            slots: new TargetSlot[] { new HandSelectSlot(1, 1, "hand") },
            context: context);

        var task = targeter.Targeting();
        var (description, responder) = await bridge.WaitForNextBeginAsync();
        Assert.Equal(TargetSlotPresentation.HandSelect, Assert.Single(description.Slots).Presentation);

        var accepted = responder.Complete(
            description.RequestId, TargeterTestKit.ReferenceSelection("hand", cardToSelect.Ref));
        Assert.True(accepted, "手牌选择提交应被接受（卡仍在手牌）。");

        var result = await task;
        Assert.Equal(TargeterStatus.Ok, result.Status);
        var selected = Assert.Single(result.Outcome!.GetSelection("hand"));
        return Assert.IsAssignableFrom<CardBase>(selected.Value);
    }
}

// ==================== 场景⑦ 消费层：自写监听 handler（私有状态闭环） ====================

/// <summary>
/// card.discarded 监听 handler（场景⑦「当有牌被弃置时」）：自建被动触发器挂总线（hooks＝card.discarded）；
/// 归属过滤（指定玩家）→ 私有状态累计被弃次数与记录；条件达成（≥2 张）触发可观测计数——
/// 证明「当有牌被弃置时」类表达的消费侧可承载。
/// </summary>
internal sealed class DiscardWatchHandler
{
    private readonly LogicEngine _engine;
    private readonly Player _watchedPlayer;
    private readonly string _watchName;
    private readonly List<Card> _discarded = new();
    private IDisposable? _watch;

    public DiscardWatchHandler(LogicEngine engine, Player watchedPlayer)
    {
        _engine = engine;
        _watchedPlayer = watchedPlayer;
        _watchName = $"{watchedPlayer.Index}/弃置监听";
    }

    /// <summary>收到的 card.discarded 事件总数（过滤前；真实源联动证据）。</summary>
    public int SeenEvents { get; private set; }

    /// <summary>归属过滤后的被弃计数（私有闭环）。</summary>
    public int DiscardCount { get; private set; }

    /// <summary>被弃卡记录（归属过滤后、发生序）。</summary>
    public IReadOnlyList<Card> Discarded => _discarded;

    /// <summary>条件达成（第 2 张起）触发次数（可断言结果）。</summary>
    public int ThresholdFires { get; private set; }

    public void Attach()
        => _watch ??= _engine.Watch<DiscardWatchView>(GameUpdates.CardDiscarded, _watchName, this, OnDiscardedAsync);

    private Task OnDiscardedAsync(DiscardWatchView view, Context ctx, CancellationToken ct)
    {
        SeenEvents += 1;

        if (view.Player is not Player player || !ReferenceEquals(player, _watchedPlayer))
        {
            return Task.CompletedTask; // 归属过滤：仅累计指定玩家的弃置
        }

        if (view.Card is not Card card)
        {
            return Task.CompletedTask;
        }

        DiscardCount += 1;
        _discarded.Add(card);
        if (DiscardCount >= 2)
        {
            ThresholdFires += 1; // 条件达成 → 可观测动作（演示）
        }

        return Task.CompletedTask;
    }
}

/// <summary>card.discarded 监听视图（载荷：{ Card, Player }——被弃卡实例＋弃置时所归属的玩家）。</summary>
[ContextView]
public class DiscardWatchView
{
    [Optional]
    [Read]
    public virtual object? Card { get; set; }

    [Optional]
    [Read]
    public virtual object? Player { get; set; }
}
