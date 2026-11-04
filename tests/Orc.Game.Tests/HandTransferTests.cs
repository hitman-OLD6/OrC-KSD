using Orc.Cards;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Game.Managers;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 手牌转移族（R4／R5／R8）证据 —— **服务层**：<see cref="HandTransferService"/> 的两个动作
/// （交换 <c>ExchangeAsync</c> / 洗入 <c>ShuffleIntoDeckAsync</c>）的「成功／拒绝／零副作用」三态，
/// 含「执行段不可失败」的可观测形态（信号在两张牌全部落位之后、次序固定）。
/// <para>夹具：普通对局（<see cref="CommandTestKit.CreateCommandMatch"/>）＋显式装配服务（与对局装配同依赖）。</para>
/// </summary>
public class HandTransferServiceTests
{
    private const string HostId = CommandTestKit.OrderCardId;

    [Fact]
    public async Task 交换_成功_互换归属与手牌_两条手牌信号且次序固定()
    {
        var match = await CreateAsync();
        var a = match.Players[0];
        var b = match.Players[1];

        var source = await Load(match, HostId, a); // 结算中的施放源卡
        var first = await Load(match, CommandTestKit.InfantryId, a);
        var second = await Load(match, CommandTestKit.InfantryId, b);

        using var recorder = new UpdateRecorder(match.Engine);
        var service = CreateService(match);

        await service.ExchangeAsync(first, second, source);

        // 归属互换（不只是容器位置变化）＋ 实例不销毁重建
        Assert.Same(b, first.Owner);
        Assert.Same(a, second.Owner);
        Assert.True(first.Life.IsAlive);
        Assert.True(second.Life.IsAlive);

        // 双方手牌总数不变、两张均在对方手牌
        Assert.Contains(first, b.Hand);
        Assert.Contains(second, a.Hand);
        Assert.DoesNotContain(first, a.Hand);
        Assert.DoesNotContain(second, b.Hand);
        Assert.Equal(2, a.Hand.Count); // host ＋ second
        Assert.Equal(1, b.Hand.Count); // 交换前 B 手牌为空

        // 信号：恰 2 条 card.hand.add，次序 first → second；零洗切
        Assert.Equal(2, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Equal(0, recorder.CountOf(GameUpdates.DeckShuffled));
        var added = recorder.Updates
            .Where(u => u.Type == GameUpdates.CardHandAdd)
            .Select(u => (Card)u.Payload![GameUpdates.PayloadCard]!)
            .ToArray();
        Assert.Same(first, added[0]);
        Assert.Same(second, added[1]);

        // 载荷玩家＝新的归属方
        var firstSignalPlayer = (Player)recorder.Updates
            .First(u => u.Type == GameUpdates.CardHandAdd).Payload![GameUpdates.PayloadPlayer]!;
        Assert.Same(b, firstSignalPlayer);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 交换_拒绝_任一侧是施放源卡_零副作用零信号(bool sourceIsFirst)
    {
        var match = await CreateAsync();
        var a = match.Players[0];
        var b = match.Players[1];

        var host = await Load(match, HostId, a);
        var first = await Load(match, CommandTestKit.InfantryId, a);
        var second = await Load(match, CommandTestKit.InfantryId, b);
        var source = sourceIsFirst ? first : second;

        using var recorder = new UpdateRecorder(match.Engine);
        var service = CreateService(match);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExchangeAsync(first, second, source));

        Assert.Contains("B#9", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Equal(0, recorder.CountOf(GameUpdates.DeckShuffled));
        Assert.Contains(first, a.Hand);
        Assert.Contains(second, b.Hand);
        Assert.Contains(host, a.Hand);
        Assert.Same(a, first.Owner);
        Assert.Same(b, second.Owner);
    }

    [Fact]
    public async Task 交换_拒绝_两卡同属一个玩家()
    {
        var match = await CreateAsync();
        var a = match.Players[0];

        var host = await Load(match, HostId, a);
        var first = await Load(match, CommandTestKit.InfantryId, a);
        var second = await Load(match, CommandTestKit.InfantryId, a);

        using var recorder = new UpdateRecorder(match.Engine);
        var service = CreateService(match);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExchangeAsync(first, second, host));

        Assert.Contains("B#6", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Contains(first, a.Hand);
        Assert.Contains(second, a.Hand);
        Assert.Same(a, first.Owner);
        Assert.Same(a, second.Owner);
    }

    [Fact]
    public async Task 交换_拒绝_任一张不在其声明方手牌_两卡都不动()
    {
        var match = await CreateAsync();
        var a = match.Players[0];
        var b = match.Players[1];

        var host = await Load(match, HostId, a);
        var first = await Load(match, CommandTestKit.InfantryId, a);
        var second = await Load(match, CommandTestKit.InfantryId, b);

        // 人为破坏不变量：second 的归属改成 A，但仍留在 B 手牌中（归属与容器不一致）
        second.ReassignOwner(a);

        using var recorder = new UpdateRecorder(match.Engine);
        var service = CreateService(match);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExchangeAsync(first, second, host));

        Assert.Contains("B#5", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Contains(first, a.Hand); // 不是「只成一半」
        Assert.Contains(second, b.Hand);
        Assert.Same(a, first.Owner);
    }

    [Fact]
    public async Task 洗入_成功_进目标卡组并改归属_单次洗切且零手牌信号()
    {
        var match = await CreateAsync();
        var a = match.Players[0];
        var b = match.Players[1];

        var host = await Load(match, HostId, a);
        var card = await Load(match, CommandTestKit.InfantryId, a);
        var deckBefore = b.Deck.Count;

        using var recorder = new UpdateRecorder(match.Engine);
        var service = CreateService(match);

        await service.ShuffleIntoDeckAsync(card, b, host);

        Assert.Same(b, card.Owner);
        Assert.DoesNotContain(card, a.Hand);
        Assert.True(b.Deck.ContainsInstance(card));
        Assert.Equal(deckBefore + 1, b.Deck.Count);

        Assert.Equal(1, recorder.CountOf(GameUpdates.DeckShuffled));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardHandAdd)); // 装入静默
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardBurned));
        Assert.Equal(0, recorder.CountOf(GameUpdates.CardDiscarded));

        // 入组后可被正常抽到（实例通道取件；引用不变、归属已是新玩家）
        CardBase? drawn = null;
        while (b.Deck.Count > 0)
        {
            var instance = b.Deck.DrawInstance();
            if (ReferenceEquals(instance, card))
            {
                drawn = instance;
                break;
            }
        }

        Assert.Same(card, drawn);
        Assert.Same(b, drawn!.Owner);
    }

    [Fact]
    public async Task 洗入_拒绝_目标即卡当前归属()
    {
        var match = await CreateAsync();
        var a = match.Players[0];

        var host = await Load(match, HostId, a);
        var card = await Load(match, CommandTestKit.InfantryId, a);

        using var recorder = new UpdateRecorder(match.Engine);
        var service = CreateService(match);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ShuffleIntoDeckAsync(card, a, host));

        Assert.Contains("A#4", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, recorder.CountOf(GameUpdates.DeckShuffled));
        Assert.Contains(card, a.Hand);
        Assert.Same(a, card.Owner);
    }

    [Fact]
    public async Task 洗入_拒绝_目标玩家非本对局()
    {
        var match = await CreateAsync();
        var a = match.Players[0];

        var host = await Load(match, HostId, a);
        var card = await Load(match, CommandTestKit.InfantryId, a);
        var outsider = new Player(7, new CardList(), match.Engine);

        using var recorder = new UpdateRecorder(match.Engine);
        var service = CreateService(match);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ShuffleIntoDeckAsync(card, outsider, host));

        Assert.Contains("A#8", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, recorder.CountOf(GameUpdates.DeckShuffled));
        Assert.Contains(card, a.Hand);
        Assert.Same(a, card.Owner);
    }

    [Fact]
    public async Task 洗入_拒绝_被转移卡即施放源卡()
    {
        var match = await CreateAsync();
        var a = match.Players[0];
        var b = match.Players[1];

        var host = await Load(match, HostId, a);

        using var recorder = new UpdateRecorder(match.Engine);
        var service = CreateService(match);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ShuffleIntoDeckAsync(host, b, host));

        Assert.Contains("A#9", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, recorder.CountOf(GameUpdates.DeckShuffled));
        Assert.Contains(host, a.Hand);
        Assert.Same(a, host.Owner);
    }

    [Fact]
    public async Task 洗入_拒绝_定义未注册_零副作用()
    {
        var match = await CreateAsync();
        var a = match.Players[0];
        var b = match.Players[1];

        var host = await Load(match, HostId, a);

        // 未注册于本局卡库的定义（独立构造的实例）：素材解析必失败（A#10）
        var stray = new OrderCard(match.Engine, new CardDefinition(
            "局外指令", deployCost: 1, operateCost: 0, attack: 0, defense: 0,
            category: CardCategory.Command, faction: Faction.Italy, rarity: Rarity.Special));
        await stray.LoadAsync(a);
        a.Hand.Add(stray);

        using var recorder = new UpdateRecorder(match.Engine);
        var service = CreateService(match);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ShuffleIntoDeckAsync(stray, b, host));

        Assert.Contains("A#10", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, recorder.CountOf(GameUpdates.DeckShuffled));
        Assert.Contains(stray, a.Hand);
        Assert.Same(a, stray.Owner);
        Assert.False(b.Deck.ContainsInstance(stray));
    }

    [Fact]
    public async Task 门禁_对局已结束_两动作均拒绝且零副作用()
    {
        var match = await CreateAsync();
        var a = match.Players[0];
        var b = match.Players[1];

        var host = await Load(match, HostId, a);
        var first = await Load(match, CommandTestKit.InfantryId, a);
        var second = await Load(match, CommandTestKit.InfantryId, b);

        // 独立构造：状态读取器固定返回 Ended（门禁 F 的可控注入面）
        var service = new HandTransferService(
            match.Engine,
            card => match.CardService.TryResolveDefinitionId(card, out var id) ? id : null,
            (player, ct) => match.ShuffleDeckAsync(player, ct),
            () => match.Players,
            () => MatchState.Ended);

        using var recorder = new UpdateRecorder(match.Engine);

        var exchange = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExchangeAsync(first, second, host));
        Assert.Contains("B#2", exchange.Message, StringComparison.Ordinal);

        var shuffle = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ShuffleIntoDeckAsync(first, b, host));
        Assert.Contains("A#2", shuffle.Message, StringComparison.Ordinal);

        Assert.Equal(0, recorder.CountOf(GameUpdates.CardHandAdd));
        Assert.Equal(0, recorder.CountOf(GameUpdates.DeckShuffled));
        Assert.Same(a, first.Owner);
        Assert.Same(b, second.Owner);
    }

    // ---------- 辅助 ----------

    /// <summary>建局并对齐手牌：起手装载的卡全部回迁卡组（手牌清空——断言基线可预期）。</summary>
    private static async Task<Match> CreateAsync()
    {
        var match = CommandTestKit.CreateCommandMatch();
        await match.Initialize();
        foreach (var player in match.Players)
        {
            foreach (var card in player.Hand.ToArray())
            {
                match.PlayerManager.ReturnToDeckTop(player, (CardBase)card);
            }
        }

        return match;
    }

    private static HandTransferService CreateService(Match match)
        => new(
            match.Engine,
            card => match.CardService.TryResolveDefinitionId(card, out var id) ? id : null,
            (player, ct) => match.ShuffleDeckAsync(player, ct),
            () => match.Players,
            () => match.State);

    private static async Task<CardBase> Load(Match match, string id, Player player)
    {
        var card = (CardBase)match.CardLibrary.Instantiate(id);
        await card.LoadAsync(player);
        player.Hand.Add(card);
        return card;
    }
}
