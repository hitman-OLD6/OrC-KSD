using Orc.Core;
using Orc.Game.Cards;
using Orc.Game.Sandbox;
using Xunit;

namespace Orc.Game.Tests.Sandbox;

/// <summary>
/// R4「原语 vs 等效效果卡」信号序列一致性（逐条对齐）：
/// 原语造一个单位 ≡ 效果侧「创建 → 放置到指定支援槽」原子组合（后者正是效果运行期的落地路径）。
/// </summary>
public sealed class SandboxPrimitiveSignalEquivalenceTests
{
    [Fact]
    public async Task Unitize_MatchesEffectCardSequence()
    {
        var primitiveMatch = await SandboxTestKit.CreateMatchAsync(seed: 777);
        var effectMatch = await SandboxTestKit.CreateMatchAsync(seed: 777);

        var primitivePlayer = primitiveMatch.Players[0];
        var effectPlayer = effectMatch.Players[0];

        var primitiveUpdates = SandboxTestKit.TrackUpdates(primitiveMatch);
        var effectUpdates = SandboxTestKit.TrackUpdates(effectMatch);

        // 原语路径：unitize → JoinUnitAsync（加入链）。
        var card = await primitiveMatch.CardService.CreateAsync(SandboxTestKit.UnitId, primitivePlayer);
        var primitiveResult = await primitiveMatch.Sandbox.UnitizeAsync(card, "support", 4);
        Assert.True(primitiveResult.Ok, primitiveResult.Reason);

        // 效果侧路径：创建 → PlaceToSupportLineAsync（效果运行期使用的同一原子组合）。
        var effectCard = await effectMatch.CardService.CreateAsync(SandboxTestKit.UnitId, effectPlayer);
        var placed = await effectMatch.CardService.PlaceToSupportLineAsync(
            (Orc.Game.Cards.UnitCard)effectCard, effectPlayer, effectMatch.Battlefield.GetSupportLine(effectPlayer)[4]);
        Assert.True(placed.IsSuccess);

        // 逐条一致（含 CreateAsync 的 card.load）：不多不少。
        Assert.Equal(effectUpdates, primitiveUpdates);
        Assert.Contains(Updates.CardPlaced, primitiveUpdates);
        Assert.Contains(GameUpdates.UnitJoined, primitiveUpdates);

        // 状态面同样一致（不在场单位 → 在场单位，位置＝指定槽）。
        var primitiveCard = (UnitCard)SandboxTestKit.Resolve(primitiveMatch, primitiveResult.Ref!)!;
        Assert.Same(
            primitiveMatch.Battlefield.GetSupportLine(primitivePlayer)[4],
            primitiveCard.GetData<UnitStateData>().Position);
        Assert.Same(
            effectMatch.Battlefield.GetSupportLine(effectPlayer)[4],
            ((Orc.Game.Cards.UnitCard)effectCard).GetData<UnitStateData>().Position);
    }
}
