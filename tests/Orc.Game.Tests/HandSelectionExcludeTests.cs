using Orc.Cards;
using Orc.Game;
using Orc.Game.Cards;
using Orc.Game.Players;
using Orc.Game.Targeting;
using Xunit;

namespace Orc.Game.Tests;

/// <summary>
/// 选牌层（S6）证据：<c>HandSelection.PickAsync</c> 的 <c>exclude</c>——
/// **过滤发生在「min 与手牌数」比较之前**：排除后候选为空 ⇒ 自动取消（零交互、不挂起）；
/// 排除后仍有牌 ⇒ 候选集不含被排者。以及「选牌层不是防线」：不传 exclude 时施放源卡仍是合法候选
/// （真正的强制点在动作层，见 <see cref="HandTransferServiceTests"/>）。
/// </summary>
public class HandSelectionExcludeTests
{
    [Fact]
    public async Task 排除后手牌为空_自动取消_零交互()
    {
        var bridge = new MockTargeterBridge();
        var match = CommandTestKit.CreateCommandMatch(bridge: bridge);
        await match.Initialize();
        var a = match.Players[0];
        ClearHand(match, a);

        var only = await Load(match, CommandTestKit.InfantryId, a);
        var runtime = a.EffectRuntime!;

        var pick = await runtime.PickHandCardsForAsync(a, 1, 1, new[] { (Card)only });

        Assert.Equal(HandPickStatus.Cancelled, pick.Status);
        Assert.Empty(pick.Cards);
        Assert.Empty(bridge.Begins); // 零交互：根本没有发起选择请求
    }

    [Fact]
    public async Task 排除后仍有牌_候选集不含被排者()
    {
        var bridge = new MockTargeterBridge();
        bridge.InteractionScript = TargeterTestKit.AutoCompleteWithFirstAllowed();
        var match = CommandTestKit.CreateCommandMatch(bridge: bridge);
        await match.Initialize();
        var a = match.Players[0];
        ClearHand(match, a);

        var excluded = await Load(match, CommandTestKit.InfantryId, a);
        var expected = await Load(match, CommandTestKit.InfantryId, a);
        var runtime = a.EffectRuntime!;

        var pick = await runtime.PickHandCardsForAsync(a, 1, 1, new[] { (Card)excluded });

        var begin = Assert.Single(bridge.Begins);
        var candidate = Assert.Single(begin.AllowedTargets);
        Assert.Same(expected, candidate.Value);
        Assert.DoesNotContain(begin.AllowedTargets, reference => ReferenceEquals(reference.Value, excluded));

        Assert.Equal(HandPickStatus.Ok, pick.Status);
        Assert.Same(expected, Assert.Single(pick.Cards));
    }

    [Fact]
    public async Task 不传排除_施放源卡仍是候选_强制点在动作层()
    {
        var bridge = new MockTargeterBridge();
        bridge.InteractionScript = TargeterTestKit.AutoCompleteWithFirstAllowed();
        var match = CommandTestKit.CreateCommandMatch(bridge: bridge);
        await match.Initialize();
        var a = match.Players[0];
        ClearHand(match, a);

        var host = await Load(match, CommandTestKit.InfantryId, a);
        var runtime = a.EffectRuntime!;

        var pick = await runtime.PickHandCardsForAsync(a, 1, 1); // 不传 exclude

        var begin = Assert.Single(bridge.Begins);
        Assert.Contains(begin.AllowedTargets, reference => ReferenceEquals(reference.Value, host));
        Assert.Same(host, Assert.Single(pick.Cards));
    }

    [Fact]
    public async Task 既有按视角卡的选牌_签名与行为零变化()
    {
        var bridge = new MockTargeterBridge();
        bridge.InteractionScript = TargeterTestKit.AutoCompleteWithFirstAllowed();
        var match = CommandTestKit.CreateCommandMatch(bridge: bridge);
        await match.Initialize();
        var a = match.Players[0];
        ClearHand(match, a);

        var viewer = await Load(match, CommandTestKit.InfantryId, a);
        var other = await Load(match, CommandTestKit.InfantryId, a);
        var runtime = a.EffectRuntime!;

        var pick = await runtime.PickHandCardsAsync(viewer, 1, 1); // 既有重载（无 exclude）

        Assert.Equal(HandPickStatus.Ok, pick.Status);
        Assert.Same(viewer, Assert.Single(pick.Cards)); // 首张＝手牌序（未被排除）
        _ = other;
    }

    private static void ClearHand(Match match, Player player)
    {
        foreach (var card in player.Hand.ToArray())
        {
            match.PlayerManager.ReturnToDeckTop(player, (CardBase)card);
        }
    }

    private static async Task<CardBase> Load(Match match, string id, Player player)
    {
        var card = (CardBase)match.CardLibrary.Instantiate(id);
        await card.LoadAsync(player);
        player.Hand.Add(card);
        return card;
    }
}
