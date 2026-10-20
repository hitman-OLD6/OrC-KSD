using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Cards.Loading;
using Orc.Game.Collections;
using Orc.Game.Managers;
using Orc.Game.Triggers;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// S7「对局接线」验收（对局层）：卡牌加载器端口接入后的**整批装载＋实例化时序**。
/// <para>
/// 钉死的四件事：
/// <list type="bullet">
///   <item>双方牌堆＝**一次**整批装载（不是逐张、也不是两批）；</item>
///   <item>整批阶段**不分配任何实例 ID**（此时玩家管理器尚不存在——ID 提供器不可达）；</item>
///   <item>ID 水位线快照仍在校牌加载之后原位执行（批次后仍是快照前的准备态，快照值＝双方全量卡数）；</item>
///   <item>实例化路径不经过装载器（清空/关闭装载面后实例化照常）。</item>
/// </list>
/// </para>
/// </summary>
public class MatchCardLoaderWiringTests
{
    /// <summary>
    /// 记录型假加载器（端口实现）：Attach 时把「已装载定义」写进卡库（模拟资产加载器的落地），
    /// 批次只记录卡位并按 <see cref="EmptyReferences"/> 产出留空卡位。
    /// </summary>
    private sealed class RecordingCardLoader : IMatchCardLoader
    {
        private readonly Dictionary<string, CardDefinition> _definitions;
        private readonly HashSet<string> _empty;

        internal RecordingCardLoader(
            IEnumerable<CardDefinitionEntry> definitions, IEnumerable<string>? emptyReferences = null)
        {
            _definitions = definitions.ToDictionary(entry => entry.Id, entry => entry.Definition, StringComparer.Ordinal);
            _empty = new HashSet<string>(emptyReferences ?? Array.Empty<string>(), StringComparer.Ordinal);
        }

        /// <summary>每次批次的卡位清单（调用序）。</summary>
        internal List<IReadOnlyList<MatchCardSlot>> Batches { get; } = new();

        /// <summary>Attach 次数。</summary>
        internal int AttachCount { get; private set; }

        /// <summary>批次回调（测试在批次执行窗口内取证——如确认此时还没有玩家管理器）。</summary>
        internal Action? OnBatch { get; set; }

        /// <summary>关闭后任何装载调用即抛（用于证明实例化路径不经过装载面）。</summary>
        internal bool Closed { get; set; }

        public void Attach(CardLibrary library, TriggerRegistry triggers)
        {
            AttachCount++;
            foreach (var (id, definition) in _definitions)
            {
                library.Register(id, definition);
            }
        }

        public Task<CardLoadOutcome> LoadBatchAsync(
            IReadOnlyList<MatchCardSlot> slots, CancellationToken ct = default)
        {
            if (Closed)
            {
                throw new InvalidOperationException("装载面已关闭（实例化不得经过卡牌加载器）。");
            }

            Batches.Add(slots);
            OnBatch?.Invoke();

            var outcomes = slots
                .Select(slot => _empty.Contains(slot.Reference) || !_definitions.ContainsKey(slot.Reference)
                    ? CardSlotOutcome.Empty
                    : new CardSlotOutcome(CardSlotStatus.Loaded, slot.Reference))
                .ToArray();

            return Task.FromResult(new CardLoadOutcome("batch-" + Batches.Count, outcomes));
        }
    }

    private static (Match Match, RecordingCardLoader Loader) CreateMatch(
        int deckSize = GameTestData.StandardDeckSize,
        IEnumerable<string>? emptyReferences = null,
        Action<RecordingCardLoader>? configure = null)
    {
        var definitions = GameTestData.CreateDefinitions(deckSize);
        var loader = new RecordingCardLoader(definitions, emptyReferences);
        configure?.Invoke(loader);

        var match = new Match(
            GameTestData.CreateDeck(deckSize),
            GameTestData.CreateDeck(deckSize),
            definitions,
            seed: 42,
            options: new MatchOptions
            {
                SkipMulligan = true,
                // 起手张数置 0：本文件断言的是「牌堆装载」，起手抽取会缩小卡组计数（另有专测）。
                OpeningHandSizeFirstPlayer = 0,
                OpeningHandSizeSecondPlayer = 0,
            },
            packageActivator: TestHost.Activator,
            cardLoader: loader);

        return (match, loader);
    }

    [Fact]
    public async Task Both_Decks_Are_Loaded_In_A_Single_Batch()
    {
        var (match, loader) = CreateMatch(deckSize: 6);

        await match.Initialize();

        var batch = Assert.Single(loader.Batches);
        Assert.Equal(12, batch.Count); // 双方牌堆合并为一次（6＋6）
        Assert.Equal(
            Enumerable.Repeat("0", 6).Concat(Enumerable.Repeat("1", 6)),
            batch.Select(slot => slot.PlayerId));
        Assert.Equal(Enumerable.Range(0, 6).Concat(Enumerable.Range(0, 6)), batch.Select(slot => slot.Index));
        Assert.Equal(1, loader.AttachCount);

        // 双方牌堆全部实例化（装载段不发实例，实例由 LoadDecksAsync 原位创建）：卡组剩余＝牌堆全量（起手为 0）。
        Assert.Equal(6, match.Players[0].Deck.Count + match.Players[0].Hand.Count);
        Assert.Equal(6, match.Players[1].Deck.Count + match.Players[1].Hand.Count);
    }

    [Fact]
    public async Task Empty_Slot_Is_Left_Empty_And_Match_Continues()
    {
        var (match, _) = CreateMatch(deckSize: 5, emptyReferences: new[] { "c03" });

        await match.Initialize();

        // c03 判非法 ⇒ 双方牌堆的该卡位都留空（各 4 张）——开局继续。
        Assert.Equal(4, match.Players[0].Deck.Count + match.Players[0].Hand.Count);
        Assert.Equal(4, match.Players[1].Deck.Count + match.Players[1].Hand.Count);
        Assert.DoesNotContain("c03", match.Players[0].Deck);
        Assert.DoesNotContain(
            match.Players[0].Hand, card => card is CardBase concrete && concrete.Definition.Name == "卡03");

        // 实例 ID 分配随「剩余卡位 × 加载顺序」推进（空位不占号）。
        Assert.Equal(8, match.PlayerManager.CardIdWatermark);
    }

    [Fact]
    public async Task Batch_Allocates_No_Instance_Id_And_Watermark_Is_Snapshotted_After_Deck_Loading()
    {
        var (match, loader) = CreateMatch(deckSize: 8);

        PlayerManager? managerDuringBatch = null;
        var watermarkDuringBatch = -1;
        loader.OnBatch = () =>
        {
            managerDuringBatch = match.AssemblyPlayerManager;
            watermarkDuringBatch = managerDuringBatch?.CardIdWatermark ?? -1;
        };

        await match.Initialize();

        // 整批装载窗口内：玩家管理器尚未创建 ⇒ 卡牌 ID 提供器不可达 ⇒ 不可能分配实例 ID。
        Assert.Null(managerDuringBatch);

        // 批次之后：水位线已快照（＝双方全量实例化的末位 ID：8＋8），即快照仍在校牌加载之后原位执行。
        Assert.Equal(16, match.PlayerManager.CardIdWatermark);
        Assert.Equal(-1, watermarkDuringBatch);

        // 组内顺序＝洗牌后卡组序、A 组先 B 组后：所有构筑内卡 ≤ 水位线。
        foreach (var player in match.Players)
        {
            for (var i = 0; i < player.Deck.Count; i++)
            {
                Assert.False(match.IsOutsideDeck(player.Deck.PeekInstance(i)));
            }

            foreach (var card in player.Hand)
            {
                Assert.False(match.IsOutsideDeck(Assert.IsAssignableFrom<CardBase>(card)));
            }
        }

        // 快照之后的局内新增卡才是「构筑之外」——证明快照点未被前移／后移。
        var generated = await match.CardService.CreateAsync("c01", match.Players[0]);
        Assert.True(match.IsOutsideDeck(generated));
    }

    [Fact]
    public async Task In_Match_New_Card_Uses_Single_Card_Batch()
    {
        var (match, loader) = CreateMatch(deckSize: 4);
        await match.Initialize();
        loader.Batches.Clear();

        var created = await match.CardService.CreateAsync("c02", match.Players[0]);

        var batch = Assert.Single(loader.Batches);
        var slot = Assert.Single(batch);
        Assert.Equal("0", slot.PlayerId);   // 归属玩家的卡位标识
        Assert.Equal(0, slot.Index);
        Assert.Equal("c02", slot.Reference);
        Assert.Equal("卡02", created.Name);
    }

    [Fact]
    public async Task Instantiation_Never_Consults_The_Loader()
    {
        var (match, loader) = CreateMatch(deckSize: 4);
        await match.Initialize();

        // 关闭装载面：此后任何装载调用都会抛。实例化仍须成功（实例化不做任何依赖解析）。
        loader.Closed = true;

        var card = match.CardLibrary.Instantiate("c02");

        Assert.Equal("卡02", card.Name);
        Assert.Empty(loader.Batches.Skip(1)); // 只有初始化那一次批次
    }

    [Fact]
    public async Task Missing_Loader_Falls_Back_To_Code_Registered_Definitions()
    {
        // 无端口（独立装配路径）：定义集直注册——语义与接线前一致（回归护栏）。
        var match = GameTestData.CreateStandardMatch(deckSize: 8);
        await match.Initialize();

        Assert.Equal(16, match.PlayerManager.CardIdWatermark);
        Assert.True(match.CardLibrary.Contains("c01"));
    }
}
