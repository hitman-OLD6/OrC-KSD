using Orc.Game.Cards;
using Orc.Game.Collections;
using Orc.Game.Players;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 示例 5（R6e + R9）证据：单位「战地救护」——**友方因敌方弃牌时**从卡组抽取此牌（双向断言）。
/// 数据侧：监听 <c>card.discarded</c>，读 <c>view.Source</c> 判敌方所致，再 <c>FetchFromDeckAsync(self)</c>。
/// </summary>
public class DiyExample05_DiscardFetchTests
{
    private const string CardId = "diy.field-rescue";
    private const string CardName = "战地救护";

    [Fact]
    public async Task 战地救护_敌方弃置友方手牌_从卡组抽取自身()
    {
        if (!DiyExampleKit.TryLocatePack(out var cardsDirectory, out var effectsDirectory))
        {
            return; // 卡包缺失：跳过。
        }

        var pack = DiyExampleKit.LoadPack(cardsDirectory);
        var deck = new CardList(new[] { CardId }.Concat(Enumerable.Repeat(CommandTestKit.InfantryId, 9)));
        var match = await DiyExampleKit.CreateMatchAsync(pack, effectsDirectory, deck);
        var playerA = match.Players[0];
        var playerB = match.Players[1];

        // 宿主就位：确保在卡组中等待触发（起手装载可能抽入）。
        var rescue = FindRescue(playerA);
        if (!playerA.Deck.ContainsInstance(rescue))
        {
            match.PlayerManager.ReturnToDeckTop(playerA, rescue);
        }

        Assert.True(playerA.Deck.ContainsInstance(rescue));

        // 敌方施动卡（在场即可）+ 被弃的友方手牌。
        var enemyCard = await CommandTestKit.PrepareOnSupportAsync(match, playerB, CommandTestKit.InfantryId, 1);
        var victim = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);

        var deckBefore = playerA.Deck.Count;
        var handBefore = playerA.Hand.Count;

        // R9：弃置必须显式带 source（否则归因不可判）。
        await match.PlayerManager.DiscardCardAsync(playerA, victim, source: enemyCard);

        Assert.Equal(deckBefore - 1, playerA.Deck.Count);
        Assert.Equal(handBefore, playerA.Hand.Count); // 弃 1 抽 1
        Assert.Contains(rescue, playerA.Hand);
        Assert.False(playerA.Deck.ContainsInstance(rescue));
    }

    [Fact]
    public async Task 战地救护_己方弃置_不触发()
    {
        if (!DiyExampleKit.TryLocatePack(out var cardsDirectory, out var effectsDirectory))
        {
            return; // 卡包缺失：跳过。
        }

        var pack = DiyExampleKit.LoadPack(cardsDirectory);
        var deck = new CardList(new[] { CardId }.Concat(Enumerable.Repeat(CommandTestKit.InfantryId, 9)));
        var match = await DiyExampleKit.CreateMatchAsync(pack, effectsDirectory, deck);
        var playerA = match.Players[0];

        var rescue = FindRescue(playerA);
        if (!playerA.Deck.ContainsInstance(rescue))
        {
            match.PlayerManager.ReturnToDeckTop(playerA, rescue);
        }

        var ownCard = await CommandTestKit.PrepareOnSupportAsync(match, playerA, CommandTestKit.InfantryId, 1);
        var victim = await CommandTestKit.InstantiateLoadedAsync(match, playerA, CommandTestKit.InfantryId, toHand: true);

        var deckBefore = playerA.Deck.Count;
        await match.PlayerManager.DiscardCardAsync(playerA, victim, source: ownCard);

        Assert.Equal(deckBefore, playerA.Deck.Count);
        Assert.True(playerA.Deck.ContainsInstance(rescue));
        Assert.DoesNotContain(rescue, playerA.Hand);
    }

    private static UnitCard FindRescue(Player player)
    {
        foreach (var card in player.Hand)
        {
            if (card is UnitCard unit && string.Equals(unit.Definition.Name, CardName, StringComparison.Ordinal))
            {
                return unit;
            }
        }

        for (var i = 0; i < player.Deck.Count; i++)
        {
            if (string.Equals(player.Deck[i], CardId, StringComparison.Ordinal))
            {
                return (UnitCard)player.Deck.PeekInstance(i);
            }
        }

        throw new InvalidOperationException("未找到战地救护宿主卡（测试装配错误）。");
    }
}
